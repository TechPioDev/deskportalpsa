using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// Staff status changes (incl. closing a ticket). Maps the chosen portal-neutral status to the PSA's
/// real value via the mapping rules, pushes it to the provider (PSA = system of record), then updates
/// the portal projection. Gated by <see cref="Permissions.TicketsUpdate"/>.
/// </summary>
// Class-level [Authorize] as a floor: every action here also carries [RequirePermission],
// but that is opt-in per action — an action added later without one would otherwise be
// reachable anonymously. This makes authentication the default and the omission harmless.
[Authorize]
[ApiController]
[Route("api/tickets")]
public sealed class TicketStatusController(
    DeskDbContext db,
    IConnectorResolver connectors,
    IMappingEngine mapping,
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
        var portalStatus = req.Status.Trim();

        // A ticket on the team's own board has no provider to agree with: the portal's status IS
        // the status, so it is set here and nothing is pushed.
        if (ticket.Origin != TicketOrigin.Psa || ticket.PsaConnectionId is not { } connectionId || ticket.Provider is not { } provider)
        {
            ticket.PortalStatus = portalStatus;
            if (Closed(portalStatus)) ticket.ClosedAt ??= DateTimeOffset.UtcNow;
            if (Resolved(portalStatus)) ticket.ResolvedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Ok(new { portalStatus = ticket.PortalStatus });
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
        return Ok(new { portalStatus = ticket.PortalStatus });
    }

    /// <summary>
    /// Closing dates on a board with no provider. Every report that measures resolution time reads
    /// these, so an internal ticket that is closed without one would silently sit outside its own
    /// figures — the exact fault the attention list reports for ConnectWise today.
    /// </summary>
    private static bool Closed(string status) => status.Contains("CLOSED", StringComparison.OrdinalIgnoreCase);
    private static bool Resolved(string status) =>
        Closed(status) || status.Contains("RESOLV", StringComparison.OrdinalIgnoreCase);

    public sealed record SetStatusRequest(string Status);
}
