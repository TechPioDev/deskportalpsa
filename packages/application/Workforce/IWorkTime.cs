using Desk.Domain.Tickets;
using Desk.Domain.Workforce;

namespace Desk.Application.Workforce;

// ---- Phase 6: My Day, work sessions and actual time ----------------------------------------------
//
// Execution state (a session with its segments) is kept apart from recorded history (the ticket's
// time entries, which the PSA may hold). A stopped session becomes one time entry; the session's
// seconds then stop counting, so a minute is never summed twice. Nothing here is attendance.

/// <summary>What to do with the session that is already running when another is started or resumed.</summary>
public enum ActiveWorkSwitch
{
    /// <summary>Refuse with a 409 naming the running work; the person decides.</summary>
    None = 0,
    /// <summary>Pause the running work, then start the new.</summary>
    PauseCurrent = 1,
    /// <summary>Stop the running work (its time is logged, no note), then start the new.</summary>
    StopCurrent = 2,
}

public enum WorkItemState
{
    /// <summary>Planned and not touched yet.</summary>
    Planned = 1,
    /// <summary>The clock is running on it.</summary>
    Active = 2,
    /// <summary>The clock is paused on it.</summary>
    Paused = 3,
    /// <summary>Time has been recorded on it and the ticket is still open.</summary>
    InProgress = 4,
    /// <summary>The ticket is finished.</summary>
    Completed = 5,
}

public sealed record WorkSessionDto(
    Guid Id, Guid AppUserId, Guid TicketId, bool TicketVisible, string? Reference, string? Title, string? ClientName, bool TicketFinished,
    Guid? AllocationId, WorkSessionStatus Status, WorkPauseReason PauseReason,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    /// <summary>Seconds in the closed segments. The screen adds (now - RunningSince) while the clock runs; the server never streams ticks.</summary>
    int ActiveSeconds,
    /// <summary>When the open segment began, while the session is active; null when paused or ended.</summary>
    DateTimeOffset? RunningSince,
    Guid? TimeEntryId, TimeEntrySyncStatus? TimeEntrySyncStatus, string? TimeEntrySyncError, string? Note,
    /// <summary>Started outside the person's working window for that day (after hours, a day off): a fact, not overtime.</summary>
    bool OutsideSchedule,
    int Version,
    /// <summary>The asker may pause, resume or stop it: their own session, or they lead boards.</summary>
    bool CanControl);

/// <param name="CurrentId">With a switch: the running clock the person agreed to pause or stop; another one running by then is refused as stale.</param>
public sealed record StartWorkInput(Guid TicketId, Guid? AllocationId = null, ActiveWorkSwitch Switch = ActiveWorkSwitch.None, Guid? CurrentId = null);
public sealed record WorkSessionStateInput(int Version, WorkPauseReason PauseReason = WorkPauseReason.None, ActiveWorkSwitch Switch = ActiveWorkSwitch.None, Guid? CurrentId = null);
public sealed record StopWorkInput(int Version, string? Note = null, bool Billable = true, string? WorkType = null, string? WorkRole = null,
    /// <summary>Throw the time away instead of logging it (a clock started by mistake).</summary>
    bool Discard = false);

/// <summary>The 409 payload when work is already running: what, since when, and the ways out.</summary>
public sealed record ActiveWorkProblemDto(WorkSessionDto Current, bool CanPauseCurrent, bool CanStopCurrent);

public sealed record MyDaySlotDto(Guid AllocationId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int PlannedMinutes, bool Tentative, bool IsFixed);

/// <summary>One piece of work on a person's day: what was planned for it (its slots) against what was actually done on it that day.</summary>
public sealed record MyDayItemDto(
    Guid TicketId, bool TicketVisible, string? Reference, string? Title, string? ClientName, string Source, string? TicketStatus, bool TicketFinished, string? Priority, DateTimeOffset? DueAt,
    IReadOnlyList<MyDaySlotDto> Slots,
    /// <summary>Confirmed planned minutes on this ticket today; 0 when it was not planned.</summary>
    int PlannedMinutes, int TentativeMinutes,
    /// <summary>Recorded time entries by this person on this ticket, dated today, plus the live clock; whole seconds.</summary>
    int ActualSeconds,
    /// <summary>Actual - planned, in minutes; null when nothing was planned (not a variance, "not planned").</summary>
    int? VarianceMinutes, double? VariancePercent,
    bool Planned, WorkItemState State, WorkSessionDto? Session,
    /// <summary>The clock still runs past the last planned slot's end.</summary>
    bool OverPlannedEnd,
    /// <summary>Time entries by this person on it today that the PSA has not accepted yet (pending or failed).</summary>
    int EntriesNotSynced,
    /// <summary>The earliest slot's start, for ordering; null for unplanned work (ordered by first actual work).</summary>
    DateTimeOffset OrderAt);

public sealed record MyDaySummaryDto(
    int PlannedMinutes, int TentativeMinutes, int ActualSeconds, int UnplannedActualSeconds,
    int Completed, int InProgress, int NotStarted,
    /// <summary>Planned minutes still ahead: for every item not finished, what is left of its planned time after the actual.</summary>
    int RemainingPlannedMinutes);

public sealed record MyDayDto(
    Guid AppUserId, string DisplayName, string TimeZone, DateOnly Date, DateOnly Today,
    DayCapacityDto? Day, IReadOnlyList<MyDayItemDto> Items, IReadOnlyList<UnscheduledWorkDto> Unscheduled,
    WorkSessionDto? Current, IReadOnlyList<WorkSessionDto> Paused, MyDaySummaryDto Summary,
    /// <summary>The asker may start, pause and stop work on this day (it is their own and they may log time).</summary>
    bool CanWork);

public sealed record TeamTodayPersonDto(
    Guid AppUserId, string DisplayName, string TimeZone, bool IsSchedulable, bool HasSchedule,
    WorkSessionDto? Current, int PausedCount,
    int UsableMinutes, int PlannedMinutes, int ActualSeconds, int Completed, int InProgress, int NotStarted);

public sealed record TeamTodayDto(DateOnly Date, string TimeZone, IReadOnlyList<TeamTodayPersonDto> People,
    int PlannedMinutes, int ActualSeconds, int Working, int Paused);

public interface IWorkTimeService
{
    /// <summary>The caller's running or paused sessions: the running one first.</summary>
    Task<IReadOnlyList<WorkSessionDto>> ActiveAsync(Guid callerId, CancellationToken ct = default);
    Task<WorkSessionDto> StartAsync(Guid callerId, StartWorkInput input, CancellationToken ct = default);
    Task<WorkSessionDto> PauseAsync(Guid callerId, Guid sessionId, WorkSessionStateInput input, CancellationToken ct = default);
    Task<WorkSessionDto> ResumeAsync(Guid callerId, Guid sessionId, WorkSessionStateInput input, CancellationToken ct = default);
    /// <summary>Stops the clock for good: the active seconds become a time entry (or nothing when discarded or under a minute).</summary>
    Task<WorkSessionDto> StopAsync(Guid callerId, Guid sessionId, StopWorkInput input, CancellationToken ct = default);
    /// <summary>A person's day: planned against actual, the live clock, what is still unscheduled. Own, or someone the caller may see.</summary>
    Task<MyDayDto> MyDayAsync(Guid callerId, Guid? appUserId, DateOnly? date, CancellationToken ct = default);
    /// <summary>The people the caller may see, today: who is working on what, planned against actual.</summary>
    Task<TeamTodayDto> TeamTodayAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default);
}
