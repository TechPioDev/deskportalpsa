using Desk.Application.Abstractions;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Client-portal reads. All queries are constrained to the caller's company, and to their own
/// tickets when they are not a company administrator. Detail exposes only the public conversation;
/// internal PSA notes are never persisted to the portal, so they cannot leak here.
///
/// The staff-side methods below route through <see cref="ITicketScopeQuery"/> for the caller's
/// effective TicketsViewAll scope — the tenant filter alone bounds them to the organization, but
/// says nothing about which tickets WITHIN it the caller may see.
/// </summary>
public sealed class TicketReadService(DeskDbContext db, ITicketScopeQuery scopeQuery, ICurrentUser user) : ITicketReadService
{
    /// <summary>
    /// What a client may see. Naming a client on a ticket does NOT show it to them: work the team
    /// records against a customer — a task they asked for, a shift handover, an alert nobody has
    /// triaged yet — is the team's own record. Only a PSA ticket, or a board explicitly published to
    /// the client, is ever returned, and the company scope still applies on top of that.
    /// </summary>
    private IQueryable<Ticket> Visible(ClientAccess access) => ClientVisible(db, access);

    /// <summary>
    /// The one rule for what a client may see, public so that everything answering a client — the
    /// list, the detail, a satisfaction rating — applies the same rule rather than a copy of it.
    /// </summary>
    public static IQueryable<Ticket> ClientVisible(DeskDbContext db, ClientAccess access) =>
        db.Tickets.Where(t =>
            t.ClientCompanyId == access.ClientCompanyId
            && (t.Origin == TicketOrigin.Psa
                || db.Boards.Any(b => b.Id == t.BoardId && b.ClientVisible))
            && (access.IsCompanyAdministrator || t.RequesterUserId == access.ClientUserId));

    public async Task<IReadOnlyList<TicketListItem>> ListAsync(ClientAccess access, CancellationToken ct = default)
        => await Visible(access)
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            // Company + connection names let users tell tickets apart when an MSP runs multiple
            // PSA connections (even several tenants of the same provider).
            .Select(t => new TicketListItem(
                t.Id, t.ExternalTicketId, t.Provider, t.Title, t.PortalStatus, t.PortalPriority,
                t.QueueOrBoard, t.CreatedAt, t.LastSyncedAt,
                db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                db.PsaConnections.Where(p => p.Id == t.PsaConnectionId).Select(p => p.Name).FirstOrDefault(),
                // People stays null on the client list: no technician identity reaches a client, and
                // neither does a board or an assignee's name.
                t.PsaCreatedAt ?? t.CreatedAt, t.TimeWorkedHours, t.BillableHours, null, null, null, null,
                null, null, null, null, 0, null, null, null, false, 0, 0, null, null, null))
            .ToListAsync(ct);

    /// <summary>
    /// Every ticket the tenant holds, across all connections and companies — the STAFF list. The
    /// client-scoped list above shows one company's tickets; an MSP admin looking at that saw only
    /// whichever company their login happened to be bound to, and concluded a whole PSA was missing.
    /// Callers gate this on TicketsViewAll; the tenant filter on the DbContext bounds it to the org.
    /// </summary>
    /// <param name="boardId">
    /// Narrows the list to one of the team's own boards. Given none, every ticket the caller may see
    /// is returned, PSA queues and internal boards alike — the all-tickets list this has always been.
    /// </param>
    public async Task<IReadOnlyList<TicketListItem>> ListAllAsync(CancellationToken ct = default, Guid? boardId = null)
    {
        var visible = await StaffVisibleAsync(ct);
        if (boardId is { } board) visible = visible.Where(t => t.BoardId == board);
        return await ProjectStaffAsync(visible, take: null, byLastUpdate: false, ct);
    }

    /// <summary>
    /// The same list, narrowed in the database. The list page filters an already-loaded page, which
    /// is fine for what it holds — but it can only ever match the rows it loaded and the columns it
    /// carries, and the answer to "which ticket was that failing disk on" is usually three replies
    /// down where no client-side filter will ever find it.
    ///
    /// Answers as the caller: a client identity resolves against the same <see cref="Visible"/> rule
    /// as their own list, so a search cannot surface a ticket the list would not. One rule in one
    /// place — a second implementation is how a search ends up leaking.
    /// </summary>
    public async Task<TicketSearchResult> SearchAsync(
        TicketQuery query, ClientAccess? access = null, CancellationToken ct = default)
    {
        var scope = access is { } a ? Visible(a) : await StaffVisibleAsync(ct);
        scope = await NarrowAsync(scope, query, staff: access is null, ct);

        // Counted before the limit, so the result can say how much it is not showing rather than
        // implying there is nothing more to find.
        var total = await scope.CountAsync(ct);
        var take = Math.Clamp(query.Take, 1, 200);

        // A client's results carry no technician identity, no board and no team, exactly as their
        // list does. The projection difference IS the privacy rule, so it is not shared.
        IReadOnlyList<TicketListItem> items = access is not null
            ? await scope.AsNoTracking()
                .OrderByDescending(t => t.UpdatedAt)
                .Take(take)
                .Select(t => new TicketListItem(
                    t.Id, t.ExternalTicketId, t.Provider, t.Title, t.PortalStatus, t.PortalPriority,
                    t.QueueOrBoard, t.CreatedAt, t.LastSyncedAt,
                    db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                    db.PsaConnections.Where(p => p.Id == t.PsaConnectionId).Select(p => p.Name).FirstOrDefault(),
                    t.PsaCreatedAt ?? t.CreatedAt, t.TimeWorkedHours, t.BillableHours, null, null, null, null,
                    null, null, null, null, 0, null, null, null, false, 0, 0, null, null, null))
                .ToListAsync(ct)
            : await ProjectStaffAsync(scope, take, byLastUpdate: true, ct);

        return new TicketSearchResult(items, total, total > items.Count);
    }

    /// <summary>
    /// Applies a query to an already visibility-scoped set. Every clause here only REMOVES rows —
    /// nothing widens the set — so this can never be the place a search escapes its scope.
    /// </summary>
    private async Task<IQueryable<Ticket>> NarrowAsync(
        IQueryable<Ticket> scope, TicketQuery q, bool staff, CancellationToken ct)
    {
        if (q.BoardId is { } board) scope = scope.Where(t => t.BoardId == board);
        if (q.DepartmentId is { } dept) scope = scope.Where(t => t.DepartmentId == dept);
        if (q.TeamId is { } team) scope = scope.Where(t => t.AssignedTeamId == team);
        if (q.ClientCompanyId is { } company) scope = scope.Where(t => t.ClientCompanyId == company);
        if (!string.IsNullOrWhiteSpace(q.Status)) scope = scope.Where(t => t.PortalStatus == q.Status);
        if (!string.IsNullOrWhiteSpace(q.Priority)) scope = scope.Where(t => t.PortalPriority == q.Priority);

        // "Open" and "resolved" are each a SET of statuses, and which statuses those are is one
        // decision that already lives in one place; a second list here would drift from the first.
        if (string.Equals(q.Openness, "open", StringComparison.OrdinalIgnoreCase))
            scope = scope.Where(TicketStatusRules.Open());
        else if (string.Equals(q.Openness, "resolved", StringComparison.OrdinalIgnoreCase))
            scope = scope.Where(TicketStatusRules.Resolved());

        if (q.UnassignedOnly)
            scope = scope.Where(t => t.AssignedAppUserId == null
                && (t.AssignedTechnicianExternalId == null || t.AssignedTechnicianExternalId == ""));

        // Overdue means past its due date AND still open. A ticket closed late is history, not work
        // to do, and a list that keeps showing it is a list nobody can ever empty.
        if (q.OverdueOnly)
        {
            var now = DateTimeOffset.UtcNow;
            // ...and not paused: a ticket waiting on the customer is not late, whatever its date says.
            scope = scope.Where(TicketStatusRules.Open()).Where(t => t.SlaDueAt != null && t.SlaDueAt < now && t.SlaPausedAt == null);
        }

        // Due soon: not late yet, due within the window, still open and not paused.
        if (q.DueSoonOnly)
        {
            var now = DateTimeOffset.UtcNow;
            var by = now.AddHours(TicketStatusRules.DueSoonHours);
            scope = scope.Where(TicketStatusRules.Open())
                .Where(t => t.SlaDueAt != null && t.SlaDueAt >= now && t.SlaDueAt <= by && t.SlaPausedAt == null);
        }

        // The window is relative on purpose: see SavedTicketView.RaisedWithinDays. Measured on the
        // date the ticket was RAISED, falling back to the import date only where the provider gave
        // none — the same axis every other windowed figure in the portal uses.
        if (q.RaisedWithinDays is { } days && days > 0)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            scope = scope.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) >= since);
        }

        // Mine and Following are about the caller, so they mean nothing without one: asked for by a
        // caller with no portal identity they return nothing rather than everything.
        if (q.MineOnly || q.FollowingOnly)
        {
            if (!staff || user.UserId is not { } uid) return scope.Where(_ => false);

            if (q.MineOnly)
            {
                // A ticket sitting with a team I am in is mine to pick up — that is the point of
                // routing to a team — so "mine" covers both without a separate view for it.
                var myTeams = await db.UserTeams.AsNoTracking()
                    .Where(m => m.AppUserId == uid).Select(m => m.TeamId).ToListAsync(ct);
                scope = scope.Where(t => t.AssignedAppUserId == uid
                    || (t.AssignedTeamId != null && myTeams.Contains(t.AssignedTeamId.Value)));
            }
            if (q.FollowingOnly)
                scope = scope.Where(t => db.TicketFollowers.Any(f => f.TicketId == t.Id && f.AppUserId == uid));
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            // ToLower().Contains() rather than ILIKE or EF.Functions: it translates on Postgres and
            // on the SQLite local mode runs, and a search that works on only one of them is a search
            // nobody can develop against.
            var needle = q.Q.Trim().ToLowerInvariant();
            var notes = q.IncludeNotes;
            scope = scope.Where(t =>
                (t.Number != null && t.Number.ToLower().Contains(needle))
                || (t.ExternalTicketId != null && t.ExternalTicketId.ToLower().Contains(needle))
                || t.Title.ToLower().Contains(needle)
                || t.RequesterName.ToLower().Contains(needle)
                || db.ClientCompanies.Any(c => c.Id == t.ClientCompanyId && c.Name.ToLower().Contains(needle))
                // The conversation, when asked for. Off by default because it is the expensive half
                // and most searches are for a number or a subject; on, it is the half that makes the
                // search worth having.
                || (notes && t.Notes.Any(n => n.Body.ToLower().Contains(needle))));
        }

        return scope;
    }

    /// <summary>
    /// The staff projection, and the people enrichment that goes with it. One method because the
    /// list and the search have to agree column for column: rows built somewhere else are rows that
    /// quietly state a different set of facts.
    /// </summary>
    /// <param name="take">
    /// Null for the whole set. Given a limit, the time entries are fetched by the ids that came back
    /// rather than by joining the limited query — joining a query with a limit is a subquery the
    /// provider may or may not push down, and the id list is bounded by the limit anyway.
    /// </param>
    private async Task<List<TicketListItem>> ProjectStaffAsync(
        IQueryable<Ticket> visible, int? take, bool byLastUpdate, CancellationToken ct)
    {
        // Who is asking, for the Following column. Null only for a caller with no portal identity,
        // who then follows nothing — which is true, not a failure.
        var me = user.UserId;

        var ordered = byLastUpdate
            ? visible.AsNoTracking().OrderByDescending(t => t.UpdatedAt)
            : visible.AsNoTracking().OrderByDescending(t => t.CreatedAt);
        var limited = take is { } n ? ordered.Take(n) : ordered;

        var rows = await limited
            .Select(t => new
            {
                Item = new TicketListItem(
                    t.Id, t.ExternalTicketId, t.Provider, t.Title, t.PortalStatus, t.PortalPriority,
                    t.QueueOrBoard, t.CreatedAt, t.LastSyncedAt,
                    db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                    db.PsaConnections.Where(p => p.Id == t.PsaConnectionId).Select(p => p.Name).FirstOrDefault(),
                    t.PsaCreatedAt ?? t.CreatedAt, t.TimeWorkedHours, t.BillableHours, null,
                    t.BoardId, t.Number,
                    db.AppUsers.Where(u => u.Id == t.AssignedAppUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    db.Departments.Where(d => d.Id == t.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                    db.BoardTopics.Where(x => x.Id == t.BoardTopicId).Select(x => x.Name).FirstOrDefault(),
                    t.Source, t.SlaDueAt,
                    t.Notes.Count,
                    // When it last moved, which is what a queue is sorted by in every desk tool.
                    t.Notes.Max(n => (DateTimeOffset?)n.NoteCreatedAt) ?? t.UpdatedAt,
                    t.AssignedTeamId,
                    db.Teams.Where(x => x.Id == t.AssignedTeamId).Select(x => x.Name).FirstOrDefault(),
                    me != null && db.TicketFollowers.Any(f => f.TicketId == t.Id && f.AppUserId == me),
                    db.TicketTasks.Count(k => k.TicketId == t.Id),
                    db.TicketTasks.Count(k => k.TicketId == t.Id && k.IsDone),
                    t.FirstResponseDueAt, t.FirstRespondedAt, t.SlaPausedAt),
                t.AssignedAppUserId,
                t.AssignedTechnicianExternalId,
                t.AssignedTechnicianName,
                t.PsaConnectionId,
            })
            .ToListAsync(ct);

        // Who worked each ticket: its holder, and everyone who logged time on it - the same rule, and
        // the same PersonKey, that Client workload's People figure counts by. That is what lets a name
        // in that list open this one and land on exactly the tickets it was counted from.
        var entries = db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.AppUserId != null || (e.TechnicianExternalId != null && e.TechnicianExternalId != ""));
        var logged = take is null
            ? await entries
                .Join(visible, e => e.TicketId, t => t.Id,
                    (e, t) => new LoggedEntry(e.TicketId, t.PsaConnectionId, e.AppUserId, e.TechnicianExternalId, e.TechnicianName))
                .Distinct()
                .ToListAsync(ct)
            : await FetchLoggedByIdAsync(entries, rows.Select(r => r.Item.Id).ToList(), ct);
        var loggedByTicket = logged.ToLookup(e => e.TicketId);
        var account = await IntegrationIdentity.LoadAsync(db, ct);

        var userIds = rows.Where(r => r.AssignedAppUserId is not null).Select(r => r.AssignedAppUserId!.Value)
            .Concat(logged.Where(e => e.AppUserId is not null).Select(e => e.AppUserId!.Value))
            .Distinct().ToList();
        var userNames = userIds.Count == 0
            ? []
            : await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // Provider display names as the sync cached them; the id stands in only where neither did.
        var psaNames = rows
            .Where(r => !string.IsNullOrEmpty(r.AssignedTechnicianExternalId) && r.AssignedTechnicianName is not null
                && !account.IsAccount(r.PsaConnectionId, r.AssignedTechnicianExternalId))
            .Select(r => (Id: r.AssignedTechnicianExternalId!, Name: r.AssignedTechnicianName!))
            .Concat(logged
                .Where(e => !string.IsNullOrEmpty(e.TechnicianExternalId) && e.TechnicianName is not null
                    && !account.IsAccount(e.PsaConnectionId, e.TechnicianExternalId))
                .Select(e => (Id: e.TechnicianExternalId!, Name: e.TechnicianName!)))
            .GroupBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        string NameOf(Guid? appUserId, string? externalId) => appUserId is { } u
            ? userNames.GetValueOrDefault(u, "Unknown user")
            : psaNames.GetValueOrDefault(externalId!.Trim(), externalId!);

        return rows.Select(r =>
        {
            var people = new List<TicketPersonRef>();
            // Portal holder first, the provider's only where there is none: on a portal-assigned
            // ticket the provider's assignee is the integration account, not a person.
            // ...and the account the portal writes as holds nothing: on the PSA side those are unassigned.
            if (r.AssignedAppUserId is not null
                || (!string.IsNullOrEmpty(r.AssignedTechnicianExternalId)
                    && !account.IsAccount(r.PsaConnectionId, r.AssignedTechnicianExternalId)))
            {
                var ext = r.AssignedAppUserId is null ? r.AssignedTechnicianExternalId : null;
                people.Add(new TicketPersonRef(PersonKey.For(r.AssignedAppUserId, ext), NameOf(r.AssignedAppUserId, ext), Holds: true));
            }
            foreach (var e in loggedByTicket[r.Item.Id])
            {
                if (e.AppUserId is null && account.IsAccount(e.PsaConnectionId, e.TechnicianExternalId)) continue;
                var ext = e.AppUserId is null ? e.TechnicianExternalId : null;
                var key = PersonKey.For(e.AppUserId, ext);
                if (people.All(p => p.Key != key)) people.Add(new TicketPersonRef(key, NameOf(e.AppUserId, ext), Holds: false));
            }
            return r.Item with { People = people };
        }).ToList();
    }

    /// <summary>Time entries for a known, bounded set of tickets — the limited path above.</summary>
    private static async Task<List<LoggedEntry>> FetchLoggedByIdAsync(
        IQueryable<TicketTimeEntry> entries, List<Guid> ticketIds, CancellationToken ct)
        => ticketIds.Count == 0
            ? []
            : await entries
                .Where(e => ticketIds.Contains(e.TicketId))
                .Select(e => new LoggedEntry(
                    e.TicketId, e.Ticket!.PsaConnectionId, e.AppUserId, e.TechnicianExternalId, e.TechnicianName))
                .Distinct()
                .ToListAsync(ct);

    /// <summary>Who logged time on a ticket, and on which connection — a name for what both paths above return.</summary>
    private sealed record LoggedEntry(
        Guid TicketId, Guid? PsaConnectionId, Guid? AppUserId, string? TechnicianExternalId, string? TechnicianName);


    /// <summary>Staff callers always resolve against TicketsViewAll — it is the only permission that
    /// reaches these two methods (the controller branches to the client-scoped path otherwise) — so
    /// a caller with no linked AppUser row has nothing to resolve and sees nothing, not everything.</summary>
    private async Task<IQueryable<Ticket>> StaffVisibleAsync(CancellationToken ct)
        => user.UserId is { } uid
            ? await scopeQuery.VisibleAsync(db.Tickets, uid, Permissions.TicketsViewAll, ct)
            : db.Tickets.Where(_ => false);

    public Task<TicketDetailDto?> GetDetailAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default)
        => DetailAsync(t => Visible(access), ticketId, includeInternal: false, ct);

    /// <summary>Staff detail, narrowed to the caller's effective TicketsViewAll scope — NOT every
    /// ticket in the tenant, despite the name; the coarse permission check that used to gate the
    /// whole method said nothing about which rows within it are actually theirs to see.</summary>
    public async Task<TicketDetailDto?> GetDetailForStaffAsync(Guid ticketId, CancellationToken ct = default)
    {
        var visible = await StaffVisibleAsync(ct);
        // Staff see the WHOLE thread, internal notes included — that is what distinguishes the
        // technician view from the client one, and hiding them here is how CW-side internal notes
        // silently vanished from the portal.
        return await DetailAsync(_ => visible, ticketId, includeInternal: true, ct);
    }

    private async Task<TicketDetailDto?> DetailAsync(
        Func<object?, IQueryable<Ticket>> scope, Guid ticketId, bool includeInternal, CancellationToken ct)
    {
        var ticket = await scope(null)
            .AsNoTracking()
            .Include(t => t.Notes)
            .Include(t => t.Attachments)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null) return null; // not found OR not permitted — indistinguishable to the client

        // Portal entries logged WITH a reply: keyed by note so the thread can state the time on
        // the reply itself. Provider-side te- notes are paired by external id instead (below).
        // STAFF only — hours are billing data; the client detail never carries time pairing, the
        // same way the client never reaches the time panel.
        var entriesByNote = includeInternal
            ? (await db.TicketTimeEntries.AsNoTracking()
                .Where(e => e.TicketId == ticketId && e.NoteId != null)
                .ToListAsync(ct))
                .ToDictionary(e => e.NoteId!.Value)
            : [];

        var customerName = await db.ClientCompanies
            .AsNoTracking()
            .Where(c => c.Id == ticket.ClientCompanyId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        // Resolved by join rather than cached on the ticket, unlike the provider's assignee name.
        // That one is cached because the alternative is a call to the PSA on every read; this one
        // is a local row, so a stale copy would be a cost with nothing bought.
        var assignedAppUserName = ticket.AssignedAppUserId is { } assigneeId
            ? await db.AppUsers.AsNoTracking()
                .Where(u => u.Id == assigneeId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(ct)
            : null;
        var connection = await db.PsaConnections
            .AsNoTracking()
            .Where(p => p.Id == ticket.PsaConnectionId)
            .Select(p => new { p.Name, p.ApiEndpoint, p.TenantIdentifier })
            .FirstOrDefaultAsync(ct);
        var connectionName = connection?.Name;
        // Built from the endpoint we already have — no credentials, so no vault call to render.
        var externalUrl = PsaTicketLink.For(ticket.Provider, connection?.ApiEndpoint, ticket.ExternalTicketId, connection?.TenantIdentifier);

        // Ticket service instructions the client configured: the account-specific override if set,
        // otherwise the organization-wide default. Surfaced so technicians see them on the ticket.
        var instructions = await db.TicketInstructions
            .AsNoTracking()
            .Where(i => i.ClientCompanyId == ticket.ClientCompanyId || i.ClientCompanyId == null)
            .ToListAsync(ct);
        var serviceInstructions = instructions.FirstOrDefault(i => i.ClientCompanyId == ticket.ClientCompanyId)?.Body;
        if (string.IsNullOrWhiteSpace(serviceInstructions))
            serviceInstructions = instructions.FirstOrDefault(i => i.ClientCompanyId == null)?.Body;
        serviceInstructions = string.IsNullOrWhiteSpace(serviceInstructions) ? null : serviceInstructions;

        // The PSA's assignee - unless it is the account the portal writes as. That is how the PSA
        // records work the integration did, not a person holding the ticket, so the ticket reads as
        // unassigned there and the reassign panel pre-selects nobody.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var psaAssigneeIsAccount = account.IsAccount(ticket.PsaConnectionId, ticket.AssignedTechnicianExternalId);

        var teamName = ticket.AssignedTeamId is null ? null : await db.Teams.AsNoTracking()
            .Where(t => t.Id == ticket.AssignedTeamId).Select(t => t.Name).FirstOrDefaultAsync(ct);

        // Who is watching it. Read here rather than by a second request from the page, so the
        // detail arrives complete and the Follow button is right the first time it renders.
        var followers = includeInternal
            ? await db.TicketFollowers.AsNoTracking()
                .Where(f => f.TicketId == ticketId)
                .Join(db.AppUsers.AsNoTracking(), f => f.AppUserId, u => u.Id, (f, u) => new { f.CreatedAt, u.Id, u.DisplayName, u.Email })
                // Ordered on the column, not on the DTO: ordering by a property of a constructed
                // record does not translate to SQL, and only the in-memory test provider accepts it.
                .OrderBy(x => x.DisplayName)
                .Select(x => new TicketFollowerDto(x.Id, x.DisplayName, x.Email, x.Id == user.UserId, x.CreatedAt))
                .ToListAsync(ct)
            : [];

        return new TicketDetailDto(
            ticket.Id, ticket.ExternalTicketId, ticket.Provider, ticket.Title, ticket.Description,
            ticket.PortalStatus, ticket.PortalPriority, ticket.PortalCategory, ticket.QueueOrBoard,
            ticket.CreatedAt, ticket.ResolvedAt,
            Conversation: ticket.Notes
                .Where(n => includeInternal || n.IsPublic) // clients NEVER receive internal notes
                .OrderBy(n => n.NoteCreatedAt)
                .Select(n =>
                {
                    var te = entriesByNote.GetValueOrDefault(n.Id);
                    // The integration's own notes say so, instead of naming the account after a person.
                    return new TicketNoteDto(n.Id, account.NoteAuthor(ticket.PsaConnectionId, n.AuthorExternalId, n.AuthorName),
                        n.AuthoredByClient, n.Body, n.NoteCreatedAt, n.IsPublic,
                        n.ExternalNoteId != null && n.ExternalNoteId.StartsWith("te-")
                            ? n.ExternalNoteId[3..]
                            : te?.ExternalEntryId ?? te?.Id.ToString(),
                        te?.Hours, te?.Billable);
                })
                .ToList(),
            // Filtered the same way the conversation above is, and for the same reason. A file
            // posted with an internal note is internal: the note is withheld from clients, so the
            // screenshot of the workaround attached to it must be withheld too.
            //
            // The UI happened to hide these already — an attachment whose note was filtered out
            // matches no rendered message, and the loose-files list takes only attachments with no
            // note at all, so it fell through both. That is not protection. The file name, size,
            // uploader and ATTACHMENT ID still travelled to the client's browser, and the download
            // endpoint asked only whether the attachment belonged to the ticket.
            //
            // Attachments with no note are ticket-level and stay visible; those are the files a
            // client uploaded or that arrived with the ticket itself.
            Attachments: ticket.Attachments
                .Where(a => includeInternal
                    || a.TicketNoteId is null
                    || ticket.Notes.Any(n => n.Id == a.TicketNoteId && n.IsPublic))
                .OrderBy(a => a.UploadedAt)
                .Select(a => new AttachmentDto(a.Id, a.OriginalFileName, a.ContentType, a.SizeBytes, a.ScanStatus, a.UploadedAt)
                    {
                        // The same byline a note gets: a file the integration attached says so.
                        AuthorName = account.AttachmentAuthor(ticket.PsaConnectionId, a.AuthorExternalId, a.AuthorName),
                        FromProvider = a.ImportedFromProvider, TicketNoteId = a.TicketNoteId,
                    })
                .ToList(),
            CustomerName: customerName,
            UpdatedAt: ticket.UpdatedAt,
            ConnectionName: connectionName,
            ServiceInstructions: serviceInstructions,
            AssignedTechnicianExternalId: psaAssigneeIsAccount ? null : ticket.AssignedTechnicianExternalId,
            AssignedTechnicianName: psaAssigneeIsAccount ? null : ticket.AssignedTechnicianName,
            ExternalTicketUrl: externalUrl,
            AssignedAppUserId: ticket.AssignedAppUserId,
            AssignedAppUserName: assignedAppUserName,
            ContactName: Desk.Domain.Tickets.TicketContact.Name(ticket),
            HasReachableContact: Desk.Domain.Tickets.TicketContact.IsReachable(ticket),
            Number: ticket.Number,
            AssignedTeamId: ticket.AssignedTeamId,
            AssignedTeamName: teamName,
            // Colleagues' names, so they reach the staff detail only. The client detail carries the
            // same empty list either way, rather than a shorter one that hints there is more.
            Followers: includeInternal ? followers : [],
            // The SLA is the team's promise about its own board, so it reaches staff only; a client
            // reading a published board sees the ticket, not how the desk times itself.
            SlaPlanName: includeInternal && ticket.SlaPlanId is { } planId
                ? await db.SlaPlans.AsNoTracking().Where(p => p.Id == planId).Select(p => p.Name).FirstOrDefaultAsync(ct)
                : null,
            SlaDueAt: includeInternal ? ticket.SlaDueAt : null,
            FirstResponseDueAt: includeInternal ? ticket.FirstResponseDueAt : null,
            FirstRespondedAt: includeInternal ? ticket.FirstRespondedAt : null,
            SlaPausedAt: includeInternal ? ticket.SlaPausedAt : null,
            Rating: includeInternal
                ? await db.TicketSatisfactions.AsNoTracking().Where(s => s.TicketId == ticketId)
                    .Select(s => new TicketRatingDto(s.Rating, s.Comment, s.RatedAt,
                        db.ClientUsers.Where(u => u.Id == s.ClientUserId).Select(u => u.DisplayName).FirstOrDefault(),
                        s.TechnicianName))
                    .FirstOrDefaultAsync(ct)
                : null);
    }

    public Task<IReadOnlyList<NotificationDto>> RecentActivityAsync(ClientAccess access, int take = 10, CancellationToken ct = default)
        => RecentActivityInAsync(Visible(access), take, ct);

    /// <summary>
    /// The same feed for staff, over exactly the tickets their TicketsViewAll scope reaches. Staff
    /// pages - the header bell, the recent-activity panels, the Notifications page - all read this
    /// feed; serving it to clients only left every one of them permanently empty for the people who
    /// work the tickets.
    /// </summary>
    public async Task<IReadOnlyList<NotificationDto>> RecentActivityForStaffAsync(int take = 10, CancellationToken ct = default)
        => await RecentActivityInAsync(await StaffVisibleAsync(ct), take, ct);

    private static async Task<IReadOnlyList<NotificationDto>> RecentActivityInAsync(IQueryable<Ticket> scope, int take, CancellationToken ct)
        => await scope
            .AsNoTracking()
            .OrderByDescending(t => t.LastSyncedAt ?? t.CreatedAt)
            .Take(take)
            .Select(t => new NotificationDto(
                t.Id, t.Title, "ticket-updated",
                "Status: " + t.PortalStatus, t.LastSyncedAt ?? t.CreatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ActivityEventDto>> ActivityHistoryAsync(ClientAccess access, int take = 50, CancellationToken ct = default)
    {
        // Three real record types merged into one feed. Everything routes through Visible(), so
        // non-admins see only their own tickets' history, and only PUBLIC replies ever appear —
        // this is a client surface, and internal analysis has no business in it.
        var created = await Visible(access).AsNoTracking()
            .OrderByDescending(t => t.CreatedAt).Take(take)
            .Select(t => new ActivityEventDto(t.Id, t.Title, "ticket-created", null, t.CreatedAt))
            .ToListAsync(ct);

        var resolved = await Visible(access).AsNoTracking()
            .Where(t => t.ResolvedAt != null)
            .OrderByDescending(t => t.ResolvedAt).Take(take)
            .Select(t => new ActivityEventDto(t.Id, t.Title, "ticket-resolved", null, t.ResolvedAt!.Value))
            .ToListAsync(ct);

        // Join + anonymous projection, DTO mapped in memory: SelectMany over the navigation with a
        // record constructor is exactly the shape query providers refuse to translate.
        var replyRows = await db.TicketNotes.AsNoTracking()
            .Where(n => n.IsPublic)
            .Join(Visible(access), n => n.TicketId, t => t.Id,
                (n, t) => new { t.Id, t.Title, t.PsaConnectionId, n.AuthoredByClient, n.AuthorName, n.AuthorExternalId, n.NoteCreatedAt })
            .OrderByDescending(x => x.NoteCreatedAt).Take(take)
            .ToListAsync(ct);
        // The same byline the ticket's thread shows: a client reading "staff reply from Sudanshu
        // Aggarwal" in their history would be told a person answered when the integration did.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var replies = replyRows
            .Select(x => new ActivityEventDto(
                x.Id, x.Title, x.AuthoredByClient ? "client-reply" : "staff-reply",
                account.NoteAuthor(x.PsaConnectionId, x.AuthorExternalId, x.AuthorName), x.NoteCreatedAt))
            .ToList();

        return created.Concat(resolved).Concat(replies)
            .OrderByDescending(e => e.At)
            .Take(take)
            .ToList();
    }
}
