using Desk.Domain.Common;
using Desk.Domain.Identity;

namespace Desk.Domain.Workforce;

/// <summary>
/// When a person normally works: their weekly working windows and the planned breaks inside them,
/// in their own time zone. It is a CAPACITY boundary for planning work, never a record of attendance
/// - nothing here says anyone was at work.
///
/// Versioned by <see cref="EffectiveFrom"/>: a change starts on a chosen day and the days before it
/// keep the schedule they had, so later capacity and utilization figures for the past never shift
/// when someone's hours change. The schedule in effect on a date is the latest version starting on
/// or before it.
/// </summary>
public class WorkSchedule : TenantEntity
{
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    /// <summary>The first day, in <see cref="TimeZone"/>, this version applies to.</summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>IANA zone id ("Asia/Kolkata"). The working windows are wall-clock times in it.</summary>
    public required string TimeZone { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>One entry per working weekday; a weekday with no entry is a day off.</summary>
    public ICollection<WorkScheduleDay> Days { get; set; } = new List<WorkScheduleDay>();
}

/// <summary>
/// One weekday's working window. An end at or before the start means the window runs past midnight
/// into the next day (18:00 - 03:00) and belongs to the day it starts on. Start and end are never
/// equal: a zero-length day is a day off, and a 24-hour day is not a shift.
/// </summary>
public class WorkScheduleDay : TenantEntity
{
    public Guid WorkScheduleId { get; set; }
    public WorkSchedule? WorkSchedule { get; set; }

    public DayOfWeek Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }

    public ICollection<WorkScheduleBreak> Breaks { get; set; } = new List<WorkScheduleBreak>();
}

/// <summary>
/// A planned break inside a working window - time the window does not offer for work. Planned, not
/// tracked: nobody clocks in or out of it. Its times are wall-clock times on the window's own
/// timeline, so in an overnight window a 00:30 break falls on the following calendar day.
/// </summary>
public class WorkScheduleBreak : TenantEntity
{
    public Guid WorkScheduleDayId { get; set; }
    public WorkScheduleDay? WorkScheduleDay { get; set; }

    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}
