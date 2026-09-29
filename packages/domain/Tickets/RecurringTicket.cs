using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

public enum RecurrenceFrequency
{
    Daily = 0,
    /// <summary>Monday to Friday only — "every working day" is the most common desk routine.</summary>
    Weekdays = 1,
    Weekly = 2,
    Monthly = 3,
}

/// <summary>
/// Work that comes round on a schedule: the Monday patch review, the monthly backup restore test, the
/// daily check of the overnight alerts. The worker raises a board ticket from this when it falls due,
/// exactly as if the person who set it up had raised it by hand — same board, topic, priority,
/// assignee, SLA — and gives it the checklist below as its tasks.
///
/// Times are in the organization's own time zone and held as a local hour, so "09:00 every Monday"
/// stays 09:00 across a daylight-saving change rather than drifting to 08:00 or 10:00.
/// </summary>
public class RecurringTicket : TenantEntity
{
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public Guid? BoardTopicId { get; set; }
    public string? Priority { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? AssignedAppUserId { get; set; }
    public Guid? ClientCompanyId { get; set; }

    /// <summary>One task per line, created on every ticket this raises.</summary>
    public string? Checklist { get; set; }

    public RecurrenceFrequency Frequency { get; set; }

    /// <summary>Weekly only: 0 = Sunday … 6 = Saturday.</summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>
    /// Monthly only: 1-28, or 0 for the last day of the month. Capped at 28 so "the 31st" never
    /// silently skips the months that do not have one.
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Hour of the day it is raised, 0-23, in the organization's time zone.</summary>
    public int Hour { get; set; } = 9;

    /// <summary>
    /// Do not raise a new ticket while the last one is still open. On by default: a backlog of seven
    /// identical "check the backups" tickets is noise, not seven reminders.
    /// </summary>
    public bool SkipIfOpen { get; set; } = true;

    public bool IsActive { get; set; } = true;

    /// <summary>When it is next due, in UTC. Recomputed from the schedule after every run or edit.</summary>
    public DateTimeOffset NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastTicketId { get; set; }

    /// <summary>What happened last time, in words: the number raised, why it was skipped, or why it failed.</summary>
    public string? LastOutcome { get; set; }

    /// <summary>Who set it up — the tickets are raised in their name, as if they had raised them.</summary>
    public Guid CreatedByUserId { get; set; }
}
