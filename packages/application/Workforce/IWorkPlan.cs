using Desk.Domain.Workforce;

namespace Desk.Application.Workforce;

// Planned work. INTERNAL ONLY, like everything in this module: nothing here is ever part of what a
// client receives. A ticket a client can see does not make who is planned on it, or when, theirs.

/// <summary>
/// A piece of work in someone's plan. The ticket's own facts (reference, title, client, status) come
/// along only when the ASKER may see that ticket; otherwise they are null and the asker learns that
/// the time is taken, nothing more.
/// </summary>
public sealed record WorkAllocationDto(
    Guid Id, Guid AppUserId, string PersonName,
    Guid TicketId, bool TicketVisible, string? Reference, string? Title, string? ClientName, string? TicketStatus, bool TicketFinished,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, int PlannedMinutes, string TimeZone,
    WorkAllocationStatus Status, SchedulingMethod Method, Guid ScheduledByUserId, string? ScheduledByName,
    bool IsFixed, string? Note,
    string? OverrideReason, IReadOnlyList<ConflictType> OverriddenConflicts, string? OverriddenByName,
    DateTimeOffset? CancelledAt, string? CancelledByName, string? CancelReason,
    int Version,
    /// <summary>What the asker may do with it: move or resize it, take it out of the plan, give it to someone else, confirm it when tentative.</summary>
    bool CanEdit, bool CanCancel, bool CanReassign, bool CanConfirm);

/// <summary>Someone's plan over a run of dates: their capacity per day (planned work already counted) and the work itself.</summary>
public sealed record PersonPlanDto(
    Guid AppUserId, string DisplayName, string TimeZone, DateOnly Today,
    IReadOnlyList<DayCapacityDto> Days, IReadOnlyList<WorkAllocationDto> Allocations,
    /// <summary>Whether the asker may add work to this plan at all, place work for this person as someone else, and override conflicts.</summary>
    bool CanPlan, bool CanScheduleOthers, bool CanOverride);

/// <summary>Work that is mine and not yet in my plan.</summary>
public sealed record UnscheduledWorkDto(
    Guid TicketId, string Reference, string Title, string? ClientName, string Priority, string Status,
    string Source, DateTimeOffset? DueAt, bool AssignedToMe, string? TeamName,
    /// <summary>Minutes already planned on it (in the past, or by someone else) - so "unscheduled" is not mistaken for "untouched".</summary>
    int PlannedMinutesSoFar);

/// <summary>A person the asker may plan work for: themselves, and whoever their schedule.manage scope reaches.</summary>
public sealed record PlannablePersonDto(Guid AppUserId, string DisplayName, bool IsSelf, string TimeZone, bool IsSchedulable);

/// <summary>The team scheduler's ask: which dates, and which people (a team, a department, skills). Dates default to today.</summary>
public sealed record TeamPlanQuery(DateOnly? From = null, DateOnly? To = null, Guid? TeamId = null, Guid? DepartmentId = null,
    IReadOnlyList<Guid>? SkillIds = null, bool MatchAllSkills = true);

/// <summary>One person on the team scheduler: who they are, each day's capacity, and what is planned in those days.</summary>
public sealed record TeamPlanPersonDto(
    Guid AppUserId, string DisplayName, string TimeZone, bool IsSchedulable, bool HasSchedule,
    IReadOnlyList<string> Teams, IReadOnlyList<StaffSkillDto> Skills,
    IReadOnlyList<DayCapacityDto> Days, IReadOnlyList<WorkAllocationDto> Allocations,
    /// <summary>Whether the asker may put work into this person's plan.</summary>
    bool CanPlan);

/// <summary>The team scheduler: everyone the asker may see, narrowed as asked, over a run of dates. Sums are over people offered for work.</summary>
public sealed record TeamPlanDto(
    DateOnly From, DateOnly To, DateOnly Today, string TimeZone, IReadOnlyList<TeamPlanPersonDto> People,
    int UsableMinutes, int ConfirmedMinutes, int TentativeMinutes, int RemainingConfirmedMinutes, int ProjectedRemainingMinutes, int AllocationCount,
    /// <summary>Whether the asker may schedule at least one of the people shown, and whether they may override conflicts.</summary>
    bool CanScheduleOthers, bool CanOverride);

/// <summary>Open work in a group's hands that is in nobody's plan: held by one of its people, or sitting with one of their teams.</summary>
public sealed record TeamUnscheduledWorkDto(
    Guid TicketId, string Reference, string Title, string? ClientName, string Priority, string Status, string Source,
    DateTimeOffset? DueAt,
    /// <summary>The holder when they are one of the group's people; a holder outside the group is only said to exist.</summary>
    Guid? HolderId, string? HolderName, bool HeldOutside,
    Guid? TeamId, string? TeamName, int PlannedMinutesSoFar, DateTimeOffset CreatedAt);

/// <summary>Placing work: which ticket, whose time, when, and whether it is fixed. Instants in UTC.</summary>
public sealed record WorkAllocationInput(
    Guid TicketId, Guid AppUserId, DateTimeOffset Start, DateTimeOffset End,
    bool IsFixed = false, string? Note = null,
    /// <summary>Given only when an overridable conflict is to be overridden; needs schedule.override.</summary>
    string? OverrideReason = null,
    /// <summary>Pencil it in rather than commit: takes tentative capacity only, and is confirmed later with a fresh check.</summary>
    bool Tentative = false);

/// <summary>Confirming pencilled-in work, or pencilling committed work back in: the version the screen showed, and a reason when a clash is overridden on confirmation.</summary>
public sealed record WorkAllocationStateInput(int Version, string? OverrideReason = null);

/// <summary>What planning a piece of work needs to know, with what has been allocated so far derived from the plans.</summary>
public sealed record PlanningRequirementDto(
    Guid TicketId, int? RequiredMinutes, DateTimeOffset? EarliestStart, DateTimeOffset? LatestEnd, bool Splittable,
    Guid? RequiredSkillId, string? RequiredSkillName, string? Note,
    /// <summary>Minutes in confirmed plans and in tentative plans (any person), and what is left of the required effort after the confirmed ones.</summary>
    int ConfirmedMinutes, int TentativeMinutes, int? RemainingMinutes,
    string? UpdatedByName, DateTimeOffset? UpdatedAt);

public sealed record PlanningRequirementInput(
    int? RequiredMinutes, DateTimeOffset? EarliestStart, DateTimeOffset? LatestEnd, bool Splittable = false, Guid? RequiredSkillId = null, string? Note = null);

/// <summary>Why a piece of work is waiting, derived from what is there - nothing is stored.</summary>
public enum WaitingReason
{
    /// <summary>Held by someone; nobody has planned it yet.</summary>
    AwaitingPlanning = 1,
    /// <summary>Routed to a team; nobody holds it, so nobody's plan can take it yet.</summary>
    NoTechnicianAssigned = 2,
    /// <summary>The holder has less confirmed free time before the due date than the work needs.</summary>
    InsufficientCapacityBeforeDue = 3,
}

public enum DueRisk { None = 0, DueTomorrow = 1, DueToday = 2, Overdue = 3 }

/// <summary>One row of the planning queue: the work, its requirement, what is allocated, why it waits, and how urgent it is.</summary>
public sealed record PlanningQueueItemDto(
    TeamUnscheduledWorkDto Work,
    int? RequiredMinutes, bool Splittable, DateTimeOffset? EarliestStart, DateTimeOffset? LatestEnd, string? RequiredSkillName,
    int ConfirmedMinutes, int TentativeMinutes, int? RemainingMinutes,
    WaitingReason Reason, DueRisk Due,
    /// <summary>The holder's confirmed free time before the due date, when both are known.</summary>
    int? FreeBeforeDueMinutes,
    int AgeDays);

/// <summary>The queue and the sums behind it: demand against what the group has free over the horizon. Facts, not a score.</summary>
public sealed record PlanningQueueDto(
    DateOnly From, DateOnly To, IReadOnlyList<PlanningQueueItemDto> Items,
    int DemandMinutes, int ItemsWithoutEstimate, int AvailableMinutes, int ShortageMinutes, int PeopleCounted);

/// <summary>A proposal to place effort in a window: continuous (one sitting) or split across the person's free time.</summary>
public sealed record PlanPreviewInput(
    Guid TicketId, Guid AppUserId, DateTimeOffset EarliestStart, DateTimeOffset LatestEnd, int RequiredMinutes,
    bool Splittable = false, bool Tentative = false, int MinChunkMinutes = 30);

public sealed record PlanPieceDto(DateTimeOffset Start, DateTimeOffset End, int Minutes);

/// <summary>
/// What would be placed, what would not fit, and a token for the state it was computed from: a
/// confirmation carries the token back and is refused with a fresh preview if anything changed.
/// </summary>
public sealed record PlanPreviewDto(
    Guid TicketId, Guid AppUserId, string PersonName, string TimeZone,
    DateTimeOffset EarliestStart, DateTimeOffset LatestEnd, int RequiredMinutes, bool Splittable, bool Tentative,
    IReadOnlyList<PlanPieceDto> Pieces, int AllocatedMinutes, int UnallocatedMinutes,
    IReadOnlyList<string> Warnings, int FreeMinutesInWindow, int LongestFreeMinutes, string PlanToken);

/// <summary>The preview's request, the pieces to write (all of them, or fewer), and the token the preview carried.</summary>
public sealed record PlanConfirmInput(
    PlanPreviewInput Request, IReadOnlyList<PlanPieceDto> Pieces, string PlanToken, string? OverrideReason = null, string? Note = null);

public sealed record PlanConfirmedDto(IReadOnlyList<WorkAllocationDto> Allocations, int AllocatedMinutes, int? RemainingMinutes);

/// <summary>The 409 payload when the plan changed between the preview and its confirmation: the fresh preview to review.</summary>
public sealed record PlanChangedDto(bool Stale, PlanPreviewDto Preview);

/// <summary>Moving or resizing work, or changing its note or fixedness. <see cref="Version"/> is the version the screen showed.</summary>
public sealed record WorkAllocationUpdate(
    DateTimeOffset Start, DateTimeOffset End, int Version, bool? IsFixed = null, string? Note = null, string? OverrideReason = null);

/// <summary>Giving work to someone else, optionally at another time.</summary>
public sealed record WorkAllocationReassign(
    Guid AppUserId, int Version, DateTimeOffset? Start = null, DateTimeOffset? End = null, string? OverrideReason = null);

/// <summary>
/// A small piece of internal work that has no ticket yet, planned in one step: a ticket is raised on
/// the team's board (assigned to the planner) and placed in their time. There is no such thing as
/// planned time without work behind it.
/// </summary>
public sealed record InternalWorkInput(
    Guid BoardId, string Title, string? Description, Guid? ClientCompanyId,
    DateTimeOffset Start, DateTimeOffset End, string? Priority = null, string? Note = null, string? OverrideReason = null);

/// <summary>The problem the API answers with when work cannot be placed as asked.</summary>
public sealed record ConflictProblemDto(
    bool CanOverride, bool OverrideAllowedForCaller, IReadOnlyList<ConflictDto> Conflicts, bool Stale = false);

public interface IWorkPlanService
{
    Task<PersonPlanDto> PlanAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<UnscheduledWorkDto>> UnscheduledAsync(Guid callerId, CancellationToken ct = default);
    Task<IReadOnlyList<PlannablePersonDto>> PlannablePeopleAsync(Guid callerId, CancellationToken ct = default);
    /// <summary>What is planned on a ticket, for the people the caller may see.</summary>
    Task<IReadOnlyList<WorkAllocationDto>> ForTicketAsync(Guid callerId, Guid ticketId, CancellationToken ct = default);
    /// <summary>The team scheduler: everyone the caller may see (narrowed), their capacity per day and what is planned in it.</summary>
    Task<TeamPlanDto> TeamAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default);
    /// <summary>Open work in the group's hands that nobody has planned yet.</summary>
    Task<IReadOnlyList<TeamUnscheduledWorkDto>> UnscheduledTeamAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default);

    Task<WorkAllocationDto> CreateAsync(Guid callerId, WorkAllocationInput input, CancellationToken ct = default);
    Task<WorkAllocationDto> CreateInternalWorkAsync(Guid callerId, InternalWorkInput input, CancellationToken ct = default);
    Task<WorkAllocationDto> UpdateAsync(Guid callerId, Guid allocationId, WorkAllocationUpdate input, CancellationToken ct = default);
    Task<WorkAllocationDto> ReassignAsync(Guid callerId, Guid allocationId, WorkAllocationReassign input, CancellationToken ct = default);
    Task<WorkAllocationDto> CancelAsync(Guid callerId, Guid allocationId, string? reason, CancellationToken ct = default);

    /// <summary>Pencilled-in work becomes committed, after a fresh check of everything; a clash is refused or overridden with a reason.</summary>
    Task<WorkAllocationDto> ConfirmAsync(Guid callerId, Guid allocationId, WorkAllocationStateInput input, CancellationToken ct = default);
    /// <summary>Committed work becomes pencilled in. Only someone who schedules others; audited.</summary>
    Task<WorkAllocationDto> MakeTentativeAsync(Guid callerId, Guid allocationId, WorkAllocationStateInput input, CancellationToken ct = default);

    Task<PlanningRequirementDto> RequirementAsync(Guid callerId, Guid ticketId, CancellationToken ct = default);
    Task<PlanningRequirementDto> SetRequirementAsync(Guid callerId, Guid ticketId, PlanningRequirementInput input, CancellationToken ct = default);

    /// <summary>The planning queue: the group's unscheduled work with its requirements, why it waits, and demand against free capacity over the horizon.</summary>
    Task<PlanningQueueDto> QueueAsync(Guid callerId, TeamPlanQuery query, int horizonDays, CancellationToken ct = default);

    /// <summary>What placing this effort in this window would look like. Nothing is written.</summary>
    Task<PlanPreviewDto> PreviewAsync(Guid callerId, PlanPreviewInput input, CancellationToken ct = default);
    /// <summary>Writes a preview's pieces, if the plan is still what the preview saw; otherwise refuses with a fresh preview.</summary>
    Task<PlanConfirmedDto> ConfirmPreviewAsync(Guid callerId, PlanConfirmInput input, CancellationToken ct = default);
}

/// <summary>
/// Takes work that has finished out of people's future plans, so a resolved or closed ticket never
/// leaves a misleading block of planned time behind. Run by the worker for every organization.
/// </summary>
public interface IWorkAllocationReleaser
{
    Task<int> ReleaseFinishedAsync(CancellationToken ct = default);
}

/// <summary>Release for every organization (the worker's loop).</summary>
public interface IWorkAllocationReleaseRunner
{
    Task RunAsync(CancellationToken ct = default);
}
