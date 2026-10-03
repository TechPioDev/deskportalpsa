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
/// Work execution (Phase 6): a technician's clock on a piece of work (start, pause, resume, stop),
/// their day (planned against actual), and a manager's "team today". Staff only, behind the
/// Workforce switch: a client account is refused before any action runs. Nothing here is attendance:
/// a session is attached to a ticket, never to a person's presence.
///
/// Reads need <c>schedule.view</c> (one's own day at Own scope; the team at a wider scope). Running
/// a clock is logging time, so it needs <c>tickets.time.log</c> on the ticket, exactly as typing
/// an hour into the time panel does.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceTimeController(IWorkTimeService time, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    /// <summary>The caller's running or paused work: what a header indicator shows, and how a reloaded screen recovers its clock.</summary>
    [HttpGet("work/active")]
    public async Task<IActionResult> Active(CancellationToken ct) => Ok(await time.ActiveAsync(Caller(), ct));

    [HttpPost("work/start")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Start([FromBody] StartWorkInput input, CancellationToken ct) => Ok(await time.StartAsync(Caller(), input, ct));

    [HttpPost("work/{id:guid}/pause")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Pause(Guid id, [FromBody] WorkSessionStateInput input, CancellationToken ct) => Ok(await time.PauseAsync(Caller(), id, input, ct));

    [HttpPost("work/{id:guid}/resume")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Resume(Guid id, [FromBody] WorkSessionStateInput input, CancellationToken ct) => Ok(await time.ResumeAsync(Caller(), id, input, ct));

    [HttpPost("work/{id:guid}/stop")]
    [RequirePermission(Permissions.TicketsLogTime)]
    public async Task<IActionResult> Stop(Guid id, [FromBody] StopWorkInput input, CancellationToken ct) => Ok(await time.StopAsync(Caller(), id, input, ct));

    /// <summary>A day: the caller's own, or (for someone who may see them) another person's.</summary>
    [HttpGet("my-day")]
    public async Task<IActionResult> MyDay([FromQuery] Guid? appUserId, [FromQuery] DateOnly? date, CancellationToken ct)
        => Ok(await time.MyDayAsync(Caller(), appUserId, date, ct));

    [HttpGet("team-today")]
    public async Task<IActionResult> TeamToday([FromQuery] DateOnly? date, [FromQuery] Guid? teamId, [FromQuery] Guid? departmentId, [FromQuery] string? skills, [FromQuery] bool matchAll = true, CancellationToken ct = default)
    {
        var skillIds = (skills ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        return Ok(await time.TeamTodayAsync(Caller(), new TeamPlanQuery(date, date, teamId, departmentId, skillIds.Count == 0 ? null : skillIds, matchAll), ct));
    }
}
