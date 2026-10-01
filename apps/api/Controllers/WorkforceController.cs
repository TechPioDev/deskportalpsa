using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Workforce;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// The workforce foundation: when people normally work (a capacity boundary, not attendance) and
/// what they are skilled in. Staff only - every action needs schedule.view, which no client role
/// holds, and changes need workforce.manage. Who can be seen is decided per person by the caller's
/// schedule.view scope in the services; here only the claim and the feature switch are checked.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceController(
    IWorkScheduleService schedules, ISkillService skills, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    [HttpGet("people")]
    public async Task<IActionResult> People([FromQuery] PeopleQuery q, CancellationToken ct)
    {
        var me = Caller();
        var skillIds = (q.Skills ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        return Ok(await schedules.PeopleAsync(me, new WorkforceQuery(q.TeamId, q.DepartmentId, skillIds, q.MatchAll ?? false, q.IncludeInactive ?? false), ct));
    }

    [HttpGet("people/{id:guid}/schedule")]
    public async Task<IActionResult> Schedule(Guid id, CancellationToken ct)
        => Ok(await schedules.GetAsync(Caller(), id, ct));

    [HttpPut("people/{id:guid}/schedule")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> SaveSchedule(Guid id, [FromBody] WorkScheduleInput input, CancellationToken ct)
        => Ok(await schedules.SaveAsync(Caller(), id, input, ct));

    [HttpDelete("people/{id:guid}/schedule/{effectiveFrom}")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> RemoveUpcoming(Guid id, DateOnly effectiveFrom, CancellationToken ct)
        => Ok(await schedules.RemoveUpcomingAsync(Caller(), id, effectiveFrom, ct));

    [HttpPost("people/{id:guid}/schedule/copy")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> CopySchedule(Guid id, [FromBody] CopyRequest req, CancellationToken ct)
        => Ok(new { copied = await schedules.CopyAsync(Caller(), id, req.ToUserIds ?? [], req.EffectiveFrom, ct) });

    [HttpPut("people/{id:guid}/schedulable")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> SetSchedulable(Guid id, [FromBody] SchedulableRequest req, CancellationToken ct)
        => Ok(await schedules.SetSchedulableAsync(Caller(), id, req.Schedulable, ct));

    [HttpGet("skills")]
    public async Task<IActionResult> Skills([FromQuery] bool includeInactive, CancellationToken ct)
    {
        Caller();
        return Ok(await skills.ListAsync(includeInactive, ct));
    }

    [HttpPost("skills")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> CreateSkill([FromBody] SkillRequest req, CancellationToken ct)
    {
        Caller();
        return Ok(await skills.CreateAsync(req.Name, req.Description, ct));
    }

    [HttpPut("skills/{id:guid}")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> UpdateSkill(Guid id, [FromBody] SkillUpdateRequest req, CancellationToken ct)
    {
        Caller();
        return Ok(await skills.UpdateAsync(id, req.Name, req.Description, req.IsActive, ct));
    }

    [HttpGet("people/{id:guid}/skills")]
    public async Task<IActionResult> PersonSkills(Guid id, CancellationToken ct)
        => Ok(await skills.ForPersonAsync(Caller(), id, ct));

    [HttpPost("people/{id:guid}/skills")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> AssignSkill(Guid id, [FromBody] AssignSkillRequest req, CancellationToken ct)
        => Ok(await skills.AssignAsync(Caller(), id, req.SkillId, req.Level ?? SkillLevel.Proficient, ct));

    [HttpDelete("people/{id:guid}/skills/{skillId:guid}")]
    [RequirePermission(Permissions.WorkforceManage)]
    public async Task<IActionResult> RemoveSkill(Guid id, Guid skillId, CancellationToken ct)
        => Ok(await skills.RemoveAsync(Caller(), id, skillId, ct));

    /// <summary>The signed-in staff member - and the module switched on. Off, it is "not found" throughout.</summary>
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    public sealed record PeopleQuery(Guid? TeamId = null, Guid? DepartmentId = null, string? Skills = null, bool? MatchAll = null, bool? IncludeInactive = null);

    public sealed record CopyRequest(IReadOnlyList<Guid>? ToUserIds, DateOnly? EffectiveFrom = null);

    public sealed record SchedulableRequest(bool Schedulable);

    public sealed record SkillRequest(string Name, string? Description = null);

    public sealed record SkillUpdateRequest(string Name, string? Description, bool IsActive);

    public sealed record AssignSkillRequest(Guid SkillId, SkillLevel? Level = null);
}
