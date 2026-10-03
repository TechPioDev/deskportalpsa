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
    /// <summary>What the asker may do with it: move or resize it, take it out of the plan, give it to someone else.</summary>
    bool CanEdit, bool CanCancel, bool CanReassign);

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

/// <summary>Placing work: which ticket, whose time, when, and whether it is fixed. Instants in UTC.</summary>
public sealed record WorkAllocationInput(
    Guid TicketId, Guid AppUserId, DateTimeOffset Start, DateTimeOffset End,
    bool IsFixed = false, string? Note = null,
    /// <summary>Given only when an overridable conflict is to be overridden; needs schedule.override.</summary>
    string? OverrideReason = null);

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

    Task<WorkAllocationDto> CreateAsync(Guid callerId, WorkAllocationInput input, CancellationToken ct = default);
    Task<WorkAllocationDto> CreateInternalWorkAsync(Guid callerId, InternalWorkInput input, CancellationToken ct = default);
    Task<WorkAllocationDto> UpdateAsync(Guid callerId, Guid allocationId, WorkAllocationUpdate input, CancellationToken ct = default);
    Task<WorkAllocationDto> ReassignAsync(Guid callerId, Guid allocationId, WorkAllocationReassign input, CancellationToken ct = default);
    Task<WorkAllocationDto> CancelAsync(Guid callerId, Guid allocationId, string? reason, CancellationToken ct = default);
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
