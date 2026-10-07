using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    ICurrentUser user,
    Desk.Infrastructure.Sync.OutboundQueue outbound) : ControllerBase
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

        try
        {
            return Ok(new { portalStatus = await writer.SetAsync(ticket, req.Status, req.Resolution, ct) });
        }
        catch (Desk.PsaCore.Contracts.ConnectorException away) when (away.IsTransient && ticket.PsaConnectionId is not null)
        {
            // The PSA could not be reached. A PSA ticket's status is the PSA's to hold, so the
            // ticket goes on showing the status it has: what was asked for is kept as asked for,
            // and becomes the status only when the PSA has accepted it. Everything the portal
            // itself requires of the change (a resolution, no open tasks, review) was checked
            // before the PSA was called, so what waits is a change that may be made.
            var to = req.Status.Trim();
            var already = await db.OutboundOperations.AsNoTracking()
                .Where(o => o.TicketId == ticket.Id && o.Kind == Desk.Domain.Sync.OutboundKind.StatusChange && o.State == Desk.Domain.Sync.OutboundState.Pending)
                .Select(o => o.Summary).FirstOrDefaultAsync(ct);
            if (already is not null)
                throw new ValidationFailedException($"A change is already waiting to reach the PSA for this ticket ({already}). Let go of it first, or wait for it.");

            var op = outbound.Enqueue(ticket, Desk.Domain.Sync.OutboundKind.StatusChange, $"Status to {to}",
                new Desk.Infrastructure.Sync.OutboundStatus(to, ticket.PortalStatus, string.IsNullOrWhiteSpace(req.Resolution) ? null : req.Resolution.Trim(), uid),
                null, Guid.NewGuid().ToString("N"), uid, null, user.DisplayName,
                Desk.Infrastructure.Sync.OutboundProcessor.Unreachable(away), uncertain: false);
            await db.SaveChangesAsync(ct);
            await outbound.AuditQueuedAsync(op, ct);
            return Accepted(new
            {
                portalStatus = ticket.PortalStatus, queued = true, requested = to, operationId = op.Id,
                state = Desk.Infrastructure.Sync.OutboundQueue.PendingLabel,
            });
        }
    }

    // The resolution's length is checked by TicketStatusWriter, not by an attribute here: a validation
    // attribute on a record property makes MVC fail every request to this endpoint.
    public sealed record SetStatusRequest(string Status, string? Resolution = null);
}
