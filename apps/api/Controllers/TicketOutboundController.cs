using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Sync;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// What becomes of a change that has not reached the PSA: sent round again, or let go of.
///
/// Open to whoever may change the ticket in the first place, and to nobody else: a retry sends
/// what was already asked for, and letting go of it is a decision about that ticket's work. Both
/// are audited with who did it. Staff only; a client has no part in this.
/// </summary>
[ApiController]
[Route("api/tickets/{id:guid}/outbound")]
public sealed class TicketOutboundController(
    DeskDbContext db, ITicketScopeQuery scopeQuery, OutboundQueue queue, ICurrentUser user) : ControllerBase
{
    private async Task<OutboundOperation> OperationAsync(Guid ticketId, Guid operationId, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        // The ticket first, by the caller's own right to change it; then the change, which has to be this ticket's.
        _ = await scopeQuery.FindAsync(db.Tickets, ticketId, uid, Permissions.TicketsUpdate, ct) ?? throw new NotFoundException("Ticket");
        return await db.OutboundOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId && o.TicketId == ticketId, ct)
            ?? throw new NotFoundException("Outbound change");
    }

    /// <summary>Sends a failed change round again from the first try, or brings a waiting one forward to now.</summary>
    [HttpPost("{operationId:guid}/retry")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Retry(Guid id, Guid operationId, CancellationToken ct)
    {
        var op = await OperationAsync(id, operationId, ct);
        var again = await queue.RetryAsync(op.Id, user.DisplayName, ct);
        return Ok(new { id = again.Id, state = OutboundQueue.Label(again.State) });
    }

    /// <summary>Lets go of a change that has not reached the PSA. It is not sent.</summary>
    [HttpDelete("{operationId:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Discard(Guid id, Guid operationId, CancellationToken ct)
    {
        var op = await OperationAsync(id, operationId, ct);
        await queue.DiscardAsync(op.Id, user.DisplayName, ct);
        return NoContent();
    }
}
