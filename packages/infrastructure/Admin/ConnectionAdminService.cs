using Desk.Application.Abstractions;
using Desk.Application.Attachments;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

public sealed partial class ConnectionAdminService(
    DeskDbContext db,
    ISecretStore secrets,
    IAuditWriter audit,
    IConnectorResolver connectors,
    IConnectionFieldCache fieldCache,
    IObjectStorage storage,
    TimeProvider clock,
    Security.ConnectorEndpointPolicy? endpointPolicy = null,
    Desk.Application.Mapping.IMappingEngine? mappingEngine = null) : IConnectionAdminService
{
    private readonly Security.ConnectorEndpointPolicy _endpoints = endpointPolicy ?? Security.ConnectorEndpointPolicy.Strict;

    public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct = default) => ListAsync(archived: false, ct);

    public Task<IReadOnlyList<ConnectionSummary>> ArchivedAsync(CancellationToken ct = default) => ListAsync(archived: true, ct);

    private async Task<IReadOnlyList<ConnectionSummary>> ListAsync(bool archived, CancellationToken ct)
    {
        var rows = await db.PsaConnections.AsNoTracking()
            // Put away, not gone: an archived connection keeps everything it imported and is simply
            // not listed with the others. It has a list of its own, or it could never be restored.
            .Where(c => (c.ArchivedAt != null) == archived)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier,
                c.Status, c.IsEnabled, c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt,
                c.InSetup, c.SyncPausedAt, c.ArchivedAt, c.LastErrorKind,
                c.LogoUrl,
                // The ref itself still never leaves this method — it is resolved to key NAMES below.
                c.CredentialSecretRef,
            })
            .ToListAsync(ct);

        // What each connection holds, counted for all of them at once. As counts inside the list's
        // own query they were a pass over the tickets for every connection in it: fourteen seconds
        // at a hundred connections and half a million tickets, where one pass takes a fraction of one.
        var tickets = (await db.Tickets.AsNoTracking()
                .Where(t => t.PsaConnectionId != null)
                .GroupBy(t => t.PsaConnectionId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var customers = (await db.ClientCompanies.AsNoTracking()
                .GroupBy(o => o.PsaConnectionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var contacts = (await db.ClientUsers.AsNoTracking()
                .Join(db.ClientCompanies, u => u.ClientCompanyId, o => o.Id, (u, o) => o.PsaConnectionId)
                .GroupBy(connection => connection)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);

        var running = await RunningAsync(ct);
        var result = new List<ConnectionSummary>(rows.Count);
        foreach (var c in rows)
        {
            result.Add(new ConnectionSummary(
                c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier,
                c.Status, c.IsEnabled, c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt,
                tickets.GetValueOrDefault(c.Id), customers.GetValueOrDefault(c.Id), contacts.GetValueOrDefault(c.Id), c.LogoUrl,
                StoredCredentialKeys: await StoredCredentialKeysAsync(c.CredentialSecretRef, ct),
                State: ConnectionStates.Of(c.InSetup, c.IsEnabled, c.ArchivedAt, c.SyncPausedAt, c.LastErrorKind,
                    c.Status, c.LastSuccessfulSyncAt, running.Contains(c.Id)),
                SyncPausedAt: c.SyncPausedAt));
        }
        return result;
    }

    /// <summary>
    /// The NAMES of the credential fields that hold a non-empty stored value — never the values.
    /// An orphaned reference (the Vault-era loss) honestly reports nothing stored, which is the
    /// whole point: the edit form must be able to say "there is no existing key to keep" instead
    /// of implying one with an 'unchanged' placeholder.
    /// </summary>
    private async Task<IReadOnlyList<string>> StoredCredentialKeysAsync(string secretRef, CancellationToken ct)
    {
        try
        {
            var secret = await secrets.ReadAsync(secretRef, ct);
            return secret.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).Order().ToList();
        }
        catch (KeyNotFoundException)
        {
            return [];
        }
    }

    public async Task<ConnectionSummary> CreateAsync(CreateConnectionInput input, CancellationToken ct = default)
    {
        if (input.Credentials.Count == 0)
            throw new ValidationFailedException("At least one credential value is required.");

        // Both checked before anything is stored, the credentials included. A connection to a PSA
        // nothing here can talk to would sit enabled and fail every poll; an address that is not
        // the PSA's would be sent the credentials.
        if (!connectors.Supports(input.Provider))
            throw new ValidationFailedException($"{input.Provider} cannot be connected yet.");
        var endpoint = _endpoints.Validate(input.Provider, input.ApiEndpoint);
        var account = AccountHash(input.Provider, endpoint, input.Credentials);
        await EnsureNotAlreadyConnectedAsync(null, input.Provider, account, ct);
        await EnsureNameIsFreeAsync(null, input.Name, ct);

        // Secret goes to the encrypted store; only the opaque reference is persisted on the row.
        var secretRef = await secrets.WriteAsync($"{input.Provider}/{input.Name}", input.Credentials, ct);

        var connection = new PsaConnection
        {
            Name = input.Name,
            Provider = input.Provider,
            ApiEndpoint = endpoint,
            // ConnectWise's company id is both a credential and the name its web links route by.
            // Defaulted from the credentials so an administrator who leaves the box empty still
            // gets working "open in ConnectWise" links.
            TenantIdentifier = TenantIdentifierFor(input.Provider, input.TenantIdentifier, input.Credentials),
            CredentialSecretRef = secretRef,
            TimeZone = input.TimeZone ?? "UTC",
            LogoUrl = NormaliseLogoUrl(input.LogoUrl),
            Status = ConnectionStatus.Pending,
            // Not live yet. It used to be enabled the moment it was saved, so the scheduled sync
            // was already calling a PSA with credentials nobody had tried. It is switched on by
            // ActivateAsync, which needs a passed test.
            IsEnabled = false,
            InSetup = true,
            AccountKeyHash = account,
        };
        db.PsaConnections.Add(connection);
        await db.SaveChangesAsync(ct);

        // Audit detail deliberately excludes credentials.
        await audit.WriteAsync("connection.created", "PsaConnection", connection.Id.ToString(),
            new { connection.Name, connection.Provider, connection.ApiEndpoint }, ct);

        return Summarise(connection);
    }

    public IReadOnlyList<ProviderCatalogEntry> Providers()
    {
        var available = connectors.Providers
            .Select(d => new ProviderCatalogEntry(d.Provider, d.Name, true, d.EndpointExample, d.EndpointHint, d.TenantIdentifierLabel,
                d.Credentials.Select(c => new CredentialFieldDto(c.Key, c.Label, c.Secret, c.Hint)).ToList(), ProviderNames.Mark(d.Provider)))
            .ToList();
        // Named so that people can see what is planned. There is nothing behind one of these - no
        // connector, no form, no way to save a connection to it - and Create refuses it.
        var planned = Planned
            .Where(p => available.All(a => a.Provider != p))
            .Select(p => new ProviderCatalogEntry(p, ProviderNames.Short(p), false, null, null, null, [], ProviderNames.Mark(p)));
        return [.. available, .. planned];
    }

    /// <summary>The PSAs the portal names and has no connector for, in the order they are shown. A connector registered for one takes it off this list by itself.</summary>
    private static readonly ProviderType[] Planned =
    [
        ProviderType.HaloPsa, ProviderType.Syncro, ProviderType.SuperOps, ProviderType.Atera, ProviderType.KaseyaBms,
        ProviderType.NableMspManager, ProviderType.ServiceNow, ProviderType.Freshservice, ProviderType.JiraServiceManagement,
        ProviderType.ManageEngineServiceDeskPlus, ProviderType.Zendesk, ProviderType.ZohoDesk, ProviderType.DeskDay,
    ];

    public async Task<ProviderCapabilities> CapabilitiesAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        // What a connector can do does not depend on its credentials or on being switched on, so
        // it is asked as it stands - with nothing stored if nothing is.
        var connector = await connectors.ResolveForTrialAsync(connection, await StoredCredentialsAsync(connection, ct) ?? Empty, ct);
        return await connector.GetCapabilitiesAsync(ct);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public async Task<ConnectionSummary> ActivateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        if (connection.ArchivedAt is not null)
            throw new ValidationFailedException("This connection is archived. Restore it before switching it on.");
        if (connection.Status != ConnectionStatus.Healthy)
            throw new ValidationFailedException(
                "Test the connection first. It is switched on once the PSA has accepted its credentials.");

        // Everything else that would make it useless or harmful once on: a credential field
        // that is not stored, a scope that imports nothing, the same PSA account twice.
        if ((await PreflightAsync(connectionId, ct)).Items.FirstOrDefault(i => i.Outcome == Fail) is { } blocker)
            throw new ValidationFailedException($"Not switched on. {blocker.Name}: {blocker.Detail}");

        connection.InSetup = false;
        connection.IsEnabled = true;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("connection.activated", "PsaConnection", connectionId.ToString(), new { connection.Name }, ct);

        // Now that it may be called: fetch its field options once (best effort).
        await TryCacheFieldsAsync(connectionId, ct);
        return await SummariseAsync(connection, ct);
    }

    public async Task<ConnectionSummary> PauseSyncAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        if (connection.SyncPausedAt is null)
        {
            connection.SyncPausedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("connection.sync.paused", "PsaConnection", connectionId.ToString(), new { connection.Name }, ct);
        }
        return await SummariseAsync(connection, ct);
    }

    public async Task<ConnectionSummary> ResumeSyncAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        if (connection.SyncPausedAt is not null)
        {
            connection.SyncPausedAt = null;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("connection.sync.resumed", "PsaConnection", connectionId.ToString(), new { connection.Name }, ct);
        }
        return await SummariseAsync(connection, ct);
    }

    public async Task ArchiveAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        if (connection.ArchivedAt is not null) return;
        // Disabled and put out of sight, and that is all. Tickets, clients, mappings, links and
        // history stay exactly where they are; the stored credentials stay too, so that restoring
        // it is not the same as setting it up again.
        connection.ArchivedAt = clock.GetUtcNow();
        connection.IsEnabled = false;
        // As with any connection switched off: whatever its health was, it is not that any more.
        connection.Status = ConnectionStatus.Disabled;
        await db.SaveChangesAsync(ct);
        fieldCache.Remove(connectionId);
        await audit.WriteAsync("connection.archived", "PsaConnection", connectionId.ToString(), new { connection.Name }, ct);
    }

    public async Task<ConnectionSummary> RestoreAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        if (connection.ArchivedAt is not null)
        {
            // Another connection may have been made to the same account in the meantime.
            await EnsureNotAlreadyConnectedAsync(connection.Id, connection.Provider, connection.AccountKeyHash, ct);
            // Nor its name: another connection may have taken it while this one was put away.
            await EnsureNameIsFreeAsync(connection.Id, connection.Name, ct);
            connection.ArchivedAt = null;
            // Back, and still off: whether it should be working again is a second decision.
            connection.Status = ConnectionStatus.Disabled;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("connection.restored", "PsaConnection", connectionId.ToString(), new { connection.Name }, ct);
        }
        return await SummariseAsync(connection, ct);
    }

    private async Task<PsaConnection> FindAsync(Guid connectionId, CancellationToken ct)
        => await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
           ?? throw new NotFoundException("PSA connection");

    /// <summary>Connections with a sync in progress right now: a run recorded as running whose lease has not lapsed.</summary>
    private async Task<HashSet<Guid>> RunningAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return (await db.SyncRuns.AsNoTracking()
                .Where(r => r.Status == Desk.Domain.Sync.SyncRunStatus.Running && r.LeaseExpiresAt > now)
                .Select(r => r.PsaConnectionId)
                .ToListAsync(ct))
            .ToHashSet();
    }

    private async Task<ConnectionSummary> SummariseAsync(PsaConnection c, CancellationToken ct)
        => Summarise(c, (await RunningAsync(ct)).Contains(c.Id));

    private async Task<IReadOnlyDictionary<string, string>?> StoredCredentialsAsync(PsaConnection connection, CancellationToken ct)
    {
        try { return await secrets.ReadAsync(connection.CredentialSecretRef, ct); }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>
    /// A one-way hash of the account a connection reaches, or null when the provider cannot name
    /// one. Hashed because what names an account is a credential field, if not a secret one, and
    /// the row is not where credentials are kept.
    /// </summary>
    private string? AccountHash(ProviderType provider, string endpoint, IReadOnlyDictionary<string, string> credentials)
        => connectors.AccountKey(provider, endpoint, credentials) is { Length: > 0 } key
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{provider}|{key}")))
            : null;

    /// <summary>
    /// Refuses a second connection to a PSA account that already has one. Two connections to one
    /// account import every ticket twice, under two sets of ids, and nothing afterwards can tell
    /// which is the real one. Nothing stopped it.
    /// </summary>
    /// <summary>
    /// Saved views and the ticket list's filters know a connection by its name, so two connections
    /// with one name would be one connection to them. An archived connection does not hold its name.
    /// </summary>
    private async Task EnsureNameIsFreeAsync(Guid? self, string name, CancellationToken ct)
    {
        var wanted = name.Trim();
        var names = await db.PsaConnections.AsNoTracking()
            .Where(c => c.ArchivedAt == null && c.Id != self)
            .Select(c => c.Name)
            .ToListAsync(ct);
        if (names.Any(n => string.Equals(n.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationFailedException(
                $"Another connection is already called \"{wanted}\". Saved views and filters go by a connection's name, so each one needs its own.");
    }

    private async Task EnsureNotAlreadyConnectedAsync(Guid? self, ProviderType provider, string? account, CancellationToken ct)
    {
        if (account is null) return;
        var others = await db.PsaConnections
            .Where(c => c.Provider == provider && c.ArchivedAt == null && c.Id != self)
            .ToListAsync(ct);
        foreach (var other in others)
        {
            // A connection from before this was recorded: work its account out once, and keep it.
            if (other.AccountKeyHash is null && await StoredCredentialsAsync(other, ct) is { } stored)
                other.AccountKeyHash = AccountHash(provider, other.ApiEndpoint, stored);
            if (other.AccountKeyHash == account)
                throw new ValidationFailedException(
                    $"\"{other.Name}\" is already connected to this {provider} account. Edit that connection instead of adding a second one.");
        }
    }

    public async Task SetEnabledAsync(Guid connectionId, bool enabled, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        if (enabled && connection.ArchivedAt is not null)
            throw new ValidationFailedException("This connection is archived. Restore it before switching it on.");
        // A connection that has never been live is switched on by Activate, which needs a passed
        // test. Enabling it here would be the way round that.
        if (enabled && connection.InSetup)
            throw new ValidationFailedException("Finish setting this connection up: test it, then switch it on.");
        connection.IsEnabled = enabled;
        if (!enabled) connection.Status = ConnectionStatus.Disabled;
        // Switched back on, it is no longer disabled - and not yet known to be well either. Left
        // as it was, it went on reading "Disabled" until the next sync happened to say otherwise.
        else if (connection.Status == ConnectionStatus.Disabled) connection.Status = ConnectionStatus.Pending;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(enabled ? "connection.enabled" : "connection.disabled",
            "PsaConnection", connectionId.ToString(), null, ct);
    }

    public async Task<TimeEntryReadinessDto> CheckTimeEntryAsync(Guid connectionId, CancellationToken ct = default)
    {
        // Deliberately does not touch connection Status: a time-entry misconfiguration says nothing
        // about whether the connection itself is healthy, and marking it Failed would hide that.
        try
        {
            var connector = await connectors.ResolveAsync(connectionId, ct);
            var r = await connector.CheckTimeEntryReadinessAsync(ct);
            return new TimeEntryReadinessDto(r.Ready, r.Summary, r.Remedies, r.AvailableRoles);
        }
        catch (Exception ex) when (ex is ConnectorException or ValidationFailedException or NotFoundException)
        {
            return new TimeEntryReadinessDto(false, ex.Message, ["Fix the connection, then check again."], []);
        }
    }

    public async Task<ConnectionTestResultDto> TestAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        ConnectionTestResultDto dto;
        try
        {
            // A connection that is not switched on - one still being set up, above all - has to be
            // testable: a passed test is what lets it be switched on. Tested as it stands, with
            // what is stored.
            var connector = connection.IsEnabled
                ? await connectors.ResolveAsync(connectionId, ct)
                : await connectors.ResolveForTrialAsync(connection,
                    await StoredCredentialsAsync(connection, ct)
                    ?? throw new ValidationFailedException($"'{connection.Name}' has no valid stored credentials — edit the connection and re-enter them."), ct);
            var result = await connector.TestConnectionAsync(ct);
            connection.Status = result.Success ? ConnectionStatus.Healthy : ConnectionStatus.Failed;
            connection.LastError = result.Success ? null : result.Message;
            connection.LastErrorKind = null;
            dto = new ConnectionTestResultDto(result.Success, result.Message, result.Latency.TotalMilliseconds);
        }
        catch (ConnectorException ex)
        {
            connection.Status = ConnectionStatus.Failed;
            connection.LastError = ex.Message;
            connection.LastErrorKind = ex.Kind.ToString();
            dto = new ConnectionTestResultDto(false, $"{ex.Kind}: {ex.Message}", 0);
        }
        catch (DeskException ex)
        {
            // Thrown by connectors.ResolveAsync above — the connection is disabled, no connector is
            // registered for its provider, or its stored credentials no longer resolve (e.g. after a
            // secret-store outage). All are "test failed, here is why" outcomes for an admin to act
            // on, exactly like a ConnectorException — not a 500, and not silence.
            connection.Status = ConnectionStatus.Failed;
            connection.LastError = ex.Message;
            connection.LastErrorKind = null;
            dto = new ConnectionTestResultDto(false, ex.Message, 0);
        }

        connection.LastHealthCheckAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("connection.tested", "PsaConnection", connectionId.ToString(),
            new { dto.Success, dto.Message }, ct);

        // A successful test means creds work — refresh the field cache off the same configure action.
        if (dto.Success) await TryCacheFieldsAsync(connectionId, ct);
        return dto;
    }

    /// <summary>
    /// Accepts a site-relative path or an absolute http(s) URL, and nothing else.
    ///
    /// This value ends up as an image source in an admin's browser, so schemes like javascript: or
    /// data: must never survive storage — rejecting them here means no rendering site has to
    /// remember to. An unusable value becomes null, which falls back to the initials mark.
    /// </summary>
    /// <summary>
    /// Image types accepted for a logo.
    ///
    /// SVG is deliberately absent. An SVG can carry script, and while it cannot run inside an
    /// &lt;img&gt; tag, anyone who opens the logo URL directly gets it rendered as a document on this
    /// origin — which is stored cross-site scripting. Raster formats cannot do that, and a logo is
    /// never so detailed that PNG will not do.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedLogoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private const int MaxLogoBytes = 1024 * 1024;

    public async Task<ConnectionSummary> UploadLogoAsync(Guid connectionId, ConnectionLogoUpload upload, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        if (!AllowedLogoTypes.TryGetValue(upload.ContentType ?? "", out var extension))
            throw new ValidationFailedException("Use a PNG, JPEG, WebP or GIF image.");
        if (upload.Content.Length == 0)
            throw new ValidationFailedException("The file is empty.");
        if (upload.Content.Length > MaxLogoBytes)
            throw new ValidationFailedException("Logos must be 1 MB or smaller.");

        // Keyed by connection and stamped, so replacing a logo cannot be served from a cache that
        // still holds the previous one.
        var key = $"connection-logos/{connectionId}-{clock.GetUtcNow().ToUnixTimeMilliseconds()}{extension}";
        await storage.PutAsync(key, upload.Content, upload.ContentType!, ct);

        var previous = connection.LogoStorageKey;
        connection.LogoStorageKey = key;
        // The portal serves its own uploads; the stamp doubles as the cache-buster.
        connection.LogoUrl = $"/api/bff/api/admin/connections/{connectionId}/logo?v={clock.GetUtcNow().ToUnixTimeMilliseconds()}";
        await db.SaveChangesAsync(ct);

        // Best effort: a leftover object is untidy, never incorrect.
        if (!string.IsNullOrEmpty(previous))
            try { await storage.DeleteAsync(previous, ct); } catch { /* ignore */ }

        await audit.WriteAsync("connection.logo.updated", "PsaConnection", connectionId.ToString(),
            new { upload.FileName, upload.ContentType, Bytes = upload.Content.Length }, ct);

        return Summarise(connection);
    }

    public async Task RemoveLogoAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var key = connection.LogoStorageKey;
        connection.LogoStorageKey = null;
        connection.LogoUrl = null;
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(key))
            try { await storage.DeleteAsync(key, ct); } catch { /* ignore */ }

        await audit.WriteAsync("connection.logo.removed", "PsaConnection", connectionId.ToString(), new { }, ct);
    }

    public async Task<StoredLogo?> GetLogoAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection?.LogoStorageKey is not { Length: > 0 } key) return null;

        var bytes = await storage.GetAsync(key, ct);
        if (bytes is null) return null;

        // Serve only from the allowlist, derived from the stored key. A content type read back from
        // elsewhere could be steered into text/html and turn an image route into an HTML one.
        var extension = Path.GetExtension(key);
        var contentType = AllowedLogoTypes.FirstOrDefault(p => p.Value == extension).Key ?? "image/png";
        return new StoredLogo(bytes, contentType);
    }

    private static ConnectionSummary Summarise(PsaConnection c, bool running = false) => new(
        c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier, c.Status, c.IsEnabled,
        c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt, LogoUrl: c.LogoUrl,
        State: ConnectionStates.Of(c, running), SyncPausedAt: c.SyncPausedAt);

    public static string? NormaliseLogoUrl(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > 500) return null;

        // Site-relative, e.g. /brand/autotask.svg — but not protocol-relative //evil.example.
        if (v.StartsWith('/')) return v.StartsWith("//") ? null : v;

        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private static string? TenantIdentifierFor(ProviderType provider, string? entered, IReadOnlyDictionary<string, string>? credentials)
    {
        if (!string.IsNullOrWhiteSpace(entered)) return entered.Trim();
        if (provider == ProviderType.ConnectWisePsa && credentials is not null
            && credentials.TryGetValue("CompanyId", out var companyId) && !string.IsNullOrWhiteSpace(companyId))
            return companyId.Trim();
        return null;
    }

    public async Task<ConnectionSummary> UpdateAsync(Guid connectionId, UpdateConnectionInput input, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        // Before anything changes: an edit that fails the check must leave the connection, and the
        // credentials stored for it, exactly as they were.
        var endpoint = _endpoints.Validate(connection.Provider, input.ApiEndpoint);

        if (connection.ArchivedAt is not null)
            throw new ValidationFailedException("This connection is archived. Restore it before changing it.");

        // What the connection would hold after this edit: the stored credentials with the typed
        // fields over them (a blank field means "keep what is stored").
        var typed = input.Credentials is { Count: > 0 } ? input.Credentials : null;
        var stored = await StoredCredentialsAsync(connection, ct);
        var candidate = stored is null ? null : new Dictionary<string, string>(stored);
        if (typed is not null)
        {
            candidate ??= [];
            foreach (var (key, value) in typed) candidate[key] = value;
        }

        // A new address or new credentials are TRIED before they are kept. They used to be saved
        // first: a mistyped key replaced a working one, and a changed address had the stored
        // credentials sent to it before anything had checked it was the PSA. A connection still
        // being set up is the exception - it has nothing working to protect, and is tested before
        // it can be switched on in any case.
        var addressChanged = !string.Equals(connection.ApiEndpoint, endpoint, StringComparison.OrdinalIgnoreCase);
        var verified = false;
        if (!connection.InSetup && candidate is not null && (typed is not null || addressChanged))
        {
            var before = connection.ApiEndpoint;
            connection.ApiEndpoint = endpoint;
            try
            {
                var trial = await connectors.ResolveForTrialAsync(connection, candidate, ct);
                var outcome = await trial.TestConnectionAsync(ct);
                if (!outcome.Success)
                    throw new ConnectorException(ConnectorFailureKind.ProviderError, outcome.Message ?? "The test did not pass.");
                verified = true;
            }
            catch (ConnectorException ex)
            {
                connection.ApiEndpoint = before;
                throw new ValidationFailedException(
                    $"Not saved: {connection.Provider} did not accept {(typed is not null ? "the new credentials" : "the new address")} ({ex.Kind}: {ex.Message}). "
                    + "The connection is unchanged and still uses what it had.");
            }
        }

        var account = candidate is null ? connection.AccountKeyHash : AccountHash(connection.Provider, endpoint, candidate);
        await EnsureNotAlreadyConnectedAsync(connection.Id, connection.Provider, account, ct);
        // Only when the name is being changed: two connections that already share one (from
        // before this rule) can still have everything else about them edited.
        if (!string.Equals(connection.Name.Trim(), input.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            await EnsureNameIsFreeAsync(connection.Id, input.Name, ct);

        connection.Name = input.Name;
        connection.ApiEndpoint = endpoint;
        connection.AccountKeyHash = account;
        connection.TenantIdentifier = TenantIdentifierFor(connection.Provider, input.TenantIdentifier, input.Credentials) ?? connection.TenantIdentifier;
        connection.TimeZone = input.TimeZone ?? connection.TimeZone;
        connection.LogoUrl = NormaliseLogoUrl(input.LogoUrl);
        // A connection still in setup is switched on by Activate and by nothing else.
        if (!connection.InSetup)
        {
            connection.IsEnabled = input.IsEnabled;
            if (!input.IsEnabled) connection.Status = ConnectionStatus.Disabled;
        }

        // Rotate credentials only if new ones were supplied. The store returns the reference the
        // secret now lives at — usually the same one, but a freshly minted one when the old
        // reference could not be reused (a Vault-era connection whose secret this backend never
        // held). Assigning it back repoints the row in the same save, instead of leaving it naming
        // something that cannot be read.
        var rotated = input.Credentials is { Count: > 0 };
        if (rotated)
        {
            // The form sends only the fields the admin typed; a blank field means "keep what's
            // stored" — the edit screen says so in as many words. Rotating with just the typed
            // fields would make that promise false by replacing the whole secret: re-entering only
            // the Secret would silently drop ApiIntegrationCode and UserName. Merge over whatever
            // exists so blank truly means keep.
            var credentials = input.Credentials!;
            try
            {
                var merged = new Dictionary<string, string>(await secrets.ReadAsync(connection.CredentialSecretRef, ct));
                foreach (var (key, value) in credentials) merged[key] = value;
                credentials = merged;
            }
            catch (KeyNotFoundException)
            {
                // Nothing stored (the Vault-era loss) — the typed fields are the whole secret.
            }

            connection.CredentialSecretRef =
                await secrets.RotateAsync(connection.CredentialSecretRef, credentials, ct);

            // The recorded failure describes the credentials that were just replaced. Leaving it in
            // place keeps a solved problem on screen until something else happens to run.
            connection.LastError = null;
            connection.LastErrorKind = null;
            if (connection.IsEnabled && connection.Status is ConnectionStatus.Degraded or ConnectionStatus.Failed)
                connection.Status = ConnectionStatus.Pending;
        }

        // The PSA has just accepted exactly what is being saved: that is a passed test.
        if (verified && connection.IsEnabled)
        {
            connection.Status = ConnectionStatus.Healthy;
            connection.LastError = null;
            connection.LastErrorKind = null;
            connection.LastHealthCheckAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("connection.updated", "PsaConnection", connectionId.ToString(),
            new { connection.Name, connection.ApiEndpoint, CredentialsRotated = rotated }, ct);

        // Endpoint/creds may have changed — drop the cache so the next read re-discovers.
        fieldCache.Remove(connectionId);

        return await SummariseAsync(connection, ct);
    }

    /// <summary>Returns cached field options if present (populated at configure time), else discovers
    /// live once and caches. Use <see cref="RefreshFieldsAsync"/> to force a fresh pull.</summary>
    public async Task<ConnectionFieldsDto> GetFieldsAsync(Guid connectionId, CancellationToken ct = default)
    {
        _ = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        if (fieldCache.Get(connectionId) is { } cached) return cached;
        var fields = await DiscoverAsync(connectionId, ct);
        fieldCache.Set(connectionId, fields);
        return fields;
    }

    public async Task<ConnectionSettingsDto> GetSettingsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var c = await db.PsaConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        return ToSettings(c);
    }

    public async Task<ConnectionSettingsDto> SaveSettingsAsync(Guid connectionId, ConnectionSettingsDto input, CancellationToken ct = default)
    {
        var c = await db.PsaConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        if (!input.ImportOpenTickets && !input.ImportClosedTickets)
            throw new ValidationFailedException("Select at least one of open or closed tickets to import.");
        if (input.FilterActiveWithinDays is < 0)
            throw new ValidationFailedException("Active-within days cannot be negative.");

        c.TwoWaySync = input.TwoWaySync;
        c.AutoImportNewTickets = input.AutoImportNewTickets;
        // Note import only makes sense when provider changes flow back at all.
        c.ImportNotes = input.ImportNotes && input.TwoWaySync;
        c.ImportSystemNotes = input.ImportSystemNotes && c.ImportNotes;
        c.SyncAttachments = input.SyncAttachments;
        c.ImportOpenTickets = input.ImportOpenTickets;
        c.ImportClosedTickets = input.ImportClosedTickets;
        c.FilterCompanyIds = Clean(input.FilterCompanyIds);
        c.FilterQueueIds = Clean(input.FilterQueueIds);
        c.FilterResourceIds = Clean(input.FilterResourceIds);
        c.FilterActiveWithinDays = input.FilterActiveWithinDays is > 0 ? input.FilterActiveWithinDays : null;
        c.DefaultQueueOrBoardId = Clean(input.DefaultQueueOrBoardId);
        c.DefaultTicketType = Clean(input.DefaultTicketType);
        c.DefaultIssueType = Clean(input.DefaultIssueType);
        c.DefaultSubIssueType = Clean(input.DefaultSubIssueType);
        c.DefaultTimeEntryResourceId = Clean(input.DefaultTimeEntryResourceId);
        c.DefaultTimeEntryRoleId = Clean(input.DefaultTimeEntryRoleId);

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("connection.settings.updated", "PsaConnection", connectionId.ToString(),
            new { c.TwoWaySync, c.AutoImportNewTickets, c.ImportOpenTickets, c.ImportClosedTickets }, ct);
        return ToSettings(c);
    }

    private static ConnectionSettingsDto ToSettings(Desk.Domain.Tenancy.PsaConnection c) => new(
        c.TwoWaySync, c.AutoImportNewTickets, c.ImportNotes, c.ImportSystemNotes, c.SyncAttachments,
        c.ImportOpenTickets, c.ImportClosedTickets,
        c.FilterCompanyIds, c.FilterQueueIds, c.FilterResourceIds, c.FilterActiveWithinDays,
        c.DefaultQueueOrBoardId, c.DefaultTicketType, c.DefaultIssueType, c.DefaultSubIssueType,
        c.DefaultTimeEntryResourceId, c.DefaultTimeEntryRoleId);

    /// <summary>Normalizes a comma-separated id list; empty becomes null (= no restriction).</summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(",", parts);
    }

    public async Task<ConnectionFieldsDto> RefreshFieldsAsync(Guid connectionId, CancellationToken ct = default)
    {
        _ = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        var fields = await DiscoverAsync(connectionId, ct);
        fieldCache.Set(connectionId, fields);
        await audit.WriteAsync("connection.fields.refreshed", "PsaConnection", connectionId.ToString(), null, ct);
        return fields;
    }

    // Live discovery from the connected PSA. Individual lookups degrade to empty rather than
    // failing the whole request if the provider doesn't support one.
    private async Task<ConnectionFieldsDto> DiscoverAsync(Guid connectionId, CancellationToken ct)
    {
        // Also for a connection still being set up: its queues, statuses and priorities are what
        // its mapping and its scope are chosen from, before it is switched on.
        var connector = await ConnectorForAsync(connectionId, ct);
        var boards = await SafeAsync(() => connector.GetQueuesOrBoardsAsync(ct));
        var statuses = await SafeAsync(() => connector.GetStatusesAsync(ct));
        var priorities = await SafeAsync(() => connector.GetPrioritiesAsync(ct));
        var categories = await SafeAsync(() => connector.GetCategoriesAsync(ct));
        var workTypes = await SafeAsync(() => connector.GetWorkTypesAsync(ct));
        var workRoles = await SafeAsync(() => connector.GetWorkRolesAsync(ct));
        // Only active technicians: an admin picking a default should not be offered a leaver.
        var technicians = await SafeTechniciansAsync(connector, ct);
        var coverage = await SafeCoverageAsync(connector, ct);
        return new ConnectionFieldsDto(
            Map(boards), Map(statuses), Map(priorities), Map(categories), Map(workTypes), Map(workRoles),
            technicians, coverage);
    }

    private static async Task<IReadOnlyList<FieldOptionDto>> SafeTechniciansAsync(IServiceManagementConnector connector, CancellationToken ct)
    {
        try
        {
            var techs = await connector.GetTechniciansAsync(ct);
            return techs.Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.DisplayName))
                // Technicians are referenced by id on both sides — nothing maps them, so the two
                // representations are the same value.
                .Select(t => new FieldOptionDto(t.ExternalId, t.DisplayName, t.ExternalId))
                .ToList();
        }
        catch (ConnectorException) { return []; }
    }

    private static async Task<IReadOnlyList<TechnicianCoverageDto>> SafeCoverageAsync(IServiceManagementConnector connector, CancellationToken ct)
    {
        try
        {
            var rows = await connector.GetTechnicianAssignmentsAsync(ct);
            return rows.Select(a => new TechnicianCoverageDto(
                a.TechnicianExternalId, a.RoleId, a.RoleName, a.QueueOrBoardId)).ToList();
        }
        catch (ConnectorException) { return []; }
    }

    private async Task TryCacheFieldsAsync(Guid connectionId, CancellationToken ct)
    {
        try { fieldCache.Set(connectionId, await DiscoverAsync(connectionId, ct)); }
        catch { /* creds may be invalid/unreachable at configure time — refresh later */ }
    }

    /// <summary>
    /// Active options only. A RETIRED picklist value still comes back from the provider's field
    /// metadata, so it used to sit in the mapping dropdown looking exactly like a live one — it
    /// saved without complaint and was rejected on every write afterwards, which is how a portal
    /// status ends up mapped to something the PSA no longer accepts.
    /// </summary>
    private static IReadOnlyList<FieldOptionDto> Map(IReadOnlyList<Desk.PsaCore.Models.ExternalFieldOption> options)
        => options.Where(o => o.IsActive).Select(o => new FieldOptionDto(o.Value, o.Label, o.SyncValue)).ToList();

    private static async Task<IReadOnlyList<Desk.PsaCore.Models.ExternalFieldOption>> SafeAsync(
        Func<Task<IReadOnlyList<Desk.PsaCore.Models.ExternalFieldOption>>> fetch)
    {
        try { return await fetch(); }
        catch (ConnectorException) { return []; }
    }
}
