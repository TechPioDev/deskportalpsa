namespace Desk.Domain.Workforce;

/// <summary>
/// Work already placed in someone's time. Confirmed work takes capacity; tentative (pencilled-in)
/// work takes none until it is confirmed, and is always reported separately. <see cref="WorkId"/> is
/// null when the person asking may not see what the work is - they still learn the time is taken.
/// </summary>
public sealed record AllocatedSpan(Guid AppUserId, Interval When, bool Confirmed, Guid? WorkId = null);

/// <summary>Everything that decides one person's capacity for one shift date, as real instants.</summary>
/// <param name="Date">The shift date: the day the working window STARTS on, in the person's zone.</param>
/// <param name="Window">The working window, or null on a day the schedule does not work.</param>
/// <param name="Breaks">Planned breaks inside the window.</param>
/// <param name="UnavailableAllDay">A full-day unavailable period covers this date: the whole day is off.</param>
/// <param name="Unavailable">Part-day unavailable periods, wherever they fall; only what lands on working time counts.</param>
/// <param name="Additional">Extra availability that belongs to this date, already outside every working window.</param>
/// <param name="Allocations">Planned work near this date; only what lands on available time counts.</param>
public sealed record DayCapacityInput(
    DateOnly Date,
    Interval? Window,
    IReadOnlyList<Interval> Breaks,
    bool UnavailableAllDay,
    IReadOnlyList<Interval> Unavailable,
    IReadOnlyList<Interval> Additional,
    IReadOnlyList<AllocatedSpan> Allocations);

/// <summary>
/// One person's capacity for one shift date.
///
///   Gross working window - breaks - unavailable + additional availability = USABLE capacity
///   Usable - confirmed work                                               = REMAINING (confirmed)
///   Remaining - tentative work                                            = PROJECTED remaining
///
/// The minute figures always satisfy those lines exactly, and none is ever negative, because each is
/// measured from the set of real time left after the step - not by subtracting durations, which
/// would count an unavailable hour that overlaps a break twice.
/// </summary>
public sealed record DayCapacity(
    DateOnly Date,
    Interval? Window,
    bool UnavailableAllDay,
    int GrossMinutes,
    int BreakMinutes,
    int UnavailableMinutes,
    int AdditionalMinutes,
    int UsableMinutes,
    int ConfirmedMinutes,
    int TentativeMinutes,
    int RemainingConfirmedMinutes,
    int ProjectedRemainingMinutes,
    IReadOnlyList<Interval> Breaks,
    IReadOnlyList<Interval> Available,
    IReadOnlyList<Interval> FreeSlots,
    IReadOnlyList<Interval> ProjectedFreeSlots);

/// <summary>
/// The capacity arithmetic. Pure: the same inputs always give the same answer, nothing is read from a
/// clock or a database, so it can be called again inside the transaction that books work.
///
/// This is work CAPACITY - how much time is offered for planned work. It says nothing about
/// attendance, and nothing about how well anyone performs.
/// </summary>
public static class CapacityCalculator
{
    public static DayCapacity ForDay(DayCapacityInput input)
    {
        IReadOnlyList<Interval> working = input.Window is { IsEmpty: false } w ? [w] : Intervals.None;
        var gross = Intervals.Minutes(working);

        // 1. Breaks come off the window. Overlapping breaks are merged first, so they come off once.
        var afterBreaks = Intervals.Subtract(working, input.Breaks);
        var breaks = Intervals.Intersect(working, input.Breaks);
        var breakMinutes = gross - Intervals.Minutes(afterBreaks);

        // 2. Unavailable time comes off what the breaks left. A full day takes everything, and
        //    cancels any extra availability set for the same date.
        IReadOnlyList<Interval> afterUnavailable;
        IReadOnlyList<Interval> available;
        if (input.UnavailableAllDay)
        {
            afterUnavailable = Intervals.None;
            available = Intervals.None;
        }
        else
        {
            afterUnavailable = Intervals.Subtract(afterBreaks, input.Unavailable);
            // 3. Extra availability is added - except where the person is also marked unavailable.
            available = Intervals.Union(afterUnavailable, Intervals.Subtract(input.Additional, input.Unavailable));
        }
        var unavailableMinutes = Intervals.Minutes(afterBreaks) - Intervals.Minutes(afterUnavailable);
        var additionalMinutes = Intervals.Minutes(available) - Intervals.Minutes(afterUnavailable);
        var usable = Intervals.Minutes(available);

        // 4. Confirmed work takes capacity where it lands on available time. Two confirmed pieces
        //    that overlap each other (a booking someone forced through) take that time once.
        var confirmed = Intervals.Intersect(available, input.Allocations.Where(a => a.Confirmed).Select(a => a.When));
        var free = Intervals.Subtract(available, confirmed);

        // 5. Tentative work takes nothing yet; it is shown as what would be left if it were confirmed.
        var tentative = Intervals.Intersect(free, input.Allocations.Where(a => !a.Confirmed).Select(a => a.When));
        var projected = Intervals.Subtract(free, tentative);

        // Each "taken" figure is the difference between what was there before and after the step,
        // not its own rounded total - so the three sums hold exactly even when a piece of work starts
        // or ends between whole minutes.
        var remaining = Intervals.Minutes(free);
        var projectedRemaining = Intervals.Minutes(projected);
        return new DayCapacity(
            input.Date, input.Window, input.UnavailableAllDay,
            gross, breakMinutes, unavailableMinutes, additionalMinutes, usable,
            usable - remaining, remaining - projectedRemaining,
            remaining, projectedRemaining,
            breaks, available, free, projected);
    }

    /// <summary>
    /// The earliest place a single continuous piece of work of <paramref name="minutes"/> fits inside
    /// <paramref name="slots"/> (optionally only within <paramref name="within"/>), or null when no
    /// one slot is long enough. Two separate free hours are NOT a two-hour slot.
    /// </summary>
    public static Interval? FirstFit(IEnumerable<Interval> slots, int minutes, Interval? within = null)
        => Fitting(slots, minutes, within).Select(s => (Interval?)new Interval(s.Start, s.Start.AddMinutes(minutes))).FirstOrDefault();

    /// <summary>Every free stretch long enough to hold <paramref name="minutes"/> of continuous work, earliest first.</summary>
    public static IReadOnlyList<Interval> Fitting(IEnumerable<Interval> slots, int minutes, Interval? within = null)
    {
        if (minutes <= 0) return Intervals.None;
        var candidates = within is { } window ? Intervals.Clip(slots, window) : Intervals.Normalize(slots);
        return candidates.Where(s => s.Length.TotalMinutes >= minutes).ToList();
    }
}
