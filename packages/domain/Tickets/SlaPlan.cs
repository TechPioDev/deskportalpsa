using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// How quickly a kind of work is owed: an answer within so many hours, and done within so many. A
/// board ticket takes its plan from its topic, else from its board, and the plan turns into the
/// ticket's due dates at the moment it is raised.
///
/// Board tickets only. A PSA ticket's SLA belongs to the PSA — it arrives with its own target date,
/// and a second clock here would disagree with the one the customer's contract is measured by.
///
/// Changing a plan does not move the due dates of tickets already raised under it. A due date is a
/// promise made when the work arrived; quietly re-dating a week of tickets because somebody edited a
/// number would rewrite which of them were late.
/// </summary>
public class SlaPlan : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>Resolved within this many hours of being raised.</summary>
    public int ResolveWithinHours { get; set; }

    /// <summary>First reply within this many hours. Null when the plan promises no response time.</summary>
    public int? FirstResponseWithinHours { get; set; }

    /// <summary>
    /// Counts only working hours when set; round the clock otherwise. A desk that runs day and night
    /// shifts is a 24x7 desk and should leave this off.
    /// </summary>
    public bool BusinessHoursOnly { get; set; }

    /// <summary>Working day in the organization's own time zone, as whole hours: 9 and 18 is 09:00-18:00.</summary>
    public int WorkdayStartHour { get; set; } = 9;
    public int WorkdayEndHour { get; set; } = 18;

    /// <summary>Which days count, as a bitmask with Sunday as bit 0. 62 is Monday to Friday.</summary>
    public int WorkingDays { get; set; } = SlaClock.MondayToFriday;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
