namespace Desk.Domain.Tickets;

/// <summary>
/// Turns "within N hours" into a date. Round the clock that is simple addition; in business hours it
/// walks the working days in the organization's own time zone, spending the hours only while the desk
/// is open — a ticket raised at 17:30 on a Friday with four working hours to go is due Monday at
/// 12:30, not Friday at 21:30.
///
/// Walked in local time and converted back at the end, so a daylight-saving change inside the window
/// moves the answer by the hour it should.
/// </summary>
public static class SlaClock
{
    public const int MondayToFriday = 0b0111110;

    /// <summary>The longest a walk may run. A plan with no working days at all would otherwise never end.</summary>
    private const int MaxDays = 3660;

    public static DateTimeOffset Due(DateTimeOffset start, int hours, SlaPlan plan, TimeZoneInfo zone)
    {
        if (!plan.BusinessHoursOnly) return start.AddHours(hours);
        return Walk(start, hours * 60.0, plan.WorkdayStartHour, plan.WorkdayEndHour, plan.WorkingDays, zone);
    }

    private static DateTimeOffset Walk(DateTimeOffset start, double minutes, int open, int close, int days, TimeZoneInfo zone)
    {
        // A plan the service refuses to save, handled anyway rather than looping or throwing at the
        // moment somebody raises a ticket.
        if (days == 0 || open >= close) return start.AddMinutes(minutes);

        var cursor = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        for (var i = 0; i < MaxDays; i++)
        {
            var day = cursor.Date;
            if ((days & (1 << (int)day.DayOfWeek)) != 0)
            {
                var opens = day.AddHours(open);
                var closes = day.AddHours(close);
                if (cursor < opens) cursor = opens;
                if (cursor < closes)
                {
                    var available = (closes - cursor).TotalMinutes;
                    if (minutes <= available) return Local(cursor.AddMinutes(minutes), zone);
                    minutes -= available;
                }
            }
            cursor = day.AddDays(1);
        }
        return start.AddMinutes(minutes);
    }

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>The organization's zone, resolved the same way the scheduled reports resolve it.</summary>
    public static TimeZoneInfo Zone(string? id) => Common.TimeZones.Resolve(id);
}
