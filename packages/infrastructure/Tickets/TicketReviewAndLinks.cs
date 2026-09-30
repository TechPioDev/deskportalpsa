using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Approving resolved work, or sending it back. The status change itself is the status writer's, so
/// closing still checks tasks and a send-back returns the ticket to work the same way anything does -
/// except that it counts as a send-back, not a reopen.
/// </summary>
public sealed class TicketReviewService(DeskDbContext db, TicketStatusWriter status, IAuditWriter audit, TimeProvider clock)
    : ITicketReviewService
{
    public const int NoteMaxLength = 2000;

    public async Task ReviewAsync(Ticket ticket, Guid reviewerId, string reviewerName, bool approve, string? note, CancellationToken ct = default)
    {
        if (ticket.ReviewState != TicketReviewState.Pending)
            throw new ValidationFailedException("This ticket is not waiting for review.");
        // A second pair of eyes has to be a second person.
        if (ticket.AssignedAppUserId == reviewerId)
            throw new ForbiddenException("You hold this ticket, so someone else reviews it.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > NoteMaxLength })
            throw new ValidationFailedException($"Keep the review note to {NoteMaxLength} characters.");
        if (!approve && note is null)
            throw new ValidationFailedException("Say what needs doing before sending it back.");

        var now = clock.GetUtcNow();
        if (note is not null)
            db.TicketNotes.Add(new TicketNote
            {
                MspOrganizationId = ticket.MspOrganizationId, TicketId = ticket.Id, AuthorName = reviewerName,
                AuthoredByClient = false, IsPublic = false, NoteCreatedAt = now,
                Body = (approve ? "Approved in review: " : "Sent back in review: ") + note,
            });

        if (approve)
        {
            ticket.ReviewState = TicketReviewState.Approved;
            ticket.ReviewedByUserId = reviewerId;
            ticket.ReviewedAt = now;
            await status.SetAsync(ticket, "CLOSED", null, ct);
        }
        else
        {
            ticket.ReviewSendBacks++;
            await status.SetAsync(ticket, "IN_PROGRESS", null, ct, countReopen: false);
        }
        await audit.WriteAsync(approve ? "ticket.review.approved" : "ticket.review.sent_back", "Ticket", ticket.Id.ToString(),
            new { noted = note is not null, sendBacks = ticket.ReviewSendBacks }, ct);
    }
}

/// <summary>
/// Links between tickets. Both ends must be tickets the person can see - a link must never become a
/// way to learn that a ticket exists, or what it is called. Recorded on both tickets' history.
/// </summary>
public sealed class TicketLinkService(DeskDbContext db, ITicketScopeQuery scope, IAuditWriter audit) : ITicketLinkService
{
    public async Task<IReadOnlyList<TicketLinkDto>> ListAsync(Ticket ticket, Guid appUserId, CancellationToken ct = default)
    {
        var links = await db.TicketLinks.AsNoTracking()
            .Where(l => l.FromTicketId == ticket.Id || l.ToTicketId == ticket.Id)
            .ToListAsync(ct);
        if (links.Count == 0) return [];
        var otherIds = links.Select(l => l.FromTicketId == ticket.Id ? l.ToTicketId : l.FromTicketId).Distinct().ToList();
        var visible = await scope.VisibleAsync(db.Tickets, appUserId, Permissions.TicketsViewAll, ct);
        var others = await visible.AsNoTracking().Where(t => otherIds.Contains(t.Id))
            .Select(t => new { t.Id, Ref = t.Number ?? t.ExternalTicketId, t.Title, t.PortalStatus, t.Origin })
            .ToDictionaryAsync(t => t.Id, ct);
        return links
            .Select(l =>
            {
                var outgoing = l.FromTicketId == ticket.Id;
                var otherId = outgoing ? l.ToTicketId : l.FromTicketId;
                return others.TryGetValue(otherId, out var o)
                    ? new TicketLinkDto(l.Id, Relation(l.Kind, outgoing), o.Id, o.Ref, o.Title, o.PortalStatus, o.Origin)
                    : null;
            })
            .Where(x => x is not null).Select(x => x!)
            .OrderBy(x => x.Relation).ThenBy(x => x.OtherReference).ToList();
    }

    public async Task<TicketLinkDto> AddAsync(Ticket ticket, Guid appUserId, Guid otherTicketId, TicketLinkKind kind, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind)) throw new ValidationFailedException("Choose how the tickets relate.");
        if (otherTicketId == ticket.Id) throw new ValidationFailedException("A ticket cannot be linked to itself.");
        // Not found, whether it does not exist or is not the person's to see: the same answer either way.
        var other = await scope.FindAsync(db.Tickets, otherTicketId, appUserId, Permissions.TicketsViewAll, ct)
            ?? throw new NotFoundException("Ticket");

        // One link per pair, whatever the kind and direction: two tickets that are both "parent of" each
        // other, or related and duplicate at once, is a contradiction nobody can act on.
        var existing = await db.TicketLinks.AnyAsync(l =>
            (l.FromTicketId == ticket.Id && l.ToTicketId == other.Id) || (l.FromTicketId == other.Id && l.ToTicketId == ticket.Id), ct);
        if (existing) throw new ValidationFailedException("These tickets are already linked. Remove that link first to change how they relate.");

        var link = new TicketLink
        {
            MspOrganizationId = ticket.MspOrganizationId, FromTicketId = ticket.Id, ToTicketId = other.Id, Kind = kind, CreatedByUserId = appUserId,
        };
        db.TicketLinks.Add(link);
        await db.SaveChangesAsync(ct);
        var here = ticket.Number ?? ticket.ExternalTicketId;
        var there = other.Number ?? other.ExternalTicketId;
        await audit.WriteAsync("ticket.linked", "Ticket", ticket.Id.ToString(), new { relation = Relation(kind, true), other = there }, ct);
        await audit.WriteAsync("ticket.linked", "Ticket", other.Id.ToString(), new { relation = Relation(kind, false), other = here }, ct);
        return new TicketLinkDto(link.Id, Relation(kind, true), other.Id, there, other.Title, other.PortalStatus, other.Origin);
    }

    public async Task RemoveAsync(Ticket ticket, Guid appUserId, Guid linkId, CancellationToken ct = default)
    {
        var link = await db.TicketLinks.FirstOrDefaultAsync(l => l.Id == linkId && (l.FromTicketId == ticket.Id || l.ToTicketId == ticket.Id), ct)
            ?? throw new NotFoundException("Link");
        var otherId = link.FromTicketId == ticket.Id ? link.ToTicketId : link.FromTicketId;
        // The other end must be visible too: removing a link to a ticket you cannot see would still reach it.
        var other = await scope.FindAsync(db.Tickets, otherId, appUserId, Permissions.TicketsViewAll, ct)
            ?? throw new NotFoundException("Link");
        db.TicketLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.unlinked", "Ticket", ticket.Id.ToString(), new { other = other.Number ?? other.ExternalTicketId }, ct);
        await audit.WriteAsync("ticket.unlinked", "Ticket", other.Id.ToString(), new { other = ticket.Number ?? ticket.ExternalTicketId }, ct);
    }

    /// <summary>What the other ticket is, read from this ticket's side of the link.</summary>
    public static string Relation(TicketLinkKind kind, bool outgoing) => (kind, outgoing) switch
    {
        (TicketLinkKind.Related, _) => "Related to",
        (TicketLinkKind.Duplicate, true) => "Duplicate of",
        (TicketLinkKind.Duplicate, false) => "Duplicated by",
        (TicketLinkKind.Parent, true) => "Parent of",
        (TicketLinkKind.Parent, false) => "Part of",
        (TicketLinkKind.Blocks, true) => "Blocks",
        (TicketLinkKind.Blocks, false) => "Blocked by",
        _ => "Related to",
    };
}
