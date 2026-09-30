using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Moves a ticket to a portal-neutral status. A PSA ticket's status is the provider's: the chosen value
/// is mapped to the PSA's own, pushed, and only then reflected here. A board ticket's status is the
/// portal's own and is set directly.
///
/// One writer, used by the status control and by approvals alike, so "waiting on the customer" pauses
/// an SLA and closing checks the task list the same way whichever of them asked. The caller has
/// already decided the person may change this ticket.
///
/// Every change is audited against the ticket, which is what its History reads. Moving a finished
/// ticket back to work is a reopen: counted, dated, and for a board ticket its finished dates are
/// cleared so it is measured as open again.
/// </summary>
public sealed class TicketStatusWriter(
    DeskDbContext db, IConnectorResolver connectors, IMappingEngine mapping, IAuditWriter? audit = null)
{
    public const int ResolutionMaxLength = 4000;

    public Task<string> SetAsync(Ticket ticket, string status, CancellationToken ct = default)
        => SetAsync(ticket, status, null, ct);

    /// <param name="countReopen">
    /// False when work goes back because a reviewer sent it back: that is a review finding, counted
    /// as a send-back, not a ticket that came back after it was finished.
    /// </param>
    public async Task<string> SetAsync(
        Ticket ticket, string status, string? resolution, CancellationToken ct = default, bool countReopen = true)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ValidationFailedException("A status is required.");
        var portalStatus = status.Trim();
        var from = ticket.PortalStatus;
        var resolving = Resolved(portalStatus);
        // Finished before (by date or by status), and not finished now.
        var reopening = !resolving && (ticket.ResolvedAt is not null || ticket.ClosedAt is not null || Resolved(from));
        resolution = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim();
        if (resolution is { Length: > ResolutionMaxLength })
            throw new ValidationFailedException($"Keep the resolution to {ResolutionMaxLength} characters.");

        // A board can ask for the resolution to be written down. Asked only when there is none yet,
        // so moving a resolved ticket to closed does not ask twice.
        if (resolving && resolution is null && string.IsNullOrWhiteSpace(ticket.Resolution)
            && ticket.BoardId is { } boardId && await db.Boards.AnyAsync(b => b.Id == boardId && b.RequireResolution, ct))
            throw new ValidationFailedException("This board asks for a resolution when a ticket is resolved. Say what fixed it.");

        // A board or topic that reviews resolved work: it waits for a lead's approval before it closes.
        var reviewed = await ReviewRequiredAsync(ticket, ct);
        if (reviewed && Closed(portalStatus) && ticket.ReviewState != TicketReviewState.Approved)
            throw new ValidationFailedException(
                "Work on this board is reviewed before it closes. Mark it resolved, and a board lead approves it from the ticket.");

        // As in osTicket: the task list is only worth keeping if "closed" means it was done. Asked
        // before either branch, so it holds for PSA tickets as well — their tasks live here too.
        // A PSA that closes the ticket itself is not stopped; the portal cannot veto the provider.
        if (Resolved(portalStatus))
        {
            var open = await TicketTaskService.OpenCountAsync(db, ticket.Id, ct);
            if (open > 0)
                throw new ValidationFailedException(open == 1
                    ? "One task on this ticket is still open. Tick it off or remove it, then close the ticket."
                    : $"{open} tasks on this ticket are still open. Tick them off or remove them, then close the ticket.");
        }

        // A ticket on the team's own board has no provider to agree with: the portal's status IS
        // the status, so it is set here and nothing is pushed.
        if (ticket.Origin != TicketOrigin.Psa || ticket.PsaConnectionId is not { } connectionId || ticket.Provider is not { } provider)
        {
            // Waiting on the customer or on hold stops the SLA clock; moving on gives the time back.
            await Boards.SlaPlanner.ApplyStatusAsync(db, ticket, portalStatus, DateTimeOffset.UtcNow, ct);
            ticket.PortalStatus = portalStatus;
            if (reopening)
            {
                // Open again, so no longer finished: left in place, the old dates would keep it out of
                // every open-work list and make its eventual resolution time look instant.
                ticket.ResolvedAt = null;
                ticket.ClosedAt = null;
            }
            if (Closed(portalStatus)) ticket.ClosedAt ??= DateTimeOffset.UtcNow;
            if (Resolved(portalStatus)) ticket.ResolvedAt ??= DateTimeOffset.UtcNow;
            return await FinishAsync(ticket, from, portalStatus, resolving, reopening, resolution, reviewed, countReopen, ct);
        }

        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA.");

        var rules = await db.FieldMappings.AsNoTracking()
            .Where(m => m.Provider == provider && m.IsActive).ToListAsync(ct);
        var ctx = new MappingContext { Provider = provider, PsaConnectionId = connectionId, QueueOrBoardKey = ticket.QueueOrBoard };

        // Portal → PSA value (name). Fall back to the raw portal value when no rule matches. The
        // connector resolves the name against the ticket's own context (e.g. CW statuses are
        // board-scoped, so a globally-discovered id could be invalid for this ticket).
        var mappedName = mapping.MapToProvider(rules, ctx, "status", portalStatus).Value ?? portalStatus;

        var connector = await connectors.ResolveAsync(connectionId, ct);
        var result = await connector.UpdateTicketAsync(ticket.ExternalTicketId,
            new UnifiedTicketUpdate { Status = mappedName, IdempotencyKey = Guid.NewGuid().ToString("N") }, ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the status change.");

        ticket.PortalStatus = portalStatus;
        ticket.PsaStatus = mappedName;
        // The provider owns a PSA ticket's dates; sync brings them. The reopen is still counted here.
        return await FinishAsync(ticket, from, portalStatus, resolving, reopening, resolution, reviewed, countReopen, ct);
    }

    private async Task<string> FinishAsync(
        Ticket ticket, string from, string to, bool resolving, bool reopening, string? resolution,
        bool reviewed, bool countReopen, CancellationToken ct)
    {
        if (resolving && resolution is not null) ticket.Resolution = resolution;
        if (reopening)
        {
            if (countReopen)
            {
                ticket.ReopenCount++;
                ticket.LastReopenedAt = DateTimeOffset.UtcNow;
            }
            // Back to work: whatever review there was covered the old attempt, not the next one.
            ticket.ReviewState = TicketReviewState.None;
        }
        // Resolved on a reviewed board: it now waits for a lead.
        if (reviewed && resolving && !Closed(to) && ticket.ReviewState != TicketReviewState.Approved)
            ticket.ReviewState = TicketReviewState.Pending;
        await db.SaveChangesAsync(ct);
        if (audit is not null && !string.Equals(from, to, StringComparison.Ordinal))
            await audit.WriteAsync(reopening ? "ticket.reopened" : "ticket.status.changed", "Ticket", ticket.Id.ToString(),
                new { from, to, resolutionRecorded = resolution is not null }, ct);
        return ticket.PortalStatus;
    }

    /// <summary>Whether this ticket's board, or its topic, reviews resolved work before it closes.</summary>
    public async Task<bool> ReviewRequiredAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.BoardId is not { } boardId) return false;
        if (await db.Boards.AnyAsync(b => b.Id == boardId && b.RequireReview, ct)) return true;
        return ticket.BoardTopicId is { } topicId && await db.BoardTopics.AnyAsync(t => t.Id == topicId && t.RequireReview, ct);
    }

    /// <summary>
    /// Closing dates on a board with no provider. Every report that measures resolution time reads
    /// these, so an internal ticket that is closed without one would silently sit outside its own
    /// figures — the exact fault the attention list reports for ConnectWise today.
    /// </summary>
    private static bool Closed(string status) => status.Contains("CLOSED", StringComparison.OrdinalIgnoreCase);
    private static bool Resolved(string status) =>
        Closed(status) || status.Contains("RESOLV", StringComparison.OrdinalIgnoreCase);
}
