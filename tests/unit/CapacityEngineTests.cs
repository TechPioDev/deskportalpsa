using Desk.Domain.Common;
using Desk.Domain.Workforce;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The capacity arithmetic on its own, with no database: how much time a person is offered for
/// planned work on a date, what is left of it, and exactly when they are free. Capacity for planning
/// - never attendance, and never a measure of anyone's performance.
/// </summary>
public class CapacityEngineTests
{
    private static readonly TimeZoneInfo Kolkata = CapacityDay.Kolkata;
    private static readonly TimeZoneInfo NewYork = CapacityDay.NewYork;
    private static readonly TimeZoneInfo London = CapacityDay.London;
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static CapacityDay StandardDay(TimeZoneInfo? zone = null) => new CapacityDay(Monday, zone ?? Kolkata).Works("08:30", "17:30", ("12:30", "13:30"));

    private static void FormulaHolds(DayCapacity c)
    {
        c.UsableMinutes.Should().Be(c.GrossMinutes - c.BreakMinutes - c.UnavailableMinutes + c.AdditionalMinutes);
        c.RemainingConfirmedMinutes.Should().Be(c.UsableMinutes - c.ConfirmedMinutes);
        c.ProjectedRemainingMinutes.Should().Be(c.RemainingConfirmedMinutes - c.TentativeMinutes);
        new[] { c.GrossMinutes, c.BreakMinutes, c.UnavailableMinutes, c.AdditionalMinutes, c.UsableMinutes, c.ConfirmedMinutes,
            c.TentativeMinutes, c.RemainingConfirmedMinutes, c.ProjectedRemainingMinutes }.Should().OnlyContain(m => m >= 0, "no figure is ever negative");
        Intervals.Minutes(c.FreeSlots).Should().Be(c.RemainingConfirmedMinutes, "the free slots ARE the remaining capacity");
        Intervals.Minutes(c.ProjectedFreeSlots).Should().Be(c.ProjectedRemainingMinutes);
    }

    // ---- interval arithmetic -------------------------------------------------------------------

    [Fact]
    public void Overlapping_stretches_merge_into_one()
    {
        var d = new CapacityDay(Monday, Kolkata);
        var merged = Intervals.Normalize([d.At("11:00", "12:00"), d.At("10:00", "11:30")]);
        merged.Should().Equal(d.At("10:00", "12:00"));
        Intervals.Minutes(merged).Should().Be(120, "10:00-11:30 and 11:00-12:00 are two hours, not two and a half");
    }

    [Fact]
    public void Stretches_that_touch_merge_and_stretches_with_a_gap_do_not()
    {
        var d = new CapacityDay(Monday, Kolkata);
        Intervals.Normalize([d.At("10:00", "11:00"), d.At("11:00", "12:00")]).Should().Equal(d.At("10:00", "12:00"));
        Intervals.Normalize([d.At("10:00", "11:00"), d.At("11:01", "12:00")]).Should().HaveCount(2);
    }

    [Fact]
    public void Subtracting_cuts_out_the_middle_the_edges_and_nothing_that_is_elsewhere()
    {
        var d = new CapacityDay(Monday, Kolkata);
        Intervals.Subtract([d.At("08:00", "18:00")], [d.At("07:00", "09:00"), d.At("12:00", "13:00"), d.At("17:30", "19:00"), d.At("20:00", "21:00")])
            .Should().Equal(d.At("09:00", "12:00"), d.At("13:00", "17:30"));
        Intervals.Subtract([d.At("08:00", "09:00")], [d.At("07:00", "10:00")]).Should().BeEmpty();
        Intervals.Intersect([d.At("08:00", "12:00"), d.At("13:00", "17:00")], [d.At("11:00", "14:00")])
            .Should().Equal(d.At("11:00", "12:00"), d.At("13:00", "14:00"));
    }

    [Fact]
    public void Work_that_ends_when_other_work_starts_does_not_overlap_it()
    {
        var d = new CapacityDay(Monday, Kolkata);
        d.At("09:00", "10:00").Overlaps(d.At("10:00", "11:00")).Should().BeFalse();
        d.At("09:00", "10:01").Overlaps(d.At("10:00", "11:00")).Should().BeTrue();
    }

    // ---- the capacity formula ------------------------------------------------------------------

    [Fact]
    public void A_standard_day_is_the_window_less_the_break()
    {
        var day = StandardDay();
        var c = day.Capacity;
        (c.GrossMinutes, c.BreakMinutes, c.UsableMinutes, c.RemainingConfirmedMinutes).Should().Be((540, 60, 480, 480));
        c.FreeSlots.Should().Equal(day.At("08:30", "12:30"), day.At("13:30", "17:30"));
        FormulaHolds(c);
    }

    [Fact]
    public void A_day_the_schedule_does_not_work_offers_nothing()
    {
        var c = new CapacityDay(Monday, Kolkata).Capacity;
        (c.GrossMinutes, c.UsableMinutes, c.RemainingConfirmedMinutes).Should().Be((0, 0, 0));
        c.FreeSlots.Should().BeEmpty();
        FormulaHolds(c);
    }

    [Fact]
    public void Several_breaks_each_come_off_and_overlapping_ones_come_off_once()
    {
        var several = new CapacityDay(Monday, Kolkata).Works("08:00", "18:00", ("10:00", "10:15"), ("13:00", "13:45"), ("16:00", "16:15")).Capacity;
        (several.BreakMinutes, several.UsableMinutes).Should().Be((75, 525));
        FormulaHolds(several);

        // A schedule cannot be saved with overlapping breaks, but the arithmetic must not depend on that.
        var d = new CapacityDay(Monday, Kolkata).Works("08:00", "18:00");
        var overlapping = d.RawBreaks(d.At("12:00", "13:00"), d.At("12:30", "13:30")).Capacity;
        (overlapping.BreakMinutes, overlapping.UsableMinutes).Should().Be((90, 510));
        FormulaHolds(overlapping);
    }

    [Fact]
    public void Part_of_a_day_unavailable_comes_off_with_the_break_and_not_twice()
    {
        // Working 08:30-17:30, break 12:30-13:30, unavailable 15:00-16:00.
        var day = StandardDay().Unavailable("15:00", "16:00");
        var c = day.Capacity;
        (c.UnavailableMinutes, c.UsableMinutes).Should().Be((60, 420));
        c.FreeSlots.Should().Equal(day.At("08:30", "12:30"), day.At("13:30", "15:00"), day.At("16:00", "17:30"));
        FormulaHolds(c);

        // Unavailable 12:00-14:00 covers the whole break: only the hour of working time it takes counts.
        var overBreak = StandardDay().Unavailable("12:00", "14:00").Capacity;
        (overBreak.BreakMinutes, overBreak.UnavailableMinutes, overBreak.UsableMinutes).Should().Be((60, 60, 420));
        FormulaHolds(overBreak);
    }

    [Fact]
    public void Overlapping_unavailable_periods_are_one_period()
    {
        // 10:00-11:30 and 11:00-12:00 are 10:00-12:00: two hours, not two and a half.
        var c = StandardDay().Unavailable("10:00", "11:30").Unavailable("11:00", "12:00").Capacity;
        (c.UnavailableMinutes, c.UsableMinutes).Should().Be((120, 360));
        FormulaHolds(c);
    }

    [Fact]
    public void Unavailable_periods_that_touch_are_one_period()
    {
        var day = StandardDay().Unavailable("10:00", "11:00").Unavailable("11:00", "12:00");
        var c = day.Capacity;
        c.UnavailableMinutes.Should().Be(120);
        c.FreeSlots.Should().Equal(day.At("08:30", "10:00"), day.At("12:00", "12:30"), day.At("13:30", "17:30"));
    }

    [Fact]
    public void Unavailable_time_outside_working_hours_takes_nothing()
    {
        var c = StandardDay().Unavailable("19:00", "20:00").Unavailable("06:00", "08:30").Capacity;
        (c.UnavailableMinutes, c.UsableMinutes).Should().Be((0, 480));
    }

    [Fact]
    public void A_full_day_unavailable_leaves_no_capacity_and_no_free_slots()
    {
        var c = StandardDay().UnavailableAllDay().Capacity;
        (c.UsableMinutes, c.RemainingConfirmedMinutes, c.ProjectedRemainingMinutes).Should().Be((0, 0, 0));
        c.UnavailableMinutes.Should().Be(480);
        c.FreeSlots.Should().BeEmpty();
        c.ProjectedFreeSlots.Should().BeEmpty();
        FormulaHolds(c);
    }

    [Fact]
    public void Extra_availability_adds_time_outside_the_working_window()
    {
        var day = StandardDay().Extra("18:00", "20:00");
        var c = day.Capacity;
        (c.AdditionalMinutes, c.UsableMinutes).Should().Be((120, 600));
        c.FreeSlots.Should().Equal(day.At("08:30", "12:30"), day.At("13:30", "17:30"), day.At("18:00", "20:00"));
        FormulaHolds(c);

        // On a day off it is all the capacity there is.
        var dayOff = new CapacityDay(Monday, Kolkata).Extra("10:00", "14:00").Capacity;
        (dayOff.GrossMinutes, dayOff.AdditionalMinutes, dayOff.UsableMinutes).Should().Be((0, 240, 240));
        FormulaHolds(dayOff);
    }

    [Fact]
    public void Extra_availability_that_touches_the_window_makes_one_longer_slot()
    {
        var day = StandardDay().Extra("17:30", "19:00");
        day.Capacity.FreeSlots.Should().Equal(day.At("08:30", "12:30"), day.At("13:30", "19:00"));
    }

    [Fact]
    public void Unavailable_wins_where_it_overlaps_extra_availability_and_a_full_day_cancels_it()
    {
        var partly = StandardDay().Extra("18:00", "20:00").Unavailable("19:00", "21:00").Capacity;
        (partly.AdditionalMinutes, partly.UsableMinutes).Should().Be((60, 540));
        FormulaHolds(partly);

        var cancelled = StandardDay().Extra("18:00", "20:00").UnavailableAllDay().Capacity;
        (cancelled.AdditionalMinutes, cancelled.UsableMinutes).Should().Be((0, 0));
        FormulaHolds(cancelled);
    }

    // ---- planned work --------------------------------------------------------------------------

    [Fact]
    public void Confirmed_work_leaves_exactly_the_gaps_between_it()
    {
        // Working 08:30-17:30, break 12:30-13:30; scheduled 08:30-09:30, 10:30-11:30, 13:30-15:00.
        var day = StandardDay().Confirmed("08:30", "09:30").Confirmed("10:30", "11:30").Confirmed("13:30", "15:00");
        var c = day.Capacity;
        c.FreeSlots.Should().Equal(day.At("09:30", "10:30"), day.At("11:30", "12:30"), day.At("15:00", "17:30"));
        (c.ConfirmedMinutes, c.RemainingConfirmedMinutes).Should().Be((210, 270));
        FormulaHolds(c);
    }

    [Fact]
    public void Confirmed_and_tentative_work_are_never_mixed()
    {
        // Usable 8h; confirmed 5h; tentative 1h -> 3h remaining confirmed, 2h projected.
        var day = StandardDay().Confirmed("08:30", "12:30").Confirmed("13:30", "14:30").Tentative("15:00", "16:00");
        var c = day.Capacity;
        (c.UsableMinutes, c.ConfirmedMinutes, c.TentativeMinutes, c.RemainingConfirmedMinutes, c.ProjectedRemainingMinutes)
            .Should().Be((480, 300, 60, 180, 120));
        // Tentative work holds no capacity: its hour is still a free slot until it is confirmed.
        c.FreeSlots.Should().Equal(day.At("14:30", "17:30"));
        c.ProjectedFreeSlots.Should().Equal(day.At("14:30", "15:00"), day.At("16:00", "17:30"));
        FormulaHolds(c);
    }

    [Fact]
    public void Tentative_work_under_confirmed_work_is_not_counted_again()
    {
        var c = StandardDay().Confirmed("09:00", "11:00").Tentative("10:00", "12:00").Capacity;
        (c.ConfirmedMinutes, c.TentativeMinutes, c.ProjectedRemainingMinutes).Should().Be((120, 60, 300));
        FormulaHolds(c);
    }

    [Fact]
    public void Work_that_ends_exactly_when_the_next_starts_leaves_no_gap_and_no_overlap()
    {
        var day = StandardDay().Confirmed("08:30", "09:30").Confirmed("09:30", "10:30");
        var c = day.Capacity;
        c.ConfirmedMinutes.Should().Be(120);
        c.FreeSlots[0].Should().Be(day.At("10:30", "12:30"));
        FormulaHolds(c);
    }

    [Fact]
    public void Two_confirmed_pieces_that_overlap_take_that_time_once_and_work_outside_hours_takes_none()
    {
        var c = StandardDay().Confirmed("09:00", "11:00").Confirmed("10:00", "12:00").Confirmed("19:00", "21:00").Capacity;
        (c.ConfirmedMinutes, c.RemainingConfirmedMinutes).Should().Be((180, 300));
        FormulaHolds(c);
    }

    [Fact]
    public void A_day_fully_booked_has_no_capacity_left_and_never_less_than_none()
    {
        var c = StandardDay().Confirmed("08:00", "18:00").Tentative("09:00", "10:00").Capacity;
        (c.ConfirmedMinutes, c.RemainingConfirmedMinutes, c.TentativeMinutes, c.ProjectedRemainingMinutes).Should().Be((480, 0, 0, 0));
        c.FreeSlots.Should().BeEmpty();
        FormulaHolds(c);
    }

    // ---- finding a slot ------------------------------------------------------------------------

    [Fact]
    public void A_task_needs_one_continuous_slot_two_short_ones_do_not_add_up()
    {
        // Free: 09:30-10:30 (60m), 11:30-12:30 (60m), 15:00-17:30 (150m).
        var day = StandardDay().Confirmed("08:30", "09:30").Confirmed("10:30", "11:30").Confirmed("13:30", "15:00");
        var free = day.Capacity.FreeSlots;

        CapacityCalculator.FirstFit(free, 90).Should().Be(day.At("15:00", "16:30"), "90 minutes only fits in the afternoon slot");
        CapacityCalculator.FirstFit(free, 60).Should().Be(day.At("09:30", "10:30"), "the earliest slot long enough wins");
        CapacityCalculator.FirstFit(free, 150).Should().Be(day.At("15:00", "17:30"));
        CapacityCalculator.FirstFit(free, 151).Should().BeNull("270 minutes are free in total, but no single slot holds 151");
        CapacityCalculator.Fitting(free, 60).Should().HaveCount(3);
        CapacityCalculator.Fitting(free, 0).Should().BeEmpty();
    }

    [Fact]
    public void A_slot_search_can_be_limited_to_part_of_the_day()
    {
        var day = StandardDay().Confirmed("13:30", "14:00");
        var free = day.Capacity.FreeSlots;
        CapacityCalculator.FirstFit(free, 90, day.At("13:00", "17:30")).Should().Be(day.At("14:00", "15:30"));
        CapacityCalculator.FirstFit(free, 90, day.At("11:30", "14:30")).Should().BeNull("only 11:30-12:30 and 14:00-14:30 fall in that window");
    }

    // ---- overnight -----------------------------------------------------------------------------

    [Fact]
    public void A_night_shift_is_one_day_of_capacity_across_two_calendar_dates()
    {
        // Working 18:00-03:00, break 22:00-22:30, scheduled 23:00-01:00.
        var night = new CapacityDay(Monday, Kolkata).Works("18:00", "03:00", ("22:00", "22:30")).Confirmed("23:00", "01:00");
        var c = night.Capacity;
        (c.GrossMinutes, c.BreakMinutes, c.UsableMinutes, c.ConfirmedMinutes, c.RemainingConfirmedMinutes).Should().Be((540, 30, 510, 120, 390));
        c.FreeSlots.Should().Equal(night.At("18:00", "22:00"), night.At("22:30", "23:00"), night.At("01:00", "03:00", plusDays: 1));
        c.Date.Should().Be(Monday, "the shift belongs to the day it starts on");
        FormulaHolds(c);
    }

    [Fact]
    public void An_appointment_after_midnight_comes_off_the_night_shift_that_began_the_day_before()
    {
        var night = new CapacityDay(Monday, Kolkata).Works("18:00", "03:00").Unavailable("01:00", "02:00", plusDays: 1);
        var c = night.Capacity;
        (c.UnavailableMinutes, c.UsableMinutes).Should().Be((60, 480));
        FormulaHolds(c);
    }

    // ---- time zones and clock changes ----------------------------------------------------------

    [Fact]
    public void The_same_schedule_in_three_zones_is_the_same_capacity_at_different_instants()
    {
        var kolkata = StandardDay(Kolkata).Capacity;
        var newYork = StandardDay(NewYork).Capacity;
        var london = StandardDay(London).Capacity;
        new[] { kolkata, newYork, london }.Should().OnlyContain(c => c.UsableMinutes == 480);

        // 08:30 on 5 Oct 2026: 03:00 UTC in Kolkata (+05:30), 12:30 UTC in New York (EDT), 07:30 UTC in London (BST).
        kolkata.Window!.Value.Start.Should().Be(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero));
        newYork.Window!.Value.Start.Should().Be(new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero));
        london.Window!.Value.Start.Should().Be(new DateTimeOffset(2026, 10, 5, 7, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_night_that_loses_an_hour_to_the_clocks_has_an_hour_less_capacity()
    {
        // New York, 8 Mar 2026: 02:00 jumps to 03:00. 22:00-06:00 really lasts 7 hours.
        var night = new CapacityDay(new DateOnly(2026, 3, 7), NewYork).Works("22:00", "06:00", ("01:00", "01:30"));
        var c = night.Capacity;
        (c.GrossMinutes, c.BreakMinutes, c.UsableMinutes).Should().Be((420, 30, 390));
        FormulaHolds(c);
        // A day shift on the same date is untouched: it starts after the change.
        new CapacityDay(new DateOnly(2026, 3, 8), NewYork).Works("08:30", "17:30", ("12:30", "13:30")).Capacity.UsableMinutes.Should().Be(480);
    }

    [Fact]
    public void A_break_set_in_the_hour_the_clocks_skip_is_still_one_break_of_its_own_length()
    {
        // 02:15-02:45 does not exist on 8 Mar 2026 in New York. The break moves forward with the clock
        // (03:15-03:45) and stays thirty minutes: no negative time, no missing hour.
        var night = new CapacityDay(new DateOnly(2026, 3, 7), NewYork).Works("22:00", "06:00", ("02:15", "02:45"));
        var c = night.Capacity;
        (c.GrossMinutes, c.BreakMinutes, c.UsableMinutes).Should().Be((420, 30, 390));
        c.Breaks.Single().Should().Be(new Interval(new DateTimeOffset(2026, 3, 8, 7, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 8, 7, 45, 0, TimeSpan.Zero)));
        FormulaHolds(c);
    }

    [Fact]
    public void A_night_that_gains_an_hour_has_an_hour_more_capacity_and_no_hour_counted_twice()
    {
        // New York, 1 Nov 2026: 02:00 goes back to 01:00. 22:00-06:00 really lasts 9 hours.
        var night = new CapacityDay(new DateOnly(2026, 10, 31), NewYork).Works("22:00", "06:00");
        var c = night.Capacity;
        (c.GrossMinutes, c.UsableMinutes).Should().Be((540, 540));
        c.FreeSlots.Should().ContainSingle().Which.Length.Should().Be(TimeSpan.FromHours(9));
        FormulaHolds(c);
    }

    [Fact]
    public void A_break_in_the_hour_the_clocks_repeat_keeps_its_own_length()
    {
        // 01:15-01:45 happens twice on 1 Nov 2026 in New York. Reading the end off the clock would make
        // the break ninety minutes (first 01:15 to second 01:45) and take an hour of capacity with it.
        var night = new CapacityDay(new DateOnly(2026, 10, 31), NewYork).Works("22:00", "06:00", ("01:15", "01:45"));
        var c = night.Capacity;
        (c.GrossMinutes, c.BreakMinutes, c.UsableMinutes).Should().Be((540, 30, 510));
        c.Breaks.Single().Start.Should().Be(new DateTimeOffset(2026, 11, 1, 5, 15, 0, TimeSpan.Zero), "the first 01:15, still on summer time");
        FormulaHolds(c);
    }

    [Fact]
    public void Work_booked_across_the_repeated_hour_takes_its_real_length_once()
    {
        // Booked 00:30 EDT to 01:30 EST = 04:30-06:30 UTC: two real hours on a nine-hour night.
        var night = new CapacityDay(new DateOnly(2026, 10, 31), NewYork).Works("22:00", "06:00");
        var booked = new Interval(new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));
        var input = night.Input with { Allocations = [new AllocatedSpan(Guid.NewGuid(), booked, true)] };
        var c = CapacityCalculator.ForDay(input);
        (c.ConfirmedMinutes, c.RemainingConfirmedMinutes).Should().Be((120, 420));
        c.FreeSlots.Should().HaveCount(2);
        FormulaHolds(c);
    }

    [Fact]
    public void London_clock_changes_are_handled_the_same_way()
    {
        // 29 Mar 2026: 01:00 jumps to 02:00. 25 Oct 2026: 02:00 goes back to 01:00.
        new CapacityDay(new DateOnly(2026, 3, 28), London).Works("22:00", "06:00").Capacity.UsableMinutes.Should().Be(420);
        new CapacityDay(new DateOnly(2026, 10, 24), London).Works("22:00", "06:00").Capacity.UsableMinutes.Should().Be(540);
        // Kolkata has no clock changes: every night is its nominal length.
        new CapacityDay(new DateOnly(2026, 3, 28), Kolkata).Works("22:00", "06:00").Capacity.UsableMinutes.Should().Be(480);
    }
}
