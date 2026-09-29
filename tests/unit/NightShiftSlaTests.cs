using Desk.Domain.Tickets;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// An SLA plan whose working hours run through the night — the desk's 22:00-06:00 shift. A shift
/// belongs to the day it starts: Monday's runs into Tuesday morning.
/// </summary>
public class NightShiftSlaTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // 22:00-06:00, Monday to Friday nights.
    private static SlaPlan Nights(bool holidays = true) => new()
    {
        MspOrganizationId = Org, Name = "Nights", ResolveWithinHours = 4, BusinessHoursOnly = true,
        WorkdayStartHour = 22, WorkdayEndHour = 6, WorkingDays = SlaClock.MondayToFriday, SkipHolidays = holidays,
    };

    // 28 Sep 2026 is a Monday.
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Hours_raised_during_the_shift_are_spent_across_midnight()
        => SlaClock.Due(At(28, 23), 4, Nights(), TimeZoneInfo.Utc).Should().Be(At(29, 3));

    [Fact]
    public void Hours_raised_in_the_daytime_wait_for_the_evening()
        // Tuesday 10:00: the next shift opens Tuesday 22:00.
        => SlaClock.Due(At(29, 10), 3, Nights(), TimeZoneInfo.Utc).Should().Be(At(30, 1));

    [Fact]
    public void Raised_before_dawn_the_shift_that_began_last_night_is_still_running()
        // Wednesday 04:00 is inside Tuesday night's shift: two hours left in it, then Wednesday night.
        => SlaClock.Due(At(30, 4), 3, Nights(), TimeZoneInfo.Utc).Should().Be(At(30, 23));

    [Fact]
    public void Friday_nights_shift_runs_into_Saturday_and_then_nothing_until_Monday_night()
    {
        // Saturday 05:00: an hour left of Friday night's shift, then Monday 22:00.
        var saturday = new DateTimeOffset(2026, 10, 3, 5, 0, 0, TimeSpan.Zero);
        SlaClock.Due(saturday, 2, Nights(), TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_holiday_cancels_the_shift_that_starts_that_night()
    {
        // Tuesday is a holiday: Monday night's shift still runs into Tuesday morning, Tuesday night's
        // does not happen, and the next is Wednesday night.
        var tuesday = new HashSet<DateOnly> { new(2026, 9, 29) };
        SlaClock.Due(At(29, 5), 2, Nights(), TimeZoneInfo.Utc, tuesday).Should().Be(At(30, 23));
    }

    [Fact]
    public void Working_time_between_two_moments_counts_only_the_night_hours()
    {
        // Monday 21:00 to Wednesday 07:00: Monday night 8h + Tuesday night 8h = 960 minutes.
        SlaClock.MinutesBetween(At(28, 21), At(30, 7), Nights(), TimeZoneInfo.Utc).Should().Be(960);
        // From inside the shift before dawn: Tuesday 03:00 to 06:00.
        SlaClock.MinutesBetween(At(29, 3), At(29, 12), Nights(), TimeZoneInfo.Utc).Should().Be(180);
    }

    [Fact]
    public void A_pause_through_the_day_gives_back_night_hours()
    {
        var plan = Nights();
        plan.PauseWhileWaiting = true;
        // Paused Tuesday 02:00 with the ticket due 05:00: three working hours left.
        var ticket = new Ticket
        {
            MspOrganizationId = Org, RequesterName = "n", RequesterEmail = "n@t", Title = "t",
            SlaDueAt = At(29, 5), PortalStatus = "IN_PROGRESS",
        };
        SlaClock.OnStatusChange(ticket, "WAITING_CUSTOMER", At(29, 2), plan, TimeZoneInfo.Utc, null);
        // Resumed Tuesday afternoon: the three hours come from Tuesday night's shift.
        SlaClock.OnStatusChange(ticket, "IN_PROGRESS", At(29, 15), plan, TimeZoneInfo.Utc, null);
        ticket.SlaDueAt.Should().Be(At(30, 1));
    }

    [Fact]
    public void A_day_shift_is_unchanged_by_all_of_this()
    {
        var days = Nights();
        days.WorkdayStartHour = 9;
        days.WorkdayEndHour = 17;
        // Friday 16:00 with two hours: one Friday, one Monday.
        SlaClock.Due(new DateTimeOffset(2026, 10, 2, 16, 0, 0, TimeSpan.Zero), 2, days, TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
    }
}
