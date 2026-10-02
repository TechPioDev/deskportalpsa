using Desk.Domain.Common;
using Desk.Domain.Identity;

namespace Desk.Domain.Workforce;

/// <summary>Whether a capacity exception takes time away or adds it.</summary>
public enum CapacityExceptionKind
{
    /// <summary>Not available for planned work during this time.</summary>
    Unavailable = 1,
    /// <summary>Available for planned work outside the usual working hours.</summary>
    AdditionalAvailability = 2,
}

/// <summary>
/// Why - a label for whoever plans the work, nothing more. There are no leave balances, accruals,
/// entitlements, approvals or payroll effects behind any of these.
/// </summary>
public enum CapacityExceptionReason
{
    Other = 0,
    Meeting = 1,
    Training = 2,
    Appointment = 3,
    TimeOff = 4,
    Sick = 5,
    InternalEvent = 6,
}

/// <summary>
/// A one-off change to when someone can take planned work: time they are NOT available (a meeting,
/// an appointment, a day off), or EXTRA time they are (an evening they have agreed to cover). It
/// changes capacity for planning; it is not an attendance or leave record.
///
/// Two shapes. A PART-DAY exception is a real stretch of time (<see cref="StartsAt"/> to
/// <see cref="EndsAt"/>). An ALL-DAY exception covers whole shift dates (<see cref="FromDate"/> to
/// <see cref="ToDate"/>, both included) and takes off the working window that STARTS on each of
/// them - so a night shift that begins on a day off is off in full, including its hours after
/// midnight, exactly as a holiday cancels that night's SLA shift.
/// </summary>
public class CapacityException : TenantEntity
{
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    public CapacityExceptionKind Kind { get; set; } = CapacityExceptionKind.Unavailable;

    /// <summary>Whole shift dates rather than a stretch of time. Only an unavailable exception can be all-day.</summary>
    public bool AllDay { get; set; }

    /// <summary>First date covered, in <see cref="TimeZone"/>. For a part-day exception, the date it starts on.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>Last date covered (included). For a part-day exception, the date it ends on.</summary>
    public DateOnly ToDate { get; set; }

    /// <summary>The real start and end of a part-day exception; null when <see cref="AllDay"/>.</summary>
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>IANA zone the dates and times were entered in - the person's schedule zone at the time.</summary>
    public required string TimeZone { get; set; }

    public CapacityExceptionReason Reason { get; set; } = CapacityExceptionReason.Other;

    /// <summary>A short plain-text note for planners. Never shown to a client.</summary>
    public string? Note { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
