using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Turns a normalized provider ticket into (or updates) the portal's <see cref="Ticket"/> row.
/// Order of guards: unchanged-hash short-circuit → portal-echo short-circuit → apply. Provider
/// values are translated to portal values with the mapping engine; the raw provider value is kept
/// alongside for traceability.
/// </summary>
public sealed class TicketSyncService(
    DeskDbContext db,
    IMappingEngine mapping,
    ISyncEventStore syncEvents,
    TimeProvider clock,
    Desk.Application.Analytics.IActivityRecorder activity,
    Microsoft.Extensions.Logging.ILogger<TicketSyncService>? logger = null) : ITicketSyncService
{
    public async Task<TicketSyncOutcome> UpsertFromProviderAsync(
        Guid psaConnectionId, UnifiedTicket incoming, IReadOnlyList<FieldMapping> rules, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var company = await EnsureCompanyAsync(connection, incoming.RequesterExternalId, incoming.CompanyName, ct);
        var ctx = new MappingContext
        {
            Provider = connection.Provider,
            PsaConnectionId = psaConnectionId,
            ClientCompanyId = company.Id,
            QueueOrBoardKey = incoming.QueueOrBoard,
        };

        // Translate provider → portal, passing raw values through when no rule matches.
        var portalStatus = Map(rules, ctx, "status", incoming.Status) ?? incoming.Status ?? "NEW";
        var portalPriority = Map(rules, ctx, "priority", incoming.Priority) ?? incoming.Priority ?? "NORMAL";
        // What the connection's classification rules make of the three levels the PSA filed it
        // under. A rule that gives a category speaks over the category field's own mapping, which
        // is what the ticket has where no rule does. The work type and the subcategory have no
        // other source: with no rule for them they are empty, never the PSA's word carried across.
        var filed = ClassificationRules.Resolve(await ClassificationOfAsync(connection, ct),
            incoming.TicketType, incoming.IssueType, incoming.SubIssueType);
        var portalCategory = filed.Category ?? Map(rules, ctx, "category", incoming.Category) ?? incoming.Category;
        var portalQueue = Map(rules, ctx, "queue", incoming.QueueOrBoard) ?? incoming.QueueOrBoard;

        var existing = await db.Tickets.FirstOrDefaultAsync(
            t => t.PsaConnectionId == psaConnectionId && t.ExternalTicketId == incoming.ExternalId, ct);

        // The device the ticket is about. A provider that does not carry it leaves the portal's; one
        // that lists several (ConnectWise) keeps the portal's while it is still among them, rather
        // than swapping it for whichever the provider happens to list first.
        var deviceExternalId = !incoming.DeviceKnown
            ? existing?.DeviceExternalId
            : existing?.DeviceExternalId is { } held && incoming.DeviceExternalIds.Contains(held)
                ? held
                : string.IsNullOrWhiteSpace(incoming.DeviceExternalId) ? null : incoming.DeviceExternalId;

        var hash = UpdateHasher.ForTicketState(
            portalStatus, portalPriority, portalCategory, incoming.Title, incoming.Description,
            incoming.ResolvedAt, incoming.ClosedAt, incoming.SlaDueAt, incoming.CreatedAt, portalQueue,
            string.IsNullOrWhiteSpace(incoming.RequesterName) ? null : incoming.RequesterName,
            string.IsNullOrWhiteSpace(incoming.RequesterEmail) ? null : incoming.RequesterEmail,
            deviceExternalId,
            Level(incoming.TicketType), Level(incoming.IssueType), Level(incoming.SubIssueType),
            filed.WorkType, filed.Subcategory);

        if (existing is not null)
        {
            if (existing.UpdateHash == hash)
                return TicketSyncOutcome.SkippedUnchanged; // idempotent — nothing changed

            if (await syncEvents.IsPortalEchoAsync(psaConnectionId, existing.Id, hash, ct))
                return TicketSyncOutcome.SkippedEcho;       // our own write coming back
        }

        var ticket = existing ?? new Ticket
        {
            MspOrganizationId = connection.MspOrganizationId,
            PsaConnectionId = psaConnectionId,
            Provider = connection.Provider,
            ExternalTicketId = incoming.ExternalId,
            ClientCompanyId = company.Id,
            CorrelationId = Guid.NewGuid(),
            RequesterName = incoming.RequesterName ?? "Unknown",
            RequesterEmail = incoming.RequesterEmail ?? "unknown@unknown",
            Title = incoming.Title,
        };

        // What the portal believed BEFORE this write, so a transition can be told apart from a
        // value that was simply always there. Re-importing an already-closed ticket must not emit
        // a fresh closure every sync — the analytics would count one closure many times.
        var wasAssignedTo = existing?.AssignedTechnicianExternalId;
        var wasResolved = existing?.ResolvedAt is not null;
        var wasClosed = existing?.ClosedAt is not null;

        ticket.Title = incoming.Title;
        ticket.Description = incoming.Description;
        // The ticket's contact, refreshed on every sync: a contact changed in the PSA is who a public
        // reply now goes to. Only overwritten when the provider names one, so a portal-raised
        // ticket's requester is not wiped by a provider that sends no contact.
        if (!string.IsNullOrWhiteSpace(incoming.RequesterEmail)) ticket.RequesterEmail = incoming.RequesterEmail;
        if (!string.IsNullOrWhiteSpace(incoming.RequesterName)) ticket.RequesterName = incoming.RequesterName;
        ticket.PortalStatus = portalStatus;
        ticket.PsaStatus = incoming.Status;
        ticket.PortalPriority = portalPriority;
        ticket.PsaPriority = incoming.Priority;
        ticket.PortalCategory = portalCategory;
        ticket.PsaCategory = incoming.Category;
        ticket.PortalWorkType = filed.WorkType;
        ticket.PortalSubcategory = filed.Subcategory;
        // Kept as the PSA words them. Cleared when the PSA clears them: the hash above no longer
        // carries a level the ticket has lost, so that change is seen too.
        ticket.PsaTicketType = Level(incoming.TicketType);
        ticket.PsaIssueType = Level(incoming.IssueType);
        ticket.PsaSubIssueType = Level(incoming.SubIssueType);
        ticket.QueueOrBoard = portalQueue;
        ticket.AssignedTechnicianExternalId = incoming.AssignedTechnicianExternalId;
        ticket.ResolvedAt = incoming.ResolvedAt;
        ticket.ClosedAt = incoming.ClosedAt;
        // Reopened in the PSA: the resolution credited here was undone, so it is credited to nobody
        // until someone finishes it again - here, or in the PSA (then it goes to whoever holds it).
        if (!Tickets.TicketStatusWriter.Finished(portalStatus)) ticket.ResolvedByAppUserId = null;
        ticket.SlaDueAt = incoming.SlaDueAt;
        // Only ever set from the provider — never defaulted to "now" when absent, because a
        // fabricated raise date would make an unknown-age ticket look brand new.
        ticket.PsaCreatedAt = incoming.CreatedAt;
        // The device, where the provider says (and only then: a provider that does not carry it would
        // otherwise clear one set in the portal). The device may not have been synced yet; the daily
        // device sync links the id up when it arrives.
        if (incoming.DeviceKnown)
        {
            ticket.DeviceExternalId = deviceExternalId;
            ticket.DeviceId = ticket.DeviceExternalId is null ? null : await db.Devices.AsNoTracking()
                .Where(d => d.PsaConnectionId == psaConnectionId && d.ExternalId == ticket.DeviceExternalId)
                .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        }
        ticket.UpdateHash = hash;
        ticket.SyncStatus = TicketSyncStatus.Synced;
        ticket.LastSyncedAt = clock.GetUtcNow();
        ticket.Version++;

        if (existing is null)
            db.Tickets.Add(ticket);

        await db.SaveChangesAsync(ct);

        // Observed, not witnessed: the PSA is telling us these happened, and it is the only party
        // that knows. Source is Psa so the two halves of the activity picture stay distinguishable
        // — which is the whole point of recording them together.
        var observed = new List<Desk.Application.Analytics.ActivityRecord>();
        Desk.Application.Analytics.ActivityRecord Seen(Desk.Domain.Analytics.ActivityKind kind, DateTimeOffset? at, string? detail)
            => new(kind, Desk.Domain.Analytics.ActivitySource.Psa)
            {
                MspOrganizationId = ticket.MspOrganizationId,
                OccurredAt = at,
                ActorExternalId = ticket.AssignedTechnicianExternalId,
                PsaConnectionId = ticket.PsaConnectionId,
                TicketId = ticket.Id,
                ClientCompanyId = ticket.ClientCompanyId,
                Detail = detail,
            };

        if (incoming.AssignedTechnicianExternalId is { Length: > 0 } assignee && assignee != wasAssignedTo)
            observed.Add(Seen(Desk.Domain.Analytics.ActivityKind.TicketAssigned, null, $"Assigned to {assignee}"));
        if (!wasResolved && incoming.ResolvedAt is { } resolvedAt)
            observed.Add(Seen(Desk.Domain.Analytics.ActivityKind.TicketResolved, resolvedAt, null));
        if (!wasClosed && incoming.ClosedAt is { } closedAt)
            observed.Add(Seen(Desk.Domain.Analytics.ActivityKind.TicketClosed, closedAt, null));

        await activity.RecordManyAsync(observed, ct);
        return existing is null ? TicketSyncOutcome.Created : TicketSyncOutcome.Updated;
    }

    // A connection's classification rules, read once for each connection this instance syncs: a
    // run holds one instance, so a page of tickets is one read and not one for each ticket. Not
    // tracked, so letting go of a page's tickets does not let go of these.
    private readonly Dictionary<Guid, IReadOnlyList<ClassificationMapping>> _classification = [];

    private async Task<IReadOnlyList<ClassificationMapping>> ClassificationOfAsync(PsaConnection connection, CancellationToken ct)
    {
        if (_classification.TryGetValue(connection.Id, out var held)) return held;
        // The organization is named, as it is for the field rules: the scheduled sync runs where
        // the tenant filter is off, and a connection is only ever read by its own organization's rules.
        var read = await db.ClassificationMappings.AsNoTracking()
            .Where(m => m.MspOrganizationId == connection.MspOrganizationId && m.PsaConnectionId == connection.Id)
            .ToListAsync(ct);
        return _classification[connection.Id] = read;
    }

    /// <summary>One level of the PSA's classification as it is stored: trimmed, nothing for blank, and no longer than its column.</summary>
    private static string? Level(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= 200 ? v : v[..200];
    }

    private string? Map(IReadOnlyList<FieldMapping> rules, MappingContext ctx, string field, string? value)
    {
        if (value is null) return null;
        var r = mapping.MapToPortal(rules, ctx, field, value);
        if (!r.Resolved) ReportUnmapped(ctx, field, value);
        return r.Resolved ? r.Value : null;
    }

    // Values already reported. Per INSTANCE, not process-wide: the service is scoped and a sync run
    // holds one, so each unmapped value is named once per run rather than once per ticket. Repeating
    // next run is correct — the value is still unmapped — and it keeps the rule explainable instead
    // of hiding a condition forever behind state nobody can see.
    private readonly HashSet<string> _reported = [];

    /// <summary>
    /// Says out loud that the provider sent a value nothing maps.
    ///
    /// An unmapped value falls through to the raw provider value, which is indistinguishable from a
    /// mapping that passed it through deliberately — so the portal displays something plausible and
    /// nobody finds out. ConnectWise ran that way on every status and priority of every ticket
    /// without a single error, and it took reading the database to notice.
    ///
    /// Field and value only: these are configuration labels (status/priority/type/board names), not
    /// anything a customer wrote.
    /// </summary>
    private void ReportUnmapped(MappingContext ctx, string field, string value)
    {
        if (logger is null) return;
        if (!_reported.Add($"{ctx.PsaConnectionId}|{field}|{value}")) return;
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
            "No mapping rule matches {Provider} {Field} '{Value}' on connection {ConnectionId}; "
            + "the portal is showing the provider's own value",
            ctx.Provider, field, value, ctx.PsaConnectionId);
    }

    /// <summary>Company sync: find the client company for this external id, creating a stub if new.</summary>
    private async Task<ClientCompany> EnsureCompanyAsync(PsaConnection connection, string? externalCompanyId, string? companyName, CancellationToken ct)
    {
        var extId = string.IsNullOrEmpty(externalCompanyId) ? "unknown" : externalCompanyId;
        var company = await db.ClientCompanies.FirstOrDefaultAsync(
            c => c.PsaConnectionId == connection.Id && c.ExternalCompanyId == extId, ct);
        if (company is not null)
        {
            // Upgrade a placeholder (or stale) name when the provider sends the real one inline.
            if (!string.IsNullOrWhiteSpace(companyName) && company.Name != companyName)
            {
                company.Name = companyName;
                await db.SaveChangesAsync(ct);
            }
            return company;
        }

        company = new ClientCompany
        {
            MspOrganizationId = connection.MspOrganizationId,
            PsaConnectionId = connection.Id,
            ExternalCompanyId = extId,
            // Prefer the provider-supplied name; placeholder until a company sync fills it otherwise.
            Name = string.IsNullOrWhiteSpace(companyName) ? $"Company {extId}" : companyName,
        };
        db.ClientCompanies.Add(company);
        await db.SaveChangesAsync(ct);
        return company;
    }
}
