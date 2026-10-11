using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Infrastructure.Sync;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// What an administrator is shown while a connection is being set up, before it is switched on:
/// what the PSA allowed when it was tried, how its values map, how much an import would bring in,
/// and whether anything stands in the way of switching it on.
///
/// All of it reads. None of it writes to the PSA: a test that changed a ticket to prove it could
/// would be a test nobody dared run against production.
/// </summary>
public sealed partial class ConnectionAdminService
{
    public const string Pass = "Pass";
    public const string Fail = "Fail";
    public const string Warn = "Warn";
    public const string NotTested = "NotTested";
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";

    private readonly IMappingEngine _mapping = mappingEngine ?? new MappingEngine();

    /// <summary>What follows authentication, for the report of a test that got no further.</summary>
    private static readonly (string Key, string Name, bool Required)[] AfterAuthentication =
    [
        ("tickets.read", "Read tickets", true),
        ("configuration.read", "Read statuses, priorities and queues", true),
        ("technicians.read", "Read technicians", false),
        ("notes.read", "Read ticket notes", false),
        ("time.read", "Read time entries", false),
        ("time.write", "Log time", false),
        ("tickets.write", "Update tickets and add notes", false),
        ("webhooks", "Webhooks", false),
    ];

    /// <summary>
    /// A connector for a connection as it stands. One still being set up is not switched on, and
    /// has to be read from all the same: what its PSA offers is what its setup is decided on.
    /// </summary>
    /// <summary>The PSA's custom ticket fields, for a connection that is switched on or still being set up.</summary>
    public async Task<(bool Supported, IReadOnlyList<ExternalFieldDefinition> Fields)> ListCustomFieldsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connector = await ConnectorForAsync(connectionId, ct);
        return (await connector.GetCapabilitiesAsync(ct)).SupportsCustomFields
            ? (true, await connector.GetCustomFieldsAsync(ct))
            : (false, []);
    }

    private async Task<IServiceManagementConnector> ConnectorForAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await db.PsaConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        if (connection.IsEnabled || !connection.InSetup) return await connectors.ResolveAsync(connectionId, ct);
        return await connectors.ResolveForTrialAsync(connection,
            await StoredCredentialsAsync(connection, ct)
            ?? throw new ValidationFailedException($"'{connection.Name}' has no valid stored credentials — edit the connection and re-enter them."), ct);
    }

    public async Task<ConnectionCheckReportDto> CheckAsync(Guid connectionId, CancellationToken ct = default)
    {
        var checks = new List<ConnectionCheckDto>();

        // The first line is the test a connection has always had. It records its outcome on the
        // connection and is audited, so "tested" does not come to mean two things.
        var test = await TestAsync(connectionId, ct);
        checks.Add(new("authentication", "Authentication", test.Success ? Pass : Fail, test.Success ? null : test.Message, true));
        if (!test.Success)
        {
            // Nothing else can be learned with credentials the PSA did not accept.
            checks.AddRange(AfterAuthentication.Select(c =>
                new ConnectionCheckDto(c.Key, c.Name, NotTested, "Not tried: the PSA did not accept the connection.", c.Required)));
            return new ConnectionCheckReportDto(false, clock.GetUtcNow(), checks);
        }

        var connector = await ConnectorForAsync(connectionId, ct);
        var capabilities = await connector.GetCapabilitiesAsync(ct);
        // Failures that may not be there next time: a timeout, a rate limit, a PSA having a bad minute.
        var mayPassNextTime = new HashSet<string>();

        async Task<ConnectionCheckDto> TryAsync(string key, string name, bool required, Func<Task<string?>> attempt)
        {
            try
            {
                return new(key, name, Pass, await attempt(), required);
            }
            catch (ConnectorException ex)
            {
                if (ex.IsTransient) mayPassNextTime.Add(key);
                return new(key, name, Fail, $"{ex.Kind}: {ex.Message}", required);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not the PSA's answer, and not written for an administrator to read.
                return new(key, name, Fail, "The check could not be completed.", required);
            }
        }

        UnifiedTicket? sample = null;
        checks.Add(await TryAsync("tickets.read", "Read tickets", true, async () =>
        {
            sample = (await connector.GetTicketsAsync(new TicketFilter { PageSize = 1 }, ct)).Items.FirstOrDefault();
            return sample is null ? "The PSA answered. It holds no tickets yet." : null;
        }));
        checks.Add(await TryAsync("configuration.read", "Read statuses, priorities and queues", true, async () =>
        {
            var statuses = await connector.GetStatusesAsync(ct);
            var priorities = await connector.GetPrioritiesAsync(ct);
            var queues = await connector.GetQueuesOrBoardsAsync(ct);
            return $"{statuses.Count} statuses, {priorities.Count} priorities, {queues.Count} queues or boards.";
        }));
        checks.Add(await TryAsync("technicians.read", "Read technicians", false,
            async () => $"{(await connector.GetTechniciansAsync(ct)).Count(t => t.IsActive)} active."));

        // Notes and time hang off a ticket. With none to ask about there is nothing to try, and
        // "pass" would be a guess.
        checks.Add(sample is null
            ? new("notes.read", "Read ticket notes", NotTested, "No ticket to try it on.", false)
            : await TryAsync("notes.read", "Read ticket notes", false, async () =>
            {
                await connector.GetNotesAsync(sample.ExternalId, ct);
                return null;
            }));

        if (!capabilities.SupportsTimeEntries)
        {
            checks.Add(new("time.read", "Read time entries", Unavailable, "This PSA's connector does not read time.", false));
            checks.Add(new("time.write", "Log time", Unavailable, "This PSA's connector does not log time.", false));
        }
        else
        {
            checks.Add(sample is null
                ? new("time.read", "Read time entries", NotTested, "No ticket to try it on.", false)
                : await TryAsync("time.read", "Read time entries", false, async () =>
                {
                    await connector.GetTimeEntriesAsync(sample.ExternalId, ct);
                    return null;
                }));
            // Whether logging time WOULD work, found by reading what the PSA needs for it.
            try
            {
                var ready = await connector.CheckTimeEntryReadinessAsync(ct);
                checks.Add(new("time.write", "Log time", ready.Ready ? Pass : Warn, ready.Summary, false));
            }
            catch (ConnectorException ex)
            {
                checks.Add(new("time.write", "Log time", Fail, $"{ex.Kind}: {ex.Message}", false));
            }
        }

        // A test never writes to the PSA. What a write needs is said, not pretended.
        checks.Add(new("tickets.write", "Update tickets and add notes",
            capabilities.SupportsTicketUpdate ? NotTested : Unavailable,
            capabilities.SupportsTicketUpdate
                ? "The connector can do it. Not tried: a test never changes anything in your PSA."
                : "This PSA's connector does not write to tickets.", false));
        checks.Add(new("webhooks", "Webhooks",
            capabilities.SupportsInboundWebhooks ? Available : Unavailable,
            capabilities.SupportsInboundWebhooks
                ? "The PSA can tell the portal about a change as it happens."
                : "Changes are picked up by polling.", false));

        var blockers = checks.Where(c => c.Required && c.Outcome != Pass).ToList();
        if (blockers.Count > 0)
        {
            // Authentication passed and a read the sync cannot do without did not. Left "Healthy",
            // the connection could be switched on and would then fail every run. A LIVE connection
            // is spared when the failure may not be there next time: stopping a working sync over
            // one timeout would be the test doing damage.
            var connection = await FindAsync(connectionId, ct);
            if (connection.InSetup || blockers.Any(b => !mayPassNextTime.Contains(b.Key)))
            {
                connection.Status = ConnectionStatus.Failed;
                connection.LastError = $"{blockers[0].Name}: {blockers[0].Detail}";
                await db.SaveChangesAsync(ct);
            }
        }

        await audit.WriteAsync("connection.checked", "PsaConnection", connectionId.ToString(),
            new { passed = blockers.Count == 0, checks = checks.ToDictionary(c => c.Key, c => c.Outcome) }, ct);
        return new ConnectionCheckReportDto(blockers.Count == 0, clock.GetUtcNow(), checks);
    }

    public async Task<MappingCoverageDto> MappingCoverageAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var fields = await GetFieldsAsync(connectionId, ct);
        var rules = await ConnectionMappingRules.LoadAsync(db, connection.MspOrganizationId, connection.Provider, connection.Id, ct);
        var context = new MappingContext { Provider = connection.Provider, PsaConnectionId = connection.Id };

        List<MappedValueDto> Of(string field, IReadOnlyList<FieldOptionDto> options) => options
            .Select(o =>
            {
                // By the value tickets ARRIVE with: that is what a rule is compared against, and it
                // is not always the value the PSA lists the option under.
                var result = _mapping.MapToPortal(rules, context, field, o.SyncValue);
                return new MappedValueDto(o.SyncValue, o.Label, result.Resolved ? result.Value : null, result.Resolved && result.UsedFallback);
            })
            .ToList();

        var statuses = Of("status", fields.Statuses);
        var priorities = Of("priority", fields.Priorities);
        return new MappingCoverageDto(statuses, priorities,
            statuses.Count(s => s.MapsTo is null) + priorities.Count(p => p.MapsTo is null));
    }

    public async Task<ConnectionPreviewDto> PreviewAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var connector = await ConnectorForAsync(connectionId, ct);
        var notes = new List<string>();

        async Task<int?> CountAsync(string what, Func<Task<int?>> count)
        {
            try
            {
                return await count();
            }
            catch (ConnectorException ex)
            {
                notes.Add($"{what} could not be counted ({ex.Kind}).");
                return null;
            }
        }

        var companies = Ids(connection.FilterCompanyIds);
        var clients = await CountAsync("Clients", async () =>
            (await connector.GetOrganizationsAsync(ct)).Count(o => o.IsActive && (companies.Count == 0 || companies.Contains(o.ExternalId))));
        var technicians = await CountAsync("Technicians", async () => (await connector.GetTechniciansAsync(ct)).Count(t => t.IsActive));

        // The filter the sync itself would send, so the number is of the tickets it would be given.
        var scope = new TicketFilter
        {
            CompanyIds = companies,
            QueueOrBoardIds = Ids(connection.FilterQueueIds),
            AssignedResourceIds = Ids(connection.FilterResourceIds),
            ActiveWithinDays = connection.FilterActiveWithinDays,
        };
        var open = await CountAsync("Open tickets", () => connector.CountTicketsAsync(scope with { IncludeClosed = false }, ct));
        var all = await CountAsync("Tickets", () => connector.CountTicketsAsync(scope with { IncludeClosed = true }, ct));
        if (open is null && all is null && notes.Count == 0)
            notes.Add("This PSA cannot say how many tickets there are before they are read.");

        int? toImport = (connection.ImportOpenTickets, connection.ImportClosedTickets) switch
        {
            (true, true) => all,
            (true, false) => open,
            (false, true) => all - open,
            _ => 0,
        };

        notes.Add("Contacts are read with each client during the sync and are not counted beforehand.");
        var unmapped = (await MappingCoverageAsync(connectionId, ct)).Unmapped;
        return new ConnectionPreviewDto(clients, technicians, open, all, toImport, unmapped, notes);
    }

    public async Task<PreflightDto> PreflightAsync(Guid connectionId, CancellationToken ct = default)
    {
        var connection = await FindAsync(connectionId, ct);
        var items = new List<PreflightItemDto>();
        var tested = connection.Status == ConnectionStatus.Healthy;

        items.Add(tested
            ? new("connection", "Connection", Pass, "The PSA accepted the credentials and the reads the sync needs worked.")
            : new("connection", "Connection", Fail, string.IsNullOrWhiteSpace(connection.LastError)
                ? "It has not passed a test yet."
                : $"Its last test did not pass: {connection.LastError.Trim()}"));

        // Every field the connector asks for, held in the secret store.
        var wanted = connectors.Providers.FirstOrDefault(p => p.Provider == connection.Provider)?.Credentials ?? [];
        var stored = await StoredCredentialsAsync(connection, ct);
        var missing = wanted
            .Where(f => stored is null || !stored.TryGetValue(f.Key, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(f => f.Label)
            .ToList();
        items.Add(missing.Count == 0
            ? new("credentials", "Credentials", Pass, "Every credential the connector needs is stored.")
            : new("credentials", "Credentials", Fail, $"Not stored: {string.Join(", ", missing)}."));

        // What the PSA offers is only known once it has let the portal in.
        var fields = tested ? await GetFieldsAsync(connectionId, ct) : null;

        if (fields is null)
            items.Add(new("mapping", "Mapping", NotTested, "Checked once the connection has passed its test."));
        else
        {
            var coverage = await MappingCoverageAsync(connectionId, ct);
            var unmapped = coverage.Statuses.Concat(coverage.Priorities).Where(v => v.MapsTo is null).Select(v => v.Label).ToList();
            items.Add(unmapped.Count == 0
                ? new("mapping", "Mapping", Pass, "Every status and priority this PSA lists has a mapping.")
                : new("mapping", "Mapping", Warn,
                    $"{unmapped.Count} {(unmapped.Count == 1 ? "value has" : "values have")} no mapping and will arrive as the PSA sends "
                    + $"{(unmapped.Count == 1 ? "it" : "them")}: {string.Join(", ", unmapped.Take(6))}{(unmapped.Count > 6 ? ", …" : "")}."));
        }

        items.Add(Scope(connection, fields));

        // A second connection to the same PSA account would import every ticket twice.
        try
        {
            await EnsureNotAlreadyConnectedAsync(connection.Id, connection.Provider, connection.AccountKeyHash, ct);
            var sameName = await db.PsaConnections.AsNoTracking()
                .AnyAsync(c => c.Id != connection.Id && c.ArchivedAt == null && c.Name == connection.Name, ct);
            items.Add(sameName
                ? new("conflicts", "Conflicts", Warn, $"Another connection is also called \"{connection.Name}\". Saved views and filters go by name; give this one its own.")
                : new("conflicts", "Conflicts", Pass, "None."));
        }
        catch (ValidationFailedException ex)
        {
            items.Add(new("conflicts", "Conflicts", Fail, ex.Message));
        }

        return new PreflightDto(items.All(i => i.Outcome != Fail), items);
    }

    private static PreflightItemDto Scope(Desk.Domain.Tenancy.PsaConnection connection, ConnectionFieldsDto? fields)
    {
        if (!connection.ImportOpenTickets && !connection.ImportClosedTickets)
            return new("scope", "Sync scope", Fail, "Neither open nor closed tickets are selected, so nothing would be imported.");

        var which = (connection.ImportOpenTickets, connection.ImportClosedTickets) switch
        {
            (true, true) => "Open and closed tickets",
            (true, false) => "Open tickets",
            _ => "Closed tickets",
        };
        var recent = connection.FilterActiveWithinDays is > 0 and { } days ? $", active in the last {days} days" : "";

        var chosen = Ids(connection.FilterQueueIds);
        if (chosen.Count == 0)
            return new("scope", "Sync scope", Pass, $"{which}{recent}, from every queue or board.");
        if (fields is null || fields.QueuesOrBoards.Count == 0)
            return new("scope", "Sync scope", Warn, $"{which}{recent}, from {chosen.Count} chosen queues or boards. They could not be checked against the PSA's own list.");

        var unknown = chosen.Where(id => fields.QueuesOrBoards.All(q => q.Value != id)).ToList();
        return unknown.Count > 0
            ? new("scope", "Sync scope", Fail, $"Not in this PSA's list of queues or boards: {string.Join(", ", unknown)}. Nothing would be imported for them.")
            : new("scope", "Sync scope", Pass, $"{which}{recent}, from {chosen.Count} of {fields.QueuesOrBoards.Count} queues or boards.");
    }

    private static IReadOnlyList<string> Ids(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
