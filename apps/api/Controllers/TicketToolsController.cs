using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Boards;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Canned responses and the task list inside a ticket. Not behind the boards switch: both are just as
/// useful on a PSA ticket — a courtesy reply to a client, the steps of a job the PSA only knows as one
/// line — and neither is ever sent to the provider.
/// </summary>
[ApiController]
[Authorize]
public sealed class TicketToolsController(ICannedResponseService canned, ITicketTaskService tasks) : ControllerBase
{
    // ---- canned responses -----------------------------------------------------------------------

    /// <summary>The responses offered while replying to this ticket: everywhere's, plus its board's.</summary>
    [HttpGet("api/tickets/{id:guid}/canned-responses")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> ForTicket(Guid id, CancellationToken ct)
        => Ok(await canned.ListAsync(id, includeInactive: false, ct));

    [HttpGet("api/canned-responses")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await canned.ListAsync(null, includeInactive, ct));

    [HttpPost("api/canned-responses")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Create([FromBody] CannedResponseInput input, CancellationToken ct)
        => Ok(await canned.SaveAsync(null, input, ct));

    [HttpPut("api/canned-responses/{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CannedResponseInput input, CancellationToken ct)
        => Ok(await canned.SaveAsync(id, input, ct));

    [HttpPut("api/canned-responses/{id:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] BoardsController.SetActiveRequest req, CancellationToken ct)
    {
        await canned.SetActiveAsync(id, req.Active, ct);
        return NoContent();
    }

    // ---- tasks ----------------------------------------------------------------------------------
    // Staff only: the list is the team's working notes, and a client never sees it.

    [HttpGet("api/tickets/{id:guid}/tasks")]
    [RequirePermission(Permissions.TicketsViewAll)]
    public async Task<IActionResult> Tasks(Guid id, CancellationToken ct)
        => Ok(await tasks.ListAsync(id, ct));

    [HttpPost("api/tickets/{id:guid}/tasks")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> AddTask(Guid id, [FromBody] TaskRequest req, CancellationToken ct)
        => Ok(await tasks.AddAsync(id, req.Title, req.AssignedAppUserId, ct));

    [HttpPut("api/tickets/tasks/{taskId:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> UpdateTask(Guid taskId, [FromBody] TaskRequest req, CancellationToken ct)
        => Ok(await tasks.UpdateAsync(taskId, req.Title, req.AssignedAppUserId, ct));

    [HttpPut("api/tickets/tasks/{taskId:guid}/done")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> SetDone(Guid taskId, [FromBody] DoneRequest req, CancellationToken ct)
        => Ok(await tasks.SetDoneAsync(taskId, req.Done, ct));

    [HttpPut("api/tickets/tasks/{taskId:guid}/move")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Move(Guid taskId, [FromBody] MoveRequest req, CancellationToken ct)
        => Ok(await tasks.MoveAsync(taskId, req.Offset, ct));

    [HttpDelete("api/tickets/tasks/{taskId:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> DeleteTask(Guid taskId, CancellationToken ct)
        => Ok(await tasks.DeleteAsync(taskId, ct));

    public sealed record TaskRequest([Required, StringLength(300, MinimumLength = 1)] string Title, Guid? AssignedAppUserId = null);
    public sealed record DoneRequest(bool Done);
    /// <param name="Offset">-1 moves the task up one place, 1 down one place.</param>
    public sealed record MoveRequest([Range(-1, 1)] int Offset);
}
