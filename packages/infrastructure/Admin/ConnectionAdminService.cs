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

public sealed class ConnectionAdminService(
    DeskDbContext db,
    ISecretStore secrets,
    IAuditWriter audit,
    IConnectorResolver connectors,
    IConnectionFieldCache fieldCache,
    IObjectStorage storage,
    TimeProvider clock) : IConnectionAdminService
{
    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.PsaConnections.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier,
                c.Status, c.IsEnabled, c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt,
                // Correlated counts: one query for the page rather than three per connection.
                TicketCount = db.Tickets.Count(t => t.PsaConnectionId == c.Id),
                CustomerCount = db.ClientCompanies.Count(o => o.PsaConnectionId == c.Id),
                ContactCount = db.ClientUsers.Count(u => db.ClientCompanies
                    .Any(o => o.Id == u.ClientCompanyId && o.PsaConnectionId == c.Id)),
                c.LogoUrl,
                // The ref itself still never leaves this method — it is resolved to key NAMES below.
                c.CredentialSecretRef,
            })
            .ToListAsync(ct);

        var result = new List<ConnectionSummary>(rows.Count);
        foreach (var c in rows)
        {
            result.Add(new ConnectionSummary(
                c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier,
                c.Status, c.IsEnabled, c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt,
                c.TicketCount, c.CustomerCount, c.ContactCount, c.LogoUrl,
                StoredCredentialKeys: await StoredCredentialKeysAsync(c.CredentialSecretRef, ct)));
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

        // Secret goes to the encrypted store; only the opaque reference is persisted on the row.
        var secretRef = await secrets.WriteAsync($"{input.Provider}/{input.Name}", input.Credentials, ct);

        var connection = new PsaConnection
        {
            Name = input.Name,
            Provider = input.Provider,
            ApiEndpoint = input.ApiEndpoint,
            // ConnectWise's company id is both a credential and the name its web links route by.
            // Defaulted from the credentials so an administrator who leaves the box empty still
            // gets working "open in ConnectWise" links.
            TenantIdentifier = TenantIdentifierFor(input.Provider, input.TenantIdentifier, input.Credentials),
            CredentialSecretRef = secretRef,
            TimeZone = input.TimeZone ?? "UTC",
            LogoUrl = NormaliseLogoUrl(input.LogoUrl),
            Status = ConnectionStatus.Pending,
            IsEnabled = true,
        };
        db.PsaConnections.Add(connection);
        await db.SaveChangesAsync(ct);

        // Audit detail deliberately excludes credentials.
        await audit.WriteAsync("connection.created", "PsaConnection", connection.Id.ToString(),
            new { connection.Name, connection.Provider, connection.ApiEndpoint }, ct);

        // Fetch field options once at configure time (best-effort — creds may not be valid yet).
        await TryCacheFieldsAsync(connection.Id, ct);

        return new ConnectionSummary(connection.Id, connection.Name, connection.Provider, connection.ApiEndpoint,
            connection.TenantIdentifier, connection.Status, connection.IsEnabled, null, null,
            LogoUrl: connection.LogoUrl);
    }

    public async Task SetEnabledAsync(Guid connectionId, bool enabled, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        connection.IsEnabled = enabled;
        if (!enabled) connection.Status = ConnectionStatus.Disabled;
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
            var connector = await connectors.ResolveAsync(connectionId, ct);
            var result = await connector.TestConnectionAsync(ct);
            connection.Status = result.Success ? ConnectionStatus.Healthy : ConnectionStatus.Failed;
            connection.LastError = result.Success ? null : result.Message;
            dto = new ConnectionTestResultDto(result.Success, result.Message, result.Latency.TotalMilliseconds);
        }
        catch (ConnectorException ex)
        {
            connection.Status = ConnectionStatus.Failed;
            connection.LastError = ex.Message;
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

    private static ConnectionSummary Summarise(PsaConnection c) => new(
        c.Id, c.Name, c.Provider, c.ApiEndpoint, c.TenantIdentifier, c.Status, c.IsEnabled,
        c.LastSuccessfulSyncAt, c.LastError, c.LastHealthCheckAt, LogoUrl: c.LogoUrl);

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

        connection.Name = input.Name;
        connection.ApiEndpoint = input.ApiEndpoint;
        connection.TenantIdentifier = TenantIdentifierFor(connection.Provider, input.TenantIdentifier, input.Credentials) ?? connection.TenantIdentifier;
        connection.TimeZone = input.TimeZone ?? connection.TimeZone;
        connection.LogoUrl = NormaliseLogoUrl(input.LogoUrl);
        connection.IsEnabled = input.IsEnabled;
        if (!input.IsEnabled) connection.Status = ConnectionStatus.Disabled;

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
            // place keeps a solved problem on screen until something else happens to run. Status
            // goes back to Pending rather than Healthy — only a real successful call may claim that.
            connection.LastError = null;
            if (connection.IsEnabled && connection.Status is ConnectionStatus.Degraded or ConnectionStatus.Failed)
                connection.Status = ConnectionStatus.Pending;
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("connection.updated", "PsaConnection", connectionId.ToString(),
            new { connection.Name, connection.ApiEndpoint, CredentialsRotated = rotated }, ct);

        // Endpoint/creds may have changed — drop the cache so the next read re-discovers.
        fieldCache.Remove(connectionId);

        return new ConnectionSummary(connection.Id, connection.Name, connection.Provider, connection.ApiEndpoint,
            connection.TenantIdentifier, connection.Status, connection.IsEnabled, connection.LastSuccessfulSyncAt, connection.LastError);
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
        var connector = await connectors.ResolveAsync(connectionId, ct);
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
