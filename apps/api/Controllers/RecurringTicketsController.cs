using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Desk.Api.Controllers;

/// <summary>
/// Recurring tickets and the desk's holidays. Both are a lead's to manage, and both belong to the
/// team's own boards, so they sit behind the same switch as the boards.
/// </summary>
[ApiController]
[Route("api/boards")]
[Authorize]
public sealed class RecurringTicketsController(
    IRecurringTicketService recurring, ISlaPlanService plans, ICurrentUser user, BoardFeatureOptions features)
    : ControllerBase, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!features.InternalBoards) throw new NotFoundException("Internal boards");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    [HttpGet("recurring")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await recurring.ListAsync(includeInactive, ct));

    [HttpPost("recurring")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Create([FromBody] RecurringTicketInput input, CancellationToken ct)
        => Ok(await recurring.SaveAsync(null, input, Me(), ct));

    [HttpPut("recurring/{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] RecurringTicketInput input, CancellationToken ct)
        => Ok(await recurring.SaveAsync(id, input, Me(), ct));

    [HttpPut("recurring/{id:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] BoardsController.SetActiveRequest req, CancellationToken ct)
    {
        await recurring.SetActiveAsync(id, req.Active, ct);
        return NoContent();
    }

    [HttpDelete("recurring/{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await recurring.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Raise it now, to see that the schedule makes the ticket that was meant.</summary>
    [HttpPost("recurring/{id:guid}/run")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
        => Ok(await recurring.RunNowAsync(id, ct));

    // ---- holidays -------------------------------------------------------------------------------

    /// <summary>Upcoming closed days. Readable by anyone raising tickets — the SLA page shows them.</summary>
    [HttpGet("holidays")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Holidays([FromQuery] DateOnly? from, CancellationToken ct)
        => Ok(await plans.HolidaysAsync(from, ct));

    [HttpPost("holidays")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> AddHoliday([FromBody] HolidayRequest req, CancellationToken ct)
        => Ok(await plans.AddHolidayAsync(req.Date, req.Name, ct));

    [HttpDelete("holidays/{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> RemoveHoliday(Guid id, CancellationToken ct)
    {
        await plans.RemoveHolidayAsync(id, ct);
        return NoContent();
    }

    private Guid Me() => user.UserId ?? throw new ForbiddenException("Recurring tickets are raised in a staff member's name.");

    public sealed record HolidayRequest(DateOnly Date, string Name);
}
