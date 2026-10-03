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

    /// <summary>The team scheduler: the people the caller may see (narrowed), their days and what is planned in them.</summary>
    [HttpGet("plan/team")]
    public async Task<IActionResult> Team([FromQuery] TeamPlanRequest q, CancellationToken ct)
        => Ok(await plans.TeamAsync(Caller(), q.ToQuery(), ct));

    /// <summary>Open work in the group's hands that nobody has planned yet.</summary>
    [HttpGet("plan/unscheduled/team")]
    public async Task<IActionResult> UnscheduledTeam([FromQuery] TeamPlanRequest q, CancellationToken ct)
        => Ok(await plans.UnscheduledTeamAsync(Caller(), q.ToQuery(), ct));

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

    /// <summary>Pencilled-in work becomes committed, after a fresh check of everything.</summary>
    [HttpPost("plan/{allocationId:guid}/confirm")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Confirm(Guid allocationId, [FromBody] WorkAllocationStateInput input, CancellationToken ct)
        => Ok(await plans.ConfirmAsync(Caller(), allocationId, input, ct));

    /// <summary>Committed work becomes pencilled in. Only someone who schedules others.</summary>
    [HttpPost("plan/{allocationId:guid}/tentative")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> MakeTentative(Guid allocationId, [FromBody] WorkAllocationStateInput input, CancellationToken ct)
        => Ok(await plans.MakeTentativeAsync(Caller(), allocationId, input, ct));

    /// <summary>What planning a piece of work needs to know: effort, window, splittable, a skill; with what is allocated so far.</summary>
    [HttpGet("plan/requirements/{ticketId:guid}")]
    public async Task<IActionResult> Requirement(Guid ticketId, CancellationToken ct)
        => Ok(await plans.RequirementAsync(Caller(), ticketId, ct));

    [HttpPut("plan/requirements/{ticketId:guid}")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> SetRequirement(Guid ticketId, [FromBody] PlanningRequirementInput input, CancellationToken ct)
        => Ok(await plans.SetRequirementAsync(Caller(), ticketId, input, ct));

    /// <summary>The planning queue: the group's unscheduled work, why it waits, and demand against free capacity over the horizon (at most 14 days).</summary>
    [HttpGet("plan/queue")]
    public async Task<IActionResult> Queue([FromQuery] TeamPlanRequest q, [FromQuery] int? horizonDays, CancellationToken ct)
        => Ok(await plans.QueueAsync(Caller(), q.ToQuery(), horizonDays ?? 14, ct));

    /// <summary>What placing this effort in this window would look like. A read, so a GET: nothing is written and nothing is reserved.</summary>
    [HttpGet("plan/preview")]
    public async Task<IActionResult> Preview([FromQuery] PreviewRequest q, CancellationToken ct)
        => Ok(await plans.PreviewAsync(Caller(), q.ToInput(), ct));

    /// <summary>Writes a preview's pieces if the plan is still what the preview saw; otherwise 409 with a fresh preview.</summary>
    [HttpPost("plan/preview/confirm")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> ConfirmPreview([FromBody] PlanConfirmInput input, CancellationToken ct)
        => Ok(await plans.ConfirmPreviewAsync(Caller(), input, ct));

    /// <summary>Takes the work out of the plan. The ticket itself is untouched.</summary>
    [HttpDelete("plan/{allocationId:guid}")]
    [RequirePermission(Permissions.ScheduleManage)]
    public async Task<IActionResult> Cancel(Guid allocationId, [FromQuery] string? reason, CancellationToken ct)
        => Ok(await plans.CancelAsync(Caller(), allocationId, reason, ct));

    public sealed record PreviewRequest(Guid TicketId, Guid AppUserId, DateTimeOffset Earliest, DateTimeOffset Latest, int Minutes, bool? Splittable = null, bool? Tentative = null, int? MinChunk = null)
    {
        public PlanPreviewInput ToInput() => new(TicketId, AppUserId, Earliest, Latest, Minutes, Splittable ?? false, Tentative ?? false, MinChunk ?? 30);
    }

    public sealed record TeamPlanRequest(DateOnly? From = null, DateOnly? To = null, Guid? TeamId = null, Guid? DepartmentId = null, string? Skills = null, bool? MatchAll = null)
    {
        public TeamPlanQuery ToQuery() => new(From, To, TeamId, DepartmentId, Ids(Skills), MatchAll ?? true);

        private static List<Guid> Ids(string? csv)
            => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : throw new ValidationFailedException("One of the values in a filter is not a valid id."))
                .ToList();
    }

    /// <summary>The signed-in staff member - and the module switched on. Off, it is "not found" throughout.</summary>
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }
}
