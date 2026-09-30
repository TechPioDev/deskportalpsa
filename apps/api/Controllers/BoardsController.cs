using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Filters;

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
// Staff only, on top of each action's own key. Clients hold tickets.create (to raise their own
// tickets), which alone opened these routes to them: every internal board's name, its open count
// and its default assignees. The team's boards are the team's business.
[RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
public sealed class BoardsController(
    IBoardService boards, IInternalTicketService tickets, ICurrentUser user, BoardFeatureOptions features)
    : ControllerBase, IActionFilter
{
    /// <summary>
    /// Refuses every route here when the feature is switched off. Tickets already raised on a board
    /// are untouched and readable again the moment it is switched back on.
    /// </summary>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!features.InternalBoards) throw new NotFoundException("Internal boards");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

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

    /// <summary>What tickets on this board can be about. Anyone who raises tickets needs to read these.</summary>
    [HttpGet("{id:guid}/topics")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Topics(Guid id, [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await boards.TopicsAsync(id, includeInactive, ct));

    [HttpPost("{id:guid}/topics")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> AddTopic(Guid id, [FromBody] BoardTopicInput input, CancellationToken ct)
        => Ok(await boards.AddTopicAsync(id, input, ct));

    [HttpPut("topics/{topicId:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> UpdateTopic(Guid topicId, [FromBody] BoardTopicInput input, CancellationToken ct)
        => Ok(await boards.UpdateTopicAsync(topicId, input, ct));

    [HttpPut("topics/{topicId:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetTopicActive(Guid topicId, [FromBody] SetActiveRequest req, CancellationToken ct)
    {
        await boards.SetTopicActiveAsync(topicId, req.Active, ct);
        return NoContent();
    }

    /// <summary>
    /// The departments a ticket can be routed to, for the raise form. Deliberately its own endpoint:
    /// the administration one needs the permission to MANAGE people, and a technician raising a
    /// ticket needs to read this list without being able to change anybody's department.
    /// </summary>
    [HttpGet("departments")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Departments([FromServices] DeskDbContext db, CancellationToken ct)
        => Ok(await db.Departments.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.IsActive })
            .ToListAsync(ct));

    /// <summary>The short list of ways work reaches the desk, for the raise form's own dropdown.</summary>
    [HttpGet("sources/options")]
    [RequirePermission(Permissions.TicketsCreate)]
    public IActionResult SourceOptions() => Ok(Desk.Infrastructure.Boards.InternalTicketService.Sources);

    // ── Alert sources: which monitoring tools may open tickets here ──────────────────────────
    // A lead's decision, like the boards themselves, so the same permission governs it.

    [HttpGet("sources")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Sources([FromServices] IAlertSourceService sources, CancellationToken ct)
        => Ok(await sources.ListAsync(ct));

    /// <summary>Creates a source and returns its key ONCE. It is never retrievable again.</summary>
    [HttpPost("sources")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> CreateSource(
        [FromServices] IAlertSourceService sources, [FromBody] AlertSourceInput input, CancellationToken ct)
        => Ok(await sources.CreateAsync(input, ct));

    [HttpPut("sources/{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> UpdateSource(
        Guid id, [FromServices] IAlertSourceService sources, [FromBody] AlertSourceInput input, CancellationToken ct)
        => Ok(await sources.UpdateAsync(id, input, ct));

    [HttpPut("sources/{id:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetSourceActive(
        Guid id, [FromServices] IAlertSourceService sources, [FromBody] SetActiveRequest req, CancellationToken ct)
    {
        await sources.SetActiveAsync(id, req.Active, ct);
        return NoContent();
    }

    /// <summary>Issues a new key; the old one stops working immediately.</summary>
    [HttpPost("sources/{id:guid}/key")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> RegenerateKey(Guid id, [FromServices] IAlertSourceService sources, CancellationToken ct)
        => Ok(await sources.RegenerateKeyAsync(id, ct));

    public sealed record SetActiveRequest(bool Active);
    public sealed record SetMembersRequest(IReadOnlyList<Guid>? AppUserIds);
}
