using Desk.Domain.Common;
using Desk.Domain.Workforce;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The arithmetic of a working window: overnight windows, breaks, and the real instants a window
/// covers in a person's time zone - including the nights a clock change makes shorter or longer.
/// A schedule is a capacity boundary; none of this says anyone attended.
/// </summary>
public class WorkingWindowTests
{
    private static TimeOnly T(string hm) => TimeOnly.Parse(hm);
    private static DayWindow Day(string start, string end, params (string, string)[] breaks)
        => new(DayOfWeek.Monday, T(start), T(end), breaks.Select(b => new BreakSpan(T(b.Item1), T(b.Item2))).ToList());

    // Real zone rules on every host: the IANA id where the host reads IANA (the Linux servers and
    // CI), else the same zone's Windows id (a Windows machine in invariant-globalization mode).
    private static TimeZoneInfo Zone(string iana, string windows)
        => TimeZones.HostKnowsIana ? TimeZoneInfo.FindSystemTimeZoneById(iana) : TimeZoneInfo.FindSystemTimeZoneById(windows);
    private static readonly TimeZoneInfo NewYork = Zone("America/New_York", "Eastern Standard Time");
    private static readonly TimeZoneInfo Kolkata = Zone("Asia/Kolkata", "India Standard Time");

    [Fact]
    public void A_day_shift_with_a_lunch_break_offers_eight_hours()
    {
        var day = Day("08:30", "17:30", ("12:30", "13:30"));
        WorkingWindow.Problems(day).Should().BeEmpty();
        (WorkingWindow.GrossMinutes(day.Start, day.End), WorkingWindow.BreakMinutes(day), WorkingWindow.UsableMinutes(day))
            .Should().Be((540, 60, 480));
        WorkingWindow.CrossesMidnight(day.Start, day.End).Should().BeFalse();
    }

    [Fact]
    public void Different_hours_give_different_capacity()
    {
        WorkingWindow.UsableMinutes(Day("09:00", "18:00")).Should().Be(540);
        WorkingWindow.UsableMinutes(Day("07:00", "11:00")).Should().Be(240);
    }

    [Fact]
    public void A_night_shift_ending_after_midnight_is_one_window_not_an_invalid_one()
    {
        var night = Day("18:00", "03:00", ("00:00", "00:30"));
        WorkingWindow.Problems(night).Should().BeEmpty();
        WorkingWindow.CrossesMidnight(night.Start, night.End).Should().BeTrue();
        (WorkingWindow.GrossMinutes(night.Start, night.End), WorkingWindow.UsableMinutes(night)).Should().Be((540, 510));
    }

    [Fact]
    public void Several_breaks_each_come_off()
    {
        var day = Day("08:00", "18:00", ("10:00", "10:15"), ("13:00", "13:45"), ("16:00", "16:15"));
        WorkingWindow.Problems(day).Should().BeEmpty();
        WorkingWindow.UsableMinutes(day).Should().Be(600 - 75);
    }

    [Theory]
    [InlineData("08:30", "08:30", "start and end at the same time")]
    public void A_zero_length_window_is_refused(string start, string end, string expected)
        => WorkingWindow.Problems(Day(start, end)).Should().ContainSingle().Which.Should().Contain(expected);

    [Fact]
    public void Overlapping_breaks_are_refused()
        => WorkingWindow.Problems(Day("08:30", "17:30", ("12:00", "13:00"), ("12:30", "13:30")))
            .Should().ContainSingle().Which.Should().Contain("overlap");

    [Theory]
    [InlineData("07:00", "08:00")]   // before the window opens
    [InlineData("17:00", "18:00")]   // runs past the end
    [InlineData("13:00", "12:00")]   // backwards
    public void A_break_outside_the_window_is_refused(string from, string to)
        => WorkingWindow.Problems(Day("08:30", "17:30", (from, to))).Should().ContainSingle().Which.Should().Contain("outside the working window");

    [Fact]
    public void In_a_night_shift_a_break_must_still_fall_inside_the_night()
    {
        WorkingWindow.Problems(Day("18:00", "03:00", ("02:30", "03:00"))).Should().BeEmpty();
        WorkingWindow.Problems(Day("18:00", "03:00", ("17:00", "18:30"))).Should().ContainSingle().Which.Should().Contain("outside");
        WorkingWindow.Problems(Day("18:00", "03:00", ("03:00", "04:00"))).Should().ContainSingle().Which.Should().Contain("outside");
    }

    [Fact]
    public void Breaks_that_leave_no_working_time_are_refused()
        => WorkingWindow.Problems(Day("12:00", "13:00", ("12:00", "13:00"))).Should().ContainSingle().Which.Should().Contain("no working time");

    [Fact]
    public void At_most_four_breaks_a_day()
        => WorkingWindow.Problems(Day("08:00", "18:00", ("09:00", "09:10"), ("10:00", "10:10"), ("11:00", "11:10"), ("12:00", "12:10"), ("13:00", "13:10")))
            .Should().Contain(p => p.Contains("at most 4"));

    [Fact]
    public void A_window_is_placed_in_the_persons_own_time_zone()
    {
        // 08:30 in Kolkata (+05:30) is 03:00 UTC; the same wall times in New York (-05:00 in January) are 13:30 UTC.
        var day = Day("08:30", "17:30", ("12:30", "13:30"));
        var india = WorkingWindow.Instants(new DateOnly(2026, 1, 5), day, Kolkata);
        var us = WorkingWindow.Instants(new DateOnly(2026, 1, 5), day, NewYork);
        india.StartUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 3, 0, 0, TimeSpan.Zero));
        us.StartUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 13, 30, 0, TimeSpan.Zero));
        (india.UsableMinutes, us.UsableMinutes).Should().Be((480, 480));
        india.BreaksUtc.Should().ContainSingle().Which.StartUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void An_overnight_window_ends_the_next_morning_and_belongs_to_the_day_it_starts()
    {
        var night = WorkingWindow.Instants(new DateOnly(2026, 1, 5), Day("18:00", "03:00", ("00:00", "00:30")), Kolkata);
        night.ShiftDate.Should().Be(new DateOnly(2026, 1, 5));
        night.StartUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 12, 30, 0, TimeSpan.Zero));
        night.EndUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 21, 30, 0, TimeSpan.Zero));
        night.BreaksUtc.Single().StartUtc.Should().Be(new DateTimeOffset(2026, 1, 5, 18, 30, 0, TimeSpan.Zero));
        night.UsableMinutes.Should().Be(510);
    }

    [Fact]
    public void The_spring_forward_night_is_an_hour_shorter()
    {
        // New York, 8 Mar 2026: 02:00 jumps to 03:00. A 22:00-06:00 night really lasts 7 hours.
        var night = WorkingWindow.Instants(new DateOnly(2026, 3, 7), Day("22:00", "06:00"), NewYork);
        (night.EndUtc - night.StartUtc).Should().Be(TimeSpan.FromHours(7));
        night.UsableMinutes.Should().Be(420);
    }

    [Fact]
    public void The_fall_back_night_is_an_hour_longer()
    {
        // New York, 1 Nov 2026: 02:00 goes back to 01:00. A 22:00-06:00 night really lasts 9 hours.
        var night = WorkingWindow.Instants(new DateOnly(2026, 10, 31), Day("22:00", "06:00"), NewYork);
        (night.EndUtc - night.StartUtc).Should().Be(TimeSpan.FromHours(9));
        night.UsableMinutes.Should().Be(540);
    }

    [Fact]
    public void A_start_in_the_skipped_hour_moves_forward_by_the_gap()
    {
        // 02:30 does not exist on 8 Mar 2026 in New York; it is read as 03:30 EDT = 07:30 UTC.
        var utc = TimeZones.WallToUtc(new DateTime(2026, 3, 8, 2, 30, 0), NewYork, earlierIfAmbiguous: true);
        utc.Should().Be(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void In_the_repeated_hour_a_start_takes_the_first_occurrence_and_an_end_the_second()
    {
        // 01:30 happens twice on 1 Nov 2026 in New York: 05:30 UTC (EDT) and then 06:30 UTC (EST).
        TimeZones.WallToUtc(new DateTime(2026, 11, 1, 1, 30, 0), NewYork, earlierIfAmbiguous: true)
            .Should().Be(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero));
        TimeZones.WallToUtc(new DateTime(2026, 11, 1, 1, 30, 0), NewYork, earlierIfAmbiguous: false)
            .Should().Be(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Time_zones_are_stored_as_IANA_ids()
    {
        TimeZones.ToIana("Asia/Kolkata").Should().Be("Asia/Kolkata");
        TimeZones.ToIana("UTC").Should().Be("UTC");
        TimeZones.ToIana("").Should().BeNull();
        TimeZones.ToIana("not a zone!").Should().BeNull();
        if (TimeZones.HostKnowsIana)
            // Only a host that reads IANA can tell a real zone from a well-formed made-up one.
            TimeZones.ToIana("Not/AZone").Should().BeNull();
    }
}
