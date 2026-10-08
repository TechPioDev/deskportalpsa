using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Moving a ticket's due date later. The same right that changes a ticket's status, reached by
/// the same scope: a ticket the caller may not change is not found.
/// </summary>
[ApiController]
[Route("api/tickets")]
public sealed class TicketDueDateController(
    DeskDbContext db, ITicketDueDateService dueDates, ITicketScopeQuery scopeQuery, ICurrentUser user) : ControllerBase
{
    public sealed record ExtendRequest(DateTimeOffset DueAt, string Reason);

    [HttpPost("{id:guid}/due-date")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Extend(Guid id, [FromBody] ExtendRequest req, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        var ticket = await scopeQuery.FindAsync(db.Tickets, id, uid, Permissions.TicketsUpdate, ct)
            ?? throw new NotFoundException("Ticket");
        return Ok(await dueDates.ExtendAsync(ticket, new ExtendDueDateInput(req.DueAt, req.Reason ?? ""), ct));
    }
}
