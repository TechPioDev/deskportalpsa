namespace Desk.Domain.Tickets;

/// <summary>
/// When a recurring ticket is next due. Worked out in the organization's local time and converted to
/// UTC at the end, so "09:00 every Monday" is 09:00 on both sides of a daylight-saving change.
/// </summary>
public static class RecurrenceSchedule
{
    /// <summary>The first occurrence strictly after <paramref name="after"/>.</summary>
    public static DateTimeOffset Next(RecurringTicket r, DateTimeOffset after, TimeZoneInfo zone)
    {
        var hour = Math.Clamp(r.Hour, 0, 23);
        var day = TimeZoneInfo.ConvertTime(after, zone).DateTime.Date;
        // Fourteen months is more than any schedule here can skip: the rarest, monthly, fires twelve times a year.
        for (var i = 0; i < 430; i++, day = day.AddDays(1))
        {
            if (!Matches(r, day)) continue;
            var local = DateTime.SpecifyKind(day.AddHours(hour), DateTimeKind.Unspecified);
            var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            if (at > after) return at;
        }
        return after.AddDays(1);
    }

    public static bool Matches(RecurringTicket r, DateTime day) => r.Frequency switch
    {
        RecurrenceFrequency.Daily => true,
        RecurrenceFrequency.Weekdays => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RecurrenceFrequency.Weekly => (int)day.DayOfWeek == r.DayOfWeek,
        RecurrenceFrequency.Monthly => r.DayOfMonth == 0
            ? day.Day == DateTime.DaysInMonth(day.Year, day.Month)
            : day.Day == r.DayOfMonth,
        _ => false,
    };

    /// <summary>The schedule in words, as the list and the ticket describe it.</summary>
    public static string Describe(RecurringTicket r)
    {
        var at = $"{Math.Clamp(r.Hour, 0, 23):00}:00";
        return r.Frequency switch
        {
            RecurrenceFrequency.Daily => $"Every day at {at}",
            RecurrenceFrequency.Weekdays => $"Every weekday at {at}",
            RecurrenceFrequency.Weekly => $"Every {(DayOfWeek)Math.Clamp(r.DayOfWeek, 0, 6)} at {at}",
            RecurrenceFrequency.Monthly => r.DayOfMonth == 0
                ? $"Last day of every month at {at}"
                : $"Day {r.DayOfMonth} of every month at {at}",
            _ => at,
        };
    }
}
