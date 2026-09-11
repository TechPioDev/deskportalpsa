using Desk.Application.Abstractions;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
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
    private IQueryable<Ticket> Visible(ClientAccess access) =>
        db.Tickets.Where(t =>
            t.ClientCompanyId == access.ClientCompanyId
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
                // People stays null on the client list: no technician identity reaches a client.
                t.PsaCreatedAt ?? t.CreatedAt, t.TimeWorkedHours, t.BillableHours, null))
            .ToListAsync(ct);

    /// <summary>
    /// Every ticket the tenant holds, across all connections and companies — the STAFF list. The
    /// client-scoped list above shows one company's tickets; an MSP admin looking at that saw only
    /// whichever company their login happened to be bound to, and concluded a whole PSA was missing.
    /// Callers gate this on TicketsViewAll; the tenant filter on the DbContext bounds it to the org.
    /// </summary>
    public async Task<IReadOnlyList<TicketListItem>> ListAllAsync(CancellationToken ct = default)
    {
        var visible = await StaffVisibleAsync(ct);
        var rows = await visible
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                Item = new TicketListItem(
                    t.Id, t.ExternalTicketId, t.Provider, t.Title, t.PortalStatus, t.PortalPriority,
                    t.QueueOrBoard, t.CreatedAt, t.LastSyncedAt,
                    db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                    db.PsaConnections.Where(p => p.Id == t.PsaConnectionId).Select(p => p.Name).FirstOrDefault(),
                    t.PsaCreatedAt ?? t.CreatedAt, t.TimeWorkedHours, t.BillableHours, null),
                t.AssignedAppUserId,
                t.AssignedTechnicianExternalId,
                t.AssignedTechnicianName,
                t.PsaConnectionId,
            })
            .ToListAsync(ct);

        // Who worked each ticket: its holder, and everyone who logged time on it - the same rule, and
        // the same PersonKey, that Client workload's People figure counts by. That is what lets a name
        // in that list open this one and land on exactly the tickets it was counted from.
        var logged = await db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.AppUserId != null || (e.TechnicianExternalId != null && e.TechnicianExternalId != ""))
            .Join(visible, e => e.TicketId, t => t.Id,
                (e, t) => new { e.TicketId, t.PsaConnectionId, e.AppUserId, e.TechnicianExternalId, e.TechnicianName })
            .Distinct()
            .ToListAsync(ct);
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
            .Select(p => new { p.Name, p.ApiEndpoint })
            .FirstOrDefaultAsync(ct);
        var connectionName = connection?.Name;
        // Built from the endpoint we already have — no credentials, so no vault call to render.
        var externalUrl = PsaTicketLink.For(ticket.Provider, connection?.ApiEndpoint, ticket.ExternalTicketId);

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
                    return new TicketNoteDto(n.Id, n.AuthorName, n.AuthoredByClient, n.Body, n.NoteCreatedAt, n.IsPublic,
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
                    { AuthorName = a.AuthorName, FromProvider = a.ImportedFromProvider, TicketNoteId = a.TicketNoteId })
                .ToList(),
            CustomerName: customerName,
            UpdatedAt: ticket.UpdatedAt,
            ConnectionName: connectionName,
            ServiceInstructions: serviceInstructions,
            AssignedTechnicianExternalId: psaAssigneeIsAccount ? null : ticket.AssignedTechnicianExternalId,
            AssignedTechnicianName: psaAssigneeIsAccount ? null : ticket.AssignedTechnicianName,
            ExternalTicketUrl: externalUrl,
            AssignedAppUserId: ticket.AssignedAppUserId,
            AssignedAppUserName: assignedAppUserName);
    }

    public async Task<IReadOnlyList<NotificationDto>> RecentActivityAsync(ClientAccess access, int take = 10, CancellationToken ct = default)
        => await Visible(access)
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
                (n, t) => new { t.Id, t.Title, n.AuthoredByClient, n.AuthorName, n.NoteCreatedAt })
            .OrderByDescending(x => x.NoteCreatedAt).Take(take)
            .ToListAsync(ct);
        var replies = replyRows
            .Select(x => new ActivityEventDto(
                x.Id, x.Title, x.AuthoredByClient ? "client-reply" : "staff-reply", x.AuthorName, x.NoteCreatedAt))
            .ToList();

        return created.Concat(resolved).Concat(replies)
            .OrderByDescending(e => e.At)
            .Take(take)
            .ToList();
    }
}
