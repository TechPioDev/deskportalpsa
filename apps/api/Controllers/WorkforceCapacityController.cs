using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Capacity, free time and availability: how much time each person is offered for planned work, what
/// is left of it, exactly when they are free, and whether a piece of work would fit.
///
/// INTERNAL ONLY. Every action needs schedule.view, which no client role holds; recording an
/// exception also needs availability.manage. Who can be seen or changed is decided per person in the
/// services, by the scope of those claims. Nothing here is ever part of what a client receives - a
/// ticket a client can see does not make its planning visible to them.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceCapacityController(
    ICapacityService capacity, ICapacityExceptionService exceptions, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    /// <summary>One person's capacity and free slots, per date (today when no dates are given).</summary>
    [HttpGet("people/{id:guid}/capacity")]
    public async Task<IActionResult> PersonCapacity(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await capacity.ForPersonAsync(Caller(), id, from, to, ct));

    /// <summary>Everyone the caller may see, for one date.</summary>
    [HttpGet("capacity")]
    public async Task<IActionResult> TeamCapacity([FromQuery] TeamQuery q, CancellationToken ct)
        => Ok(await capacity.ForTeamAsync(Caller(), new TeamCapacityQuery(q.Date, q.TeamId, q.DepartmentId, Ids(q.Skills), q.MatchAll ?? true), ct));

    /// <summary>
    /// Who has a continuous free slot long enough for a piece of work. A read, so a GET: it changes
    /// nothing, and stays usable while an administrator is viewing the portal as someone (read-only).
    /// </summary>
    [HttpGet("availability")]
    public async Task<IActionResult> FindAvailable([FromQuery] SearchQuery q, CancellationToken ct)
    {
        var me = Caller();
        if (q.From is null) throw new ValidationFailedException("Choose a date to search.");
        return Ok(await capacity.FindAsync(me, new AvailabilitySearch(q.From.Value, q.To, q.Duration ?? 0, q.Earliest, q.Latest, q.TimeZone,
            q.TeamId, q.DepartmentId, Ids(q.Skills), q.MatchAll ?? true, Ids(q.People)), ct));
    }

    /// <summary>
    /// Whether a proposed piece of work fits in someone's time, and what is in the way if not. Also a
    /// read: it reserves nothing, so whatever books the work must check again as it commits.
    /// </summary>
    [HttpGet("people/{id:guid}/conflicts")]
    public async Task<IActionResult> EvaluateConflicts(Guid id, [FromQuery] ProposalQuery q, CancellationToken ct)
    {
        var me = Caller();
        if (q.Start is null || q.End is null) throw new ValidationFailedException("Give the start and the end of the work.");
        return Ok(await capacity.EvaluateAsync(me, id, new ProposedWork(q.Start.Value, q.End.Value, q.Tentative ?? false, Ids(q.Skills)), ct));
    }

    [HttpGet("people/{id:guid}/exceptions")]
    public async Task<IActionResult> Exceptions(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await exceptions.ListAsync(Caller(), id, from, to, ct));

    [HttpPost("people/{id:guid}/exceptions")]
    [RequirePermission(Permissions.AvailabilityManage)]
    public async Task<IActionResult> AddException(Guid id, [FromBody] CapacityExceptionInput input, CancellationToken ct)
        => Ok(await exceptions.AddAsync(Caller(), id, input, ct));

    [HttpPut("people/{id:guid}/exceptions/{exceptionId:guid}")]
    [RequirePermission(Permissions.AvailabilityManage)]
    public async Task<IActionResult> UpdateException(Guid id, Guid exceptionId, [FromBody] CapacityExceptionInput input, CancellationToken ct)
        => Ok(await exceptions.UpdateAsync(Caller(), id, exceptionId, input, ct));

    [HttpDelete("people/{id:guid}/exceptions/{exceptionId:guid}")]
    [RequirePermission(Permissions.AvailabilityManage)]
    public async Task<IActionResult> RemoveException(Guid id, Guid exceptionId, CancellationToken ct)
    {
        await exceptions.RemoveAsync(Caller(), id, exceptionId, ct);
        return NoContent();
    }

    /// <summary>The signed-in staff member - and the module switched on. Off, it is "not found" throughout.</summary>
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    /// <summary>
    /// A comma-separated list of ids. A value that is not an id is refused rather than dropped: a
    /// filter that quietly ignores part of what was asked returns more people than were asked for.
    /// </summary>
    private static List<Guid> Ids(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : throw new ValidationFailedException("One of the values in a filter is not a valid id."))
            .ToList();

    public sealed record SearchQuery(DateOnly? From = null, DateOnly? To = null, int? Duration = null, string? Earliest = null, string? Latest = null,
        string? TimeZone = null, Guid? TeamId = null, Guid? DepartmentId = null, string? Skills = null, bool? MatchAll = null, string? People = null);

    public sealed record ProposalQuery(DateTimeOffset? Start = null, DateTimeOffset? End = null, bool? Tentative = null, string? Skills = null);

    public sealed record TeamQuery(DateOnly? Date = null, Guid? TeamId = null, Guid? DepartmentId = null, string? Skills = null, bool? MatchAll = null);
}
