using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;

namespace Desk.Domain.Workforce;

/// <summary>Where a planned piece of work stands. Deliberately few states: a plan, or not a plan any more.</summary>
public enum WorkAllocationStatus
{
    /// <summary>In the person's plan. It takes capacity.</summary>
    Planned = 1,
    /// <summary>Taken out of the plan - by a person, or because the work finished first. Takes nothing.</summary>
    Cancelled = 5,
}

/// <summary>How the work got into the plan: who decided.</summary>
public enum SchedulingMethod
{
    /// <summary>The person planned their own work.</summary>
    Self = 1,
    /// <summary>Someone allowed to schedule others put it there.</summary>
    AuthorizedUser = 2,
    /// <summary>The system did it (today, only releasing work that finished).</summary>
    Automation = 3,
}

/// <summary>
/// A piece of work placed in someone's time: WHO is planned to do WHICH work WHEN, and for how long.
///
/// The work is a <see cref="Ticket"/> - the one unified model every source already flows into (the
/// team's own boards, Autotask, ConnectWise, monitoring alerts, and whatever connects later), so the
/// allocation carries no title, client, status or provider of its own: those stay with the ticket,
/// which stays with its system of record. An allocation is PLANNED time; actual time is the ticket's
/// time entries, which are untouched by anything here.
///
/// Planning is not assignment. An allocation never changes what the PSA says about the ticket, and
/// cancelling one never touches the ticket.
/// </summary>
public class WorkAllocation : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    /// <summary>The planned period, as real instants.</summary>
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>The planned effort: the length of the period, kept as a number for reporting.</summary>
    public int PlannedMinutes { get; set; }

    public WorkAllocationStatus Status { get; set; } = WorkAllocationStatus.Planned;

    /// <summary>Who decided, and how. Shown as "Scheduled by Mike" or "Planned by Jason".</summary>
    public SchedulingMethod Method { get; set; } = SchedulingMethod.Self;
    public Guid ScheduledByUserId { get; set; }

    /// <summary>
    /// Fixed: the person it is planned for cannot move it; only someone who schedules others can.
    /// Flexible (the default): they may move it and change its length within their capacity. Means
    /// nothing on work people planned for themselves.
    /// </summary>
    public bool IsFixed { get; set; }

    /// <summary>A short note for the person and the planners. Internal; never part of the ticket.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// When a conflict was overridden to place this: why, by whom, when, and which conflicts. Null
    /// when it was placed cleanly.
    /// </summary>
    public string? OverrideReason { get; set; }
    public string? OverriddenConflicts { get; set; }
    public Guid? OverriddenByUserId { get; set; }
    public DateTimeOffset? OverriddenAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    /// Optimistic-concurrency version: a change sent against a version that is no longer current
    /// is refused, so two people editing the same allocation from stale screens cannot both win.
    /// </summary>
    public int Version { get; set; }

    public Interval When => new(StartsAt, EndsAt);
}
