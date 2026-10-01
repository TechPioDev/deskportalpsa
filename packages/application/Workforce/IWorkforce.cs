using Desk.Domain.Workforce;

namespace Desk.Application.Workforce;

/// <summary>
/// One switch over the whole workforce module. Off by default: an installation shows nothing of it
/// until an administrator turns it on, so it can be deployed and checked before anyone relies on it.
/// </summary>
public sealed class WorkforceFeatureOptions
{
    public bool Enabled { get; init; }
}

/// <summary>A break as the screen and the API carry it ("12:30" - "13:30").</summary>
public sealed record WorkBreakDto(string Start, string End);

/// <summary>
/// One working weekday. <see cref="CrossesMidnight"/> and the minute figures are worked out by the
/// server so every screen shows the same arithmetic.
/// </summary>
public sealed record WorkDayDto(
    DayOfWeek Day, string Start, string End, IReadOnlyList<WorkBreakDto> Breaks,
    bool CrossesMidnight, int GrossMinutes, int BreakMinutes, int UsableMinutes);

/// <summary>A schedule version: from which day, in which zone, and its working days.</summary>
public sealed record WorkScheduleVersionDto(
    DateOnly EffectiveFrom, string TimeZone, IReadOnlyList<WorkDayDto> Days, int WeeklyUsableMinutes,
    DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>
/// Someone's schedule: the version in force today, the next one already set to start (if any), and
/// whether they are offered for planned work at all.
/// </summary>
public sealed record PersonScheduleDto(
    Guid AppUserId, string DisplayName, bool IsActive, bool IsSchedulable, string OrganizationTimeZone,
    WorkScheduleVersionDto? Current, WorkScheduleVersionDto? Upcoming, IReadOnlyList<DateOnly> Versions,
    bool CanManage);

/// <summary>A day as submitted. Weekdays left out are days off.</summary>
public sealed record WorkDayInput(DayOfWeek Day, string Start, string End, IReadOnlyList<WorkBreakDto>? Breaks);

/// <summary>A schedule as submitted: it starts on <see cref="EffectiveFrom"/> (today if omitted).</summary>
public sealed record WorkScheduleInput(DateOnly? EffectiveFrom, string TimeZone, IReadOnlyList<WorkDayInput> Days);

public sealed record SkillDto(Guid Id, string Name, string? Description, bool IsActive, int HolderCount);

public sealed record StaffSkillDto(Guid SkillId, string Name, SkillLevel Level, bool SkillIsActive);

/// <summary>A row in the workforce overview: who, where, when they work, and what they know.</summary>
public sealed record WorkforcePersonDto(
    Guid AppUserId, string DisplayName, string Email, bool IsActive, bool IsSchedulable,
    IReadOnlyList<string> Teams, IReadOnlyList<string> Departments,
    string? TimeZone, int? WeeklyUsableMinutes, string? ScheduleSummary,
    IReadOnlyList<StaffSkillDto> Skills);

/// <summary>Filters for the overview: team, department, and skills (any or all of them).</summary>
public sealed record WorkforceQuery(Guid? TeamId = null, Guid? DepartmentId = null, IReadOnlyList<Guid>? SkillIds = null,
    bool MatchAllSkills = false, bool IncludeInactive = false);

/// <summary>
/// Working schedules. Reads are limited to the people the caller's schedule.view scope reaches;
/// changes need workforce.manage. Every change is audited with what it was and what it became.
/// </summary>
public interface IWorkScheduleService
{
    Task<PersonScheduleDto> GetAsync(Guid callerId, Guid appUserId, CancellationToken ct = default);
    Task<PersonScheduleDto> SaveAsync(Guid callerId, Guid appUserId, WorkScheduleInput input, CancellationToken ct = default);
    Task<PersonScheduleDto> RemoveUpcomingAsync(Guid callerId, Guid appUserId, DateOnly effectiveFrom, CancellationToken ct = default);
    /// <summary>The schedule in force for <paramref name="fromUserId"/> copied to others, from a day.</summary>
    Task<int> CopyAsync(Guid callerId, Guid fromUserId, IReadOnlyList<Guid> toUserIds, DateOnly? effectiveFrom, CancellationToken ct = default);
    Task<PersonScheduleDto> SetSchedulableAsync(Guid callerId, Guid appUserId, bool schedulable, CancellationToken ct = default);
    Task<IReadOnlyList<WorkforcePersonDto>> PeopleAsync(Guid callerId, WorkforceQuery query, CancellationToken ct = default);
}

/// <summary>The skill catalogue and who holds which skill.</summary>
public interface ISkillService
{
    Task<IReadOnlyList<SkillDto>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<SkillDto> CreateAsync(string name, string? description, CancellationToken ct = default);
    Task<SkillDto> UpdateAsync(Guid id, string name, string? description, bool isActive, CancellationToken ct = default);
    Task<IReadOnlyList<StaffSkillDto>> ForPersonAsync(Guid callerId, Guid appUserId, CancellationToken ct = default);
    Task<IReadOnlyList<StaffSkillDto>> AssignAsync(Guid callerId, Guid appUserId, Guid skillId, SkillLevel level, CancellationToken ct = default);
    Task<IReadOnlyList<StaffSkillDto>> RemoveAsync(Guid callerId, Guid appUserId, Guid skillId, CancellationToken ct = default);
}
