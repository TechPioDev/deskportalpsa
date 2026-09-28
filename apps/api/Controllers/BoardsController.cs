using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// The team's own boards, and the tickets raised on them.
///
/// Two different permissions on purpose: configuring what boards exist is a lead's decision
/// (boards.manage), while raising a ticket and handing it to a colleague is something anybody on
/// the team does all day (tickets.create).
/// </summary>
[ApiController]
[Route("api/boards")]
[Authorize]
public sealed class BoardsController(IBoardService boards, IInternalTicketService tickets, ICurrentUser user) : ControllerBase
{
    /// <summary>The boards this member of staff can work on.</summary>
    [HttpGet]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await boards.ListAsync(includeInactive, ct));

    [HttpPost]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Create([FromBody] BoardInput input, CancellationToken ct)
        => Ok(await boards.CreateAsync(input, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] BoardInput input, CancellationToken ct)
        => Ok(await boards.UpdateAsync(id, input, ct));

    [HttpPut("{id:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] SetActiveRequest req, CancellationToken ct)
    {
        await boards.SetActiveAsync(id, req.Active, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/members")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Members(Guid id, CancellationToken ct)
        => Ok(await boards.MembersAsync(id, ct));

    /// <summary>Replaces the membership. An empty list reopens the board to the whole team.</summary>
    [HttpPut("{id:guid}/members")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetMembers(Guid id, [FromBody] SetMembersRequest req, CancellationToken ct)
        => Ok(await boards.SetMembersAsync(id, req.AppUserIds ?? [], ct));

    /// <summary>
    /// Raises a ticket on a board. Open to anybody who may create tickets, because the team assigns
    /// work to each other rather than waiting for a lead to route it.
    /// </summary>
    [HttpPost("tickets")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> CreateTicket([FromBody] InternalTicketInput input, CancellationToken ct)
    {
        if (user.UserId is not { } uid)
            throw new ForbiddenException("This endpoint is for members of staff.");
        return Ok(await tickets.CreateAsync(uid, input, ct));
    }

    public sealed record SetActiveRequest(bool Active);
    public sealed record SetMembersRequest(IReadOnlyList<Guid>? AppUserIds);
}
