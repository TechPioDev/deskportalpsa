using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Staff status changes (incl. closing a ticket). The change itself — mapping to the PSA's value,
/// pushing it, the SLA pause and the task check — is <see cref="TicketStatusWriter"/>'s, shared with
/// approvals. Gated by <see cref="Permissions.TicketsUpdate"/>.
/// </summary>
// Class-level [Authorize] as a floor: every action here also carries [RequirePermission],
// but that is opt-in per action — an action added later without one would otherwise be
// reachable anonymously. This makes authentication the default and the omission harmless.
[Authorize]
[ApiController]
[Route("api/tickets")]
public sealed class TicketStatusController(
    DeskDbContext db,
    TicketStatusWriter writer,
    ITicketScopeQuery scopeQuery,
    ICurrentUser user) : ControllerBase
{
    [HttpPost("{id:guid}/status")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetStatusRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Status))
            throw new ValidationFailedException("A status is required.");

        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        var ticket = await scopeQuery.FindAsync(db.Tickets, id, uid, Permissions.TicketsUpdate, ct)
            ?? throw new NotFoundException("Ticket");

        return Ok(new { portalStatus = await writer.SetAsync(ticket, req.Status, req.Resolution, ct) });
    }

    // The resolution's length is checked by TicketStatusWriter, not by an attribute here: a validation
    // attribute on a record property makes MVC fail every request to this endpoint.
    public sealed record SetStatusRequest(string Status, string? Resolution = null);
}
