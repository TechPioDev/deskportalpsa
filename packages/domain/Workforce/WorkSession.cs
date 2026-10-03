using Desk.Domain.Common;
using Desk.Domain.Tickets;

namespace Desk.Domain.Workforce;

public enum WorkSessionStatus
{
    /// <summary>The clock is running: the newest segment has no end.</summary>
    Active = 1,
    /// <summary>The clock is stopped and may be resumed; every segment has an end.</summary>
    Paused = 2,
    /// <summary>Stopped for good; the active time became a time entry (or nothing, under a minute).</summary>
    Completed = 3,
    /// <summary>Thrown away: no time entry, the segments kept for the record.</summary>
    Cancelled = 4,
}

/// <summary>Why work is paused, when the technician said so. Waiting is not working time.</summary>
public enum WorkPauseReason
{
    None = 0,
    WaitingOnClient = 1,
    WaitingOnVendor = 2,
    WaitingOnReboot = 3,
    WaitingOnThirdParty = 4,
    Other = 9,
}

/// <summary>
/// A technician's live clock on one piece of work: the EXECUTION state between "I started" and "I
/// stopped". It is attached to a ticket, never to a person's day: this is work time, not attendance,
/// and nothing here says when anyone arrived or left.
///
/// Time runs only inside segments ([StartedAt, EndedAt) of each <see cref="WorkSessionSegment"/>);
/// a pause closes the open segment and a resume opens the next, so a paused hour is never counted.
/// The server's timestamps are the truth; a browser only displays them.
///
/// Stopping turns the active seconds into a <see cref="TicketTimeEntry"/> (the historical record,
/// which may travel to the PSA); the session then points at it and its segments stop counting
/// towards "actual time", so the same minutes are never summed twice. INTERNAL ONLY.
/// </summary>
public sealed class WorkSession : TenantEntity
{
    public Guid AppUserId { get; set; }
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    /// <summary>The planned allocation this work was started from, when it was; null for unplanned (reactive) work.</summary>
    public Guid? AllocationId { get; set; }

    public WorkSessionStatus Status { get; set; } = WorkSessionStatus.Active;
    public WorkPauseReason PauseReason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Active seconds, summed from the closed segments; final once the session ends.</summary>
    public int ActiveSeconds { get; set; }
    /// <summary>The time entry the session became when it stopped; null while it runs or when nothing was logged.</summary>
    public Guid? TimeEntryId { get; set; }
    /// <summary>A short note typed at stop time; it travels with the time entry.</summary>
    public string? Note { get; set; }

    public Guid UpdatedByUserId { get; set; }
    /// <summary>Concurrency token: every state change carries the version the screen saw.</summary>
    public int Version { get; set; } = 1;

    public List<WorkSessionSegment> Segments { get; set; } = [];
}

/// <summary>One run of the clock. An open segment (no end) is the running clock itself.</summary>
public sealed class WorkSessionSegment : TenantEntity
{
    public Guid SessionId { get; set; }
    public WorkSession? Session { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Whole seconds between start and end, written when the segment closes.</summary>
    public int Seconds { get; set; }
}
