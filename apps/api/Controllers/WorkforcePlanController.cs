using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Planned work: a person's plan, the work of theirs not yet in it, and placing, moving, giving away
/// and taking out work. INTERNAL ONLY: every action needs schedule.view (no client role holds it) and
/// every change needs schedule.manage; whose plan the caller may touch is decided per person in the
/// service by that permission's scope. Nothing here is ever part of what a client receives.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforcePlanController(IWorkPlanService plans, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    /// <summary>One person's plan: capacity per day with planned work counted, and the work itself.</summary>
    [HttpGet("people/{id:guid}/plan")]
    public async Task<IActionResult> Plan(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await plans.PlanAsync(Caller(), id, from, to, ct));

    /// <summary>The caller's own open work that is not yet in their plan.</summary>
    [HttpGet("plan/unscheduled")]
    public async Task<IActionResult> Unscheduled(CancellationToken ct)
        => Ok(await plans.UnscheduledAsync(Caller(), ct));

    /// <summary>Who the caller may plan work for.</summary>
    [HttpGet("plan/people")]
    public async Task<IActionResult> PlannablePeople(CancellationToken ct)
        => Ok(await plans.PlannablePeopleAsync(Caller(), ct));

    /// <summary>What is planned on a ticket, for the people the caller may see. Staff only; never on the ticket itself.</summary>
    [HttpGet("tickets/{ticketId:guid}/plan")]
    public async Task<IActionResult> ForTicket(Guid ticketId, CancellationToken ct)
        => Ok(await plans.ForTicketAsync(Caller(), ticketId, ct));

    [HttpPost("plan")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Create([FromBody] WorkAllocationInput input, CancellationToken ct)
        => Ok(await plans.CreateAsync(Caller(), input, ct));

    /// <summary>A small piece of internal work with no ticket yet: raised on a board and planned in one step.</summary>
    [HttpPost("plan/internal-work")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> CreateInternalWork([FromBody] InternalWorkInput input, CancellationToken ct)
        => Ok(await plans.CreateInternalWorkAsync(Caller(), input, ct));

    [HttpPut("plan/{allocationId:guid}")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Update(Guid allocationId, [FromBody] WorkAllocationUpdate input, CancellationToken ct)
        => Ok(await plans.UpdateAsync(Caller(), allocationId, input, ct));

    [HttpPost("plan/{allocationId:guid}/reassign")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Reassign(Guid allocationId, [FromBody] WorkAllocationReassign input, CancellationToken ct)
        => Ok(await plans.ReassignAsync(Caller(), allocationId, input, ct));

    /// <summary>Takes the work out of the plan. The ticket itself is untouched.</summary>
    [HttpDelete("plan/{allocationId:guid}")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Cancel(Guid allocationId, [FromQuery] string? reason, CancellationToken ct)
        => Ok(await plans.CancelAsync(Caller(), allocationId, reason, ct));

    /// <summary>The signed-in staff member - and the module switched on. Off, it is "not found" throughout.</summary>
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }
}
