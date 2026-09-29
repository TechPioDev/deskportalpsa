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
/// </summary>
public sealed class TicketStatusWriter(DeskDbContext db, IConnectorResolver connectors, IMappingEngine mapping)
{
    public async Task<string> SetAsync(Ticket ticket, string status, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ValidationFailedException("A status is required.");
        var portalStatus = status.Trim();

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
            if (Closed(portalStatus)) ticket.ClosedAt ??= DateTimeOffset.UtcNow;
            if (Resolved(portalStatus)) ticket.ResolvedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return ticket.PortalStatus;
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
        await db.SaveChangesAsync(ct);
        return ticket.PortalStatus;
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
