using Desk.Domain.Workforce;

namespace Desk.Application.Workforce;

// Capacity, availability and free time. INTERNAL ONLY: every type here is for staff planning work.
// None of it may be added to anything a client can receive - a ticket a client can see does not
// make who is planned on it, or when, visible to them.

/// <summary>A stretch of real time and its length.</summary>
public sealed record SlotDto(DateTimeOffset Start, DateTimeOffset End, int Minutes);

/// <summary>A capacity exception as the screen and the API carry it.</summary>
/// <param name="StartTime">Wall-clock start in <paramref name="TimeZone"/> ("15:00"); null when all-day.</param>
public sealed record CapacityExceptionDto(
    Guid Id, Guid AppUserId, CapacityExceptionKind Kind, bool AllDay,
    DateOnly FromDate, DateOnly ToDate, string? StartTime, string? EndTime,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string TimeZone,
    CapacityExceptionReason Reason, string? Note, string? UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// A capacity exception as submitted. All-day: <see cref="FromDate"/> to <see cref="ToDate"/> (both
/// included). Part-day: <see cref="FromDate"/> with <see cref="StartTime"/> and <see cref="EndTime"/>
/// as wall-clock times in the person's time zone; an end at or before the start runs into the next day.
/// </summary>
public sealed record CapacityExceptionInput(
    CapacityExceptionKind Kind, bool AllDay, DateOnly FromDate, DateOnly? ToDate,
    string? StartTime, string? EndTime, CapacityExceptionReason Reason, string? Note);

/// <summary>
/// One person's capacity for one shift date (the day their working window starts on, in their zone).
///
///   Gross - Breaks - Unavailable + Additional = Usable
///   Usable - Confirmed                        = RemainingConfirmed   (the free slots)
///   RemainingConfirmed - Tentative            = ProjectedRemaining   (the projected free slots)
/// </summary>
public sealed record DayCapacityDto(
    DateOnly Date, string TimeZone, bool IsWorkingDay,
    DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd,
    int GrossMinutes, int BreakMinutes, int UnavailableMinutes, int AdditionalMinutes, int UsableMinutes,
    int ConfirmedMinutes, int TentativeMinutes, int RemainingConfirmedMinutes, int ProjectedRemainingMinutes,
    bool UnavailableAllDay,
    IReadOnlyList<SlotDto> Breaks, IReadOnlyList<SlotDto> FreeSlots, IReadOnlyList<SlotDto> ProjectedFreeSlots,
    IReadOnlyList<CapacityExceptionDto> Exceptions,
    string? Holiday);

/// <summary>Someone's capacity over a run of dates.</summary>
public sealed record PersonCapacityDto(
    Guid AppUserId, string DisplayName, bool IsActive, bool IsSchedulable, bool HasSchedule, string TimeZone,
    DateOnly Today, IReadOnlyList<DayCapacityDto> Days, bool CanManageExceptions);

/// <summary>A row of the team capacity table: one person, one date.</summary>
public sealed record TeamCapacityRowDto(
    Guid AppUserId, string DisplayName, bool IsSchedulable, bool HasSchedule,
    IReadOnlyList<string> Teams, IReadOnlyList<StaffSkillDto> Skills, DayCapacityDto Day);

public sealed record TeamCapacityDto(
    DateOnly Date, IReadOnlyList<TeamCapacityRowDto> People,
    int UsableMinutes, int ConfirmedMinutes, int TentativeMinutes, int RemainingConfirmedMinutes);

public sealed record TeamCapacityQuery(DateOnly? Date = null, Guid? TeamId = null, Guid? DepartmentId = null,
    IReadOnlyList<Guid>? SkillIds = null, bool MatchAllSkills = true);

/// <summary>
/// "Who can take <see cref="DurationMinutes"/> of continuous work?" - on a date or a run of dates,
/// optionally only between two times of day, in a team or department, holding some skills.
///
/// <see cref="MatchAllSkills"/> says what several skills mean: true (the default) = the person must
/// hold EVERY one; false = ANY one is enough. The times of day are wall-clock times in
/// <see cref="TimeZone"/> (the organization's zone when omitted); a latest time at or before the
/// earliest runs into the next day.
/// </summary>
public sealed record AvailabilitySearch(
    DateOnly From, DateOnly? To, int DurationMinutes,
    string? EarliestTime = null, string? LatestTime = null, string? TimeZone = null,
    Guid? TeamId = null, Guid? DepartmentId = null,
    IReadOnlyList<Guid>? SkillIds = null, bool MatchAllSkills = true,
    IReadOnlyList<Guid>? AppUserIds = null);

/// <summary>Someone who can take the work: the first place it fits, and every window it would fit in that day.</summary>
/// <param name="Date">The date (in the search's zone) the work fits on.</param>
/// <param name="Recommended">The earliest continuous stretch of exactly the requested length.</param>
/// <param name="Windows">Every free window on that date long enough for the work.</param>
/// <param name="FreeMinutes">All free time inside the searched window on that date, in pieces or not.</param>
public sealed record AvailabilityMatchDto(
    Guid AppUserId, string DisplayName, string TimeZone, IReadOnlyList<string> Teams,
    IReadOnlyList<StaffSkillDto> MatchingSkills,
    DateOnly Date, SlotDto Recommended, IReadOnlyList<SlotDto> Windows, int FreeMinutes);

/// <summary>
/// The answer, as facts: who fits, and how many people were looked at and why the rest were left
/// out. No scoring and no recommendation beyond "earliest first".
/// </summary>
public sealed record AvailabilitySearchResultDto(
    string TimeZone, int DurationMinutes, bool MatchAllSkills,
    IReadOnlyList<AvailabilityMatchDto> Matches,
    int PeopleConsidered, int WithoutRequiredSkills, int NotOfferedForWork, int WithoutASchedule, int WithNoFittingSlot);

/// <summary>A piece of work someone wants to place: when, whether only pencilled in, and the skills it asks for.</summary>
public sealed record ProposedWork(DateTimeOffset Start, DateTimeOffset End, bool Tentative = false, IReadOnlyList<Guid>? SkillIds = null);

public sealed record ConflictDto(ConflictType Type, ConflictSeverity Severity, DateTimeOffset Start, DateTimeOffset End, string Message, Guid? BlockingWorkId);

public sealed record ConflictResultDto(bool CanSchedule, bool CanOverride, IReadOnlyList<ConflictDto> Conflicts);

/// <summary>
/// Capacity, free time and conflicts. Reads reach only the people the caller's schedule.view scope
/// reaches - anyone else is "not found", whether they exist or not.
/// </summary>
public interface ICapacityService
{
    /// <summary>One person's capacity for each date from <paramref name="from"/> to <paramref name="to"/> (today when omitted).</summary>
    Task<PersonCapacityDto> ForPersonAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<TeamCapacityDto> ForTeamAsync(Guid callerId, TeamCapacityQuery query, CancellationToken ct = default);
    Task<AvailabilitySearchResultDto> FindAsync(Guid callerId, AvailabilitySearch search, CancellationToken ct = default);
    /// <summary>
    /// Whether the proposed work fits. Reads everything fresh each time it is called: a free slot shown
    /// earlier is not a reservation, and whatever books work must call this again as it commits.
    /// </summary>
    Task<ConflictResultDto> EvaluateAsync(Guid callerId, Guid appUserId, ProposedWork proposal, CancellationToken ct = default);
}

/// <summary>Capacity exceptions: time someone is unavailable, or extra time they are available.</summary>
public interface ICapacityExceptionService
{
    Task<IReadOnlyList<CapacityExceptionDto>> ListAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<CapacityExceptionDto> AddAsync(Guid callerId, Guid appUserId, CapacityExceptionInput input, CancellationToken ct = default);
    Task<CapacityExceptionDto> UpdateAsync(Guid callerId, Guid appUserId, Guid exceptionId, CapacityExceptionInput input, CancellationToken ct = default);
    Task RemoveAsync(Guid callerId, Guid appUserId, Guid exceptionId, CancellationToken ct = default);
}

/// <summary>
/// Where planned work comes from. The capacity engine asks this for the work already placed in
/// people's time and treats the answer as the truth - so it works the same whichever kind of work it
/// is (the team's own tickets, Autotask or ConnectWise tickets, later projects and monitoring work),
/// with no ticket type known to the engine itself.
///
/// The implementation decides what the caller may know: a piece of work they may not see comes back
/// with no <see cref="AllocatedSpan.WorkId"/>, so they learn the time is taken and nothing else.
/// </summary>
public interface IWorkAllocationReader
{
    Task<IReadOnlyList<AllocatedSpan>> ForAsync(
        Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
