using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Reviewing resolved work, and linking tickets. Staff only; every ticket is found through the
/// person's own scope, so a ticket they cannot see is not found here either.
/// </summary>
[Authorize]
[ApiController]
[Route("api/tickets")]
[RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
public sealed class TicketWorkflowController(
    DeskDbContext db, ITicketScopeQuery scopeQuery, ICurrentUser user,
    ITicketReviewService reviews, ITicketLinkService links) : ControllerBase
{
    /// <summary>
    /// Approve resolved work (it closes) or send it back with a note (it returns to work). A board
    /// lead's call - the same key that decides what a board is - and never on one's own ticket.
    /// </summary>
    [HttpPost("{id:guid}/review")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Review(Guid id, [FromBody] ReviewRequest req, CancellationToken ct)
    {
        var (ticket, uid) = await FindAsync(id, Permissions.TicketsUpdate, ct);
        await reviews.ReviewAsync(ticket, uid, user.DisplayName ?? user.Email ?? "Reviewer", req.Approve, req.Note, ct);
        return Ok(new { ticket.PortalStatus, ticket.ReviewState });
    }

    [HttpGet("{id:guid}/links")]
    public async Task<IActionResult> Links(Guid id, CancellationToken ct)
    {
        var (ticket, uid) = await FindAsync(id, Permissions.TicketsViewAll, ct);
        return Ok(await links.ListAsync(ticket, uid, ct));
    }

    [HttpPost("{id:guid}/links")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> AddLink(Guid id, [FromBody] LinkRequest req, CancellationToken ct)
    {
        var (ticket, uid) = await FindAsync(id, Permissions.TicketsUpdate, ct);
        return Ok(await links.AddAsync(ticket, uid, req.OtherTicketId, req.Kind, ct));
    }

    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> RemoveLink(Guid id, Guid linkId, CancellationToken ct)
    {
        var (ticket, uid) = await FindAsync(id, Permissions.TicketsUpdate, ct);
        await links.RemoveAsync(ticket, uid, linkId, ct);
        return NoContent();
    }

    private async Task<(Ticket Ticket, Guid UserId)> FindAsync(Guid id, string permission, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        var ticket = await scopeQuery.FindAsync(db.Tickets, id, uid, permission, ct) ?? throw new NotFoundException("Ticket");
        return (ticket, uid);
    }

    public sealed record ReviewRequest(bool Approve, string? Note = null);

    public sealed record LinkRequest(Guid OtherTicketId, TicketLinkKind Kind);
}
