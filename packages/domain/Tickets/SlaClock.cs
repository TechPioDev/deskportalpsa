namespace Desk.Domain.Tickets;

/// <summary>
/// Turns "within N hours" into a date. Round the clock that is simple addition; in business hours it
/// walks the working days in the organization's own time zone, spending the hours only while the desk
/// is open — a ticket raised at 17:30 on a Friday with four working hours to go is due Monday at
/// 12:30, not Friday at 21:30. A plan that skips holidays also steps over the desk's closed days, and a
/// night shift (22:00-06:00) is a working window that closes the next morning.
///
/// Walked in local time and converted back at the end, so a daylight-saving change inside the window
/// moves the answer by the hour it should.
/// </summary>
public static class SlaClock
{
    public const int MondayToFriday = 0b0111110;

    /// <summary>The longest a walk may run. A plan with no working days at all would otherwise never end.</summary>
    private const int MaxDays = 3660;

    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    public static DateTimeOffset Due(
        DateTimeOffset start, int hours, SlaPlan plan, TimeZoneInfo zone, IReadOnlySet<DateOnly>? holidays = null)
        => AddWorking(start, hours * 60.0, plan, zone, holidays);

    /// <summary>Moves forward by this many minutes of the plan's own time — working minutes, or plain ones.</summary>
    public static DateTimeOffset AddWorking(
        DateTimeOffset start, double minutes, SlaPlan plan, TimeZoneInfo zone, IReadOnlySet<DateOnly>? holidays = null)
    {
        if (!Walkable(plan)) return start.AddMinutes(minutes);
        var closed = plan.SkipHolidays ? holidays ?? NoHolidays : NoHolidays;

        var cursor = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        // From the day before: an overnight shift that began yesterday evening may still be running.
        for (var day = cursor.Date.AddDays(-1); day <= cursor.Date.AddDays(MaxDays); day = day.AddDays(1))
        {
            if (Window(day, plan, closed) is not { } window) continue;
            var (opens, closes) = window;
            if (closes <= cursor) continue;
            if (cursor < opens) cursor = opens;
            var available = (closes - cursor).TotalMinutes;
            if (minutes <= available) return Local(cursor.AddMinutes(minutes), zone);
            minutes -= available;
            cursor = closes;
        }
        return start.AddMinutes(minutes);
    }

    /// <summary>
    /// How much of the plan's own time lies between two moments: working minutes for a business-hours
    /// plan, plain minutes otherwise. Negative when <paramref name="to"/> is before <paramref name="from"/>,
    /// so a pause that begins after the ticket was already overdue carries its lateness forward.
    /// </summary>
    public static double MinutesBetween(
        DateTimeOffset from, DateTimeOffset to, SlaPlan plan, TimeZoneInfo zone, IReadOnlySet<DateOnly>? holidays = null)
    {
        if (to < from) return -MinutesBetween(to, from, plan, zone, holidays);
        if (!Walkable(plan)) return (to - from).TotalMinutes;

        var closed = plan.SkipHolidays ? holidays ?? NoHolidays : NoHolidays;
        var a = TimeZoneInfo.ConvertTime(from, zone).DateTime;
        var b = TimeZoneInfo.ConvertTime(to, zone).DateTime;
        double total = 0;
        for (var day = a.Date.AddDays(-1); day <= b.Date; day = day.AddDays(1))
        {
            if (Window(day, plan, closed) is not { } window) continue;
            var (opens, closes) = window;
            var start = a > opens ? a : opens;
            var end = b < closes ? b : closes;
            if (end > start) total += (end - start).TotalMinutes;
        }
        return total;
    }

    /// <summary>
    /// The shift that starts on this day, or null when the desk does not work it. A shift belongs to
    /// the day it STARTS: Monday's 22:00-06:00 runs into Tuesday morning, and a holiday on Monday
    /// cancels the shift that would have started that night — not the one that ends that morning.
    /// </summary>
    private static (DateTime Opens, DateTime Closes)? Window(DateTime day, SlaPlan plan, IReadOnlySet<DateOnly> closed)
    {
        if ((plan.WorkingDays & (1 << (int)day.DayOfWeek)) == 0 || closed.Contains(DateOnly.FromDateTime(day))) return null;
        var opens = day.AddHours(plan.WorkdayStartHour);
        // Closing at or before the opening hour means closing the next morning: an overnight shift.
        var closes = plan.WorkdayEndHour > plan.WorkdayStartHour
            ? day.AddHours(plan.WorkdayEndHour)
            : day.AddDays(1).AddHours(plan.WorkdayEndHour);
        return (opens, closes);
    }

    /// <summary>
    /// Whether the plan describes working hours that can be walked. A plan the service refuses to save
    /// — no working days, or a shift that opens and closes at the same hour — is treated as round the
    /// clock rather than looping or throwing at the moment somebody raises a ticket.
    /// </summary>
    private static bool Walkable(SlaPlan plan)
        => plan.BusinessHoursOnly && (plan.WorkingDays & 0b1111111) != 0
           && plan.WorkdayStartHour % 24 != plan.WorkdayEndHour % 24;

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>The organization's zone, resolved the same way the scheduled reports resolve it.</summary>
    public static TimeZoneInfo Zone(string? id) => Common.TimeZones.Resolve(id);

    /// <summary>The statuses that mean the ball is in someone else's court, so the clock stops.</summary>
    public static bool IsWaiting(string status)
    {
        var s = status.Trim().ToUpperInvariant();
        return s is "WAITING_CUSTOMER" or "ON_HOLD";
    }

    /// <summary>
    /// Stops or restarts the clock on a status change. Entering a waiting status records when the
    /// pause began; leaving it moves every promise not yet kept forward by exactly the plan-time that
    /// passed while paused — working time for a business-hours plan, so a pause over a weekend does
    /// not hand the ticket two free days it never had.
    ///
    /// Leaving for a finished status just ends the pause: a resolved ticket has no due date left to move.
    /// </summary>
    public static void OnStatusChange(
        Ticket ticket, string newStatus, DateTimeOffset now, SlaPlan? plan, TimeZoneInfo zone, IReadOnlySet<DateOnly>? holidays)
    {
        if (plan is null || !plan.PauseWhileWaiting) { ticket.SlaPausedAt = null; return; }

        var waiting = IsWaiting(newStatus);
        if (waiting)
        {
            ticket.SlaPausedAt ??= now;
            return;
        }
        if (ticket.SlaPausedAt is not { } pausedAt) return;
        ticket.SlaPausedAt = null;

        var finished = TicketStatusRules.Finished(newStatus);
        if (finished) return;

        // The promise is shifted by the plan-time the pause took, so a ticket paused with two working
        // hours left comes back with two working hours left.
        if (ticket.SlaDueAt is { } due)
            ticket.SlaDueAt = AddWorking(now, MinutesBetween(pausedAt, due, plan, zone, holidays), plan, zone, holidays);
        if (ticket.FirstRespondedAt is null && ticket.FirstResponseDueAt is { } reply)
            ticket.FirstResponseDueAt = AddWorking(now, MinutesBetween(pausedAt, reply, plan, zone, holidays), plan, zone, holidays);
    }
}
