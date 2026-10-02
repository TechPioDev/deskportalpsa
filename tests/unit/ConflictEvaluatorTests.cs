using Desk.Domain.Workforce;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Whether a piece of work fits at a proposed time, and what is in the way when it does not: which
/// conflicts stop it outright, which need a deliberate override, and which are only worth knowing.
/// </summary>
public class ConflictEvaluatorTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static CapacityDay StandardDay() => new CapacityDay(Monday, CapacityDay.Kolkata).Works("08:30", "17:30", ("12:30", "13:30"));

    private static ConflictContext Context(CapacityDay day, bool active = true, bool schedulable = true, params RequiredSkill[] skills)
        => new(active, schedulable, [(day.Input, day.Capacity)], skills);

    private static ConflictResult Evaluate(CapacityDay day, string from, string to, bool tentative = false, params RequiredSkill[] skills)
        => ConflictEvaluator.Evaluate(day.At(from, to), tentative, Context(day, skills: skills));

    [Fact]
    public void Work_in_a_free_slot_has_nothing_in_its_way()
    {
        var result = Evaluate(StandardDay().Confirmed("08:30", "10:00"), "10:00", "11:30");
        result.CanSchedule.Should().BeTrue();
        result.CanOverride.Should().BeFalse("there is nothing to override");
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Confirmed_work_in_the_way_is_a_hard_conflict_that_needs_an_override()
    {
        var workId = Guid.NewGuid();
        var day = StandardDay().Confirmed("10:00", "11:00", workId: workId);
        var result = Evaluate(day, "10:30", "11:30");

        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeTrue();
        var conflict = result.Conflicts.Should().ContainSingle().Subject;
        conflict.Type.Should().Be(ConflictType.HardConflict);
        conflict.Severity.Should().Be(ConflictSeverity.Overridable);
        conflict.When.Should().Be(day.At("10:30", "11:00"), "the conflict is the part that overlaps");
        conflict.BlockingWorkId.Should().Be(workId);
    }

    [Fact]
    public void Work_the_asker_may_not_see_is_reported_as_taken_time_and_nothing_more()
    {
        // The allocation reader strips the id of work the caller has no right to see.
        var result = Evaluate(StandardDay().Confirmed("10:00", "11:00", workId: null), "10:30", "11:30");
        var conflict = result.Conflicts.Should().ContainSingle().Subject;
        conflict.BlockingWorkId.Should().BeNull();
        conflict.Message.Should().Be("Already has confirmed work during this period.", "no title, client or ticket number leaks through the message");
    }

    [Fact]
    public void Tentative_work_in_the_way_is_only_a_warning()
    {
        var result = Evaluate(StandardDay().Tentative("10:00", "11:00"), "10:30", "11:30");
        result.CanSchedule.Should().BeTrue();
        result.Conflicts.Should().ContainSingle().Which.Should().Match<Conflict>(c => c.Type == ConflictType.TentativeConflict && c.Severity == ConflictSeverity.Warning);
    }

    [Fact]
    public void Work_across_a_break_is_a_warning()
    {
        var day = StandardDay();
        var result = Evaluate(day, "12:00", "14:00");
        result.CanSchedule.Should().BeTrue();
        var conflict = result.Conflicts.Should().ContainSingle().Subject;
        (conflict.Type, conflict.Severity, conflict.When).Should().Be((ConflictType.BreakConflict, ConflictSeverity.Warning, day.At("12:30", "13:30")));
    }

    [Fact]
    public void Work_while_marked_unavailable_is_blocked_and_cannot_be_overridden()
    {
        var day = StandardDay().Unavailable("15:00", "16:00");
        var result = Evaluate(day, "14:30", "15:30");
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeFalse("the unavailable period has to be changed instead");
        var overlap = day.At("15:00", "15:30");
        result.Conflicts.Should().Contain(c => c.Type == ConflictType.UnavailableConflict && c.Severity == ConflictSeverity.Block && c.When == overlap);
    }

    [Fact]
    public void A_day_taken_off_in_full_blocks_work_in_its_working_hours()
    {
        var result = Evaluate(StandardDay().UnavailableAllDay(), "09:00", "10:00");
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeFalse();
        result.Conflicts.Should().ContainSingle().Which.Type.Should().Be(ConflictType.UnavailableConflict,
            "a day off is reported as unavailable, not as outside working hours as well");
    }

    [Fact]
    public void Work_outside_working_hours_needs_an_override()
    {
        var day = StandardDay();
        var result = Evaluate(day, "17:00", "19:00");
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeTrue();
        var afterHours = day.At("17:30", "19:00");
        result.Conflicts.Should().Contain(c => c.Type == ConflictType.OutsideWorkingWindow && c.Severity == ConflictSeverity.Overridable && c.When == afterHours);
    }

    [Fact]
    public void Extra_availability_is_working_time()
    {
        Evaluate(StandardDay().Extra("18:00", "20:00"), "18:30", "19:30").CanSchedule.Should().BeTrue();
    }

    [Fact]
    public void Work_on_a_day_off_is_outside_working_hours()
    {
        var dayOff = new CapacityDay(Monday, CapacityDay.Kolkata);
        var result = Evaluate(dayOff, "09:00", "10:00");
        result.Conflicts.Should().ContainSingle().Which.Type.Should().Be(ConflictType.OutsideWorkingWindow);
        result.Conflicts.Should().NotContain(c => c.Type == ConflictType.OverCapacity, "there is no capacity to exceed - it is simply not a working day");
    }

    [Fact]
    public void Work_longer_than_everything_left_in_the_day_is_over_capacity()
    {
        // Seven of the eight hours are booked: one hour is left, and two are asked for.
        var day = StandardDay().Confirmed("08:30", "12:30").Confirmed("13:30", "16:30");
        var result = Evaluate(day, "15:30", "17:30");
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeTrue();
        result.Conflicts.Select(c => c.Type).Should().BeEquivalentTo([ConflictType.HardConflict, ConflictType.OverCapacity]);
        result.Conflicts.Single(c => c.Type == ConflictType.OverCapacity).Message.Should().Be("Needs 2h, and only 1h of capacity is left.");
    }

    [Fact]
    public void Work_that_fits_the_day_but_clashes_at_that_time_is_not_over_capacity()
    {
        // Five hours are free in the day; the clash is with that hour, not with the day.
        var result = Evaluate(StandardDay().Confirmed("09:00", "12:00"), "11:00", "12:00");
        result.Conflicts.Select(c => c.Type).Should().Equal(ConflictType.HardConflict);
    }

    [Fact]
    public void A_missing_skill_is_a_warning_naming_the_skill()
    {
        var result = Evaluate(StandardDay(), "09:00", "10:00", tentative: false,
            new RequiredSkill(Guid.NewGuid(), "SonicWall", Held: false), new RequiredSkill(Guid.NewGuid(), "Microsoft 365", Held: true));
        result.CanSchedule.Should().BeTrue();
        result.Conflicts.Should().ContainSingle().Which.Should().Match<Conflict>(c =>
            c.Type == ConflictType.SkillWarning && c.Severity == ConflictSeverity.Warning && c.Message == "Does not hold the skill \"SonicWall\".");
    }

    [Theory]
    [InlineData(false, true, "This person's account is inactive.")]
    [InlineData(true, false, "This person is not offered for planned work.")]
    public void Someone_not_offered_for_planned_work_cannot_be_given_any(bool active, bool schedulable, string message)
    {
        var day = StandardDay();
        var result = ConflictEvaluator.Evaluate(day.At("09:00", "10:00"), false, Context(day, active, schedulable));
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeFalse();
        result.Conflicts.Should().ContainSingle().Which.Should().Match<Conflict>(c =>
            c.Type == ConflictType.NotSchedulable && c.Severity == ConflictSeverity.Block && c.Message == message);
    }

    [Fact]
    public void A_tentative_proposal_holds_no_capacity_so_overridable_conflicts_are_only_warnings()
    {
        var day = StandardDay().Confirmed("10:00", "11:00");
        var clash = Evaluate(day, "10:30", "11:30", tentative: true);
        clash.CanSchedule.Should().BeTrue();
        clash.Conflicts.Should().ContainSingle().Which.Should().Match<Conflict>(c => c.Type == ConflictType.HardConflict && c.Severity == ConflictSeverity.Warning);

        Evaluate(StandardDay(), "17:00", "19:00", tentative: true).CanSchedule.Should().BeTrue();
        // A block stays a block even for pencilled-in work.
        Evaluate(StandardDay().Unavailable("15:00", "16:00"), "15:00", "16:00", tentative: true).CanSchedule.Should().BeFalse();
    }

    [Fact]
    public void Every_conflict_is_reported_at_once()
    {
        var day = StandardDay().Confirmed("11:00", "12:00").Tentative("14:00", "14:30").Unavailable("15:00", "16:00");
        var result = Evaluate(day, "11:30", "18:00");
        result.Conflicts.Select(c => c.Type).Distinct().Should().BeEquivalentTo([
            ConflictType.HardConflict, ConflictType.TentativeConflict, ConflictType.UnavailableConflict,
            ConflictType.BreakConflict, ConflictType.OutsideWorkingWindow, ConflictType.OverCapacity]);
        result.CanSchedule.Should().BeFalse();
        result.CanOverride.Should().BeFalse("one of them is a block");
    }

    [Fact]
    public void A_night_shift_is_checked_across_midnight()
    {
        var night = new CapacityDay(Monday, CapacityDay.Kolkata).Works("18:00", "03:00", ("22:00", "22:30")).Confirmed("23:00", "01:00");
        ConflictEvaluator.Evaluate(night.At("01:00", "02:30", plusDays: 1), false, Context(night)).CanSchedule.Should().BeTrue();
        ConflictEvaluator.Evaluate(night.At("00:30", "01:30", plusDays: 1), false, Context(night))
            .Conflicts.Should().ContainSingle().Which.Type.Should().Be(ConflictType.HardConflict);
        ConflictEvaluator.Evaluate(night.At("02:30", "03:30", plusDays: 1), false, Context(night))
            .Conflicts.Should().ContainSingle().Which.Type.Should().Be(ConflictType.OutsideWorkingWindow);
    }

    [Fact]
    public void Checking_again_after_someone_else_books_the_slot_finds_the_new_work()
    {
        // What a screen showed as free is not a reservation: the same check on fresh data must refuse.
        var day = StandardDay();
        Evaluate(day, "15:00", "16:00").CanSchedule.Should().BeTrue("manager A sees 15:00-16:00 free");
        Evaluate(day, "15:00", "16:00").CanSchedule.Should().BeTrue("so does manager B, a moment later");

        day.Confirmed("15:00", "16:00");
        Evaluate(day, "15:00", "16:00").CanSchedule.Should().BeFalse("manager A booked it; B's check at commit time now sees that work");
    }

    [Fact]
    public void A_proposal_must_end_after_it_starts()
    {
        var day = StandardDay();
        var act = () => ConflictEvaluator.Evaluate(new Interval(day.At("10:00", "11:00").End, day.At("10:00", "11:00").Start), false, Context(day));
        act.Should().Throw<ArgumentException>();
    }
}
