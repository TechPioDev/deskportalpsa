namespace Desk.Domain.Workforce;

/// <summary>What is wrong with putting a piece of work at a given time.</summary>
public enum ConflictType
{
    /// <summary>The person already has confirmed work then.</summary>
    HardConflict = 1,
    /// <summary>The person has pencilled-in (tentative) work then.</summary>
    TentativeConflict = 2,
    /// <summary>It runs into a planned break.</summary>
    BreakConflict = 3,
    /// <summary>The person is marked unavailable then.</summary>
    UnavailableConflict = 4,
    /// <summary>It falls outside the time the person is offered for work.</summary>
    OutsideWorkingWindow = 5,
    /// <summary>It is longer than all the capacity the person has left that day.</summary>
    OverCapacity = 6,
    /// <summary>A skill the work asks for is not one the person holds.</summary>
    SkillWarning = 7,
    /// <summary>The person is not offered for planned work at all (switched off, or their account is inactive).</summary>
    NotSchedulable = 8,
}

/// <summary>What a conflict means for whoever is trying to place the work.</summary>
public enum ConflictSeverity
{
    /// <summary>Worth knowing; the work can still be placed.</summary>
    Warning = 1,
    /// <summary>The work cannot be placed unless someone allowed to overrides it, giving a reason.</summary>
    Overridable = 2,
    /// <summary>The work cannot be placed here. Nobody can override it - change what causes it instead.</summary>
    Block = 3,
}

/// <summary>One conflict: what, how serious, when, and (only if the asker may see it) with which work.</summary>
public sealed record Conflict(ConflictType Type, ConflictSeverity Severity, Interval When, string Message, Guid? BlockingWorkId = null);

/// <summary>
/// The verdict on a proposed piece of work. <see cref="CanSchedule"/> is true only when nothing
/// stands in the way; <see cref="CanOverride"/> when the only things in the way are overridable.
/// </summary>
public sealed record ConflictResult(bool CanSchedule, bool CanOverride, IReadOnlyList<Conflict> Conflicts);

/// <summary>A skill the proposed work asks for, and whether the person holds it.</summary>
public sealed record RequiredSkill(Guid SkillId, string Name, bool Held);

/// <summary>What the evaluation looks at for one person.</summary>
/// <param name="Days">The shift dates the proposal could touch (the day before, the day, the day after), each with its inputs and result.</param>
public sealed record ConflictContext(
    bool PersonIsActive,
    bool PersonIsSchedulable,
    IReadOnlyList<(DayCapacityInput Input, DayCapacity Capacity)> Days,
    IReadOnlyList<RequiredSkill> RequiredSkills);

/// <summary>
/// Decides whether a piece of work fits at a proposed time, and says exactly what is in the way.
/// Deterministic and free of side effects, so the transaction that finally books the work can call
/// it again on fresh data: what a screen showed as free a minute ago is not a reservation.
///
/// Which conflicts block, and which can be overridden:
///
///   NotSchedulable        Block        Switch "offered for planned work" back on instead.
///   UnavailableConflict   Block        Change or remove the unavailable period instead - otherwise
///                                      the record says someone is away while they are booked.
///   HardConflict          Overridable  A double booking needs a deliberate override and a reason.
///   OutsideWorkingWindow  Overridable  Overtime is a decision, not an accident.
///   OverCapacity          Overridable  As above.
///   BreakConflict         Overridable  Work placed over a break takes capacity the break does not
///                                      offer; working through lunch is a decision too. (Phase 2
///                                      called this a warning; placing work made it a decision.)
///   TentativeConflict     Warning      Pencilled-in work holds no capacity.
///   SkillWarning          Warning      Skills guide the choice; they do not forbid it.
///
/// A TENTATIVE proposal holds no capacity itself, so what would be overridable for confirmed work is
/// only a warning for it. Blocks stay blocks.
/// </summary>
public static class ConflictEvaluator
{
    public static ConflictResult Evaluate(Interval proposal, bool tentative, ConflictContext context)
    {
        if (proposal.IsEmpty) throw new ArgumentException("The proposed work must end after it starts.", nameof(proposal));
        var conflicts = new List<Conflict>();
        var overridable = tentative ? ConflictSeverity.Warning : ConflictSeverity.Overridable;
        IReadOnlyList<Interval> wanted = [proposal];

        if (!context.PersonIsActive || !context.PersonIsSchedulable)
            conflicts.Add(new Conflict(ConflictType.NotSchedulable, ConflictSeverity.Block, proposal,
                context.PersonIsActive ? "This person is not offered for planned work." : "This person's account is inactive."));

        // Work already there. Reported per piece of work, so each can be named to those who may see it.
        var allocations = context.Days.SelectMany(d => d.Input.Allocations).Distinct().OrderBy(a => a.When.Start).ToList();
        foreach (var a in allocations.Where(a => a.When.Overlaps(proposal)))
            conflicts.Add(a.Confirmed
                ? new Conflict(ConflictType.HardConflict, overridable, a.When.Intersect(proposal), "Already has confirmed work during this period.", a.WorkId)
                : new Conflict(ConflictType.TentativeConflict, ConflictSeverity.Warning, a.When.Intersect(proposal), "Has tentative work during this period.", a.WorkId));

        // Unavailable: part-day periods, and the working window of any day taken off in full.
        var live = context.Days.Where(d => !d.Input.UnavailableAllDay).ToList();
        var cancelledWindows = context.Days.Where(d => d.Input.UnavailableAllDay && d.Input.Window is not null).Select(d => d.Input.Window!.Value);
        var unavailable = Intervals.Union(context.Days.SelectMany(d => d.Input.Unavailable), cancelledWindows);
        foreach (var piece in Intervals.Intersect(wanted, unavailable))
            conflicts.Add(new Conflict(ConflictType.UnavailableConflict, ConflictSeverity.Block, piece, "Marked unavailable during this period."));

        // Breaks - where the person is not already unavailable (one reason per minute is enough).
        var breaks = Intervals.Subtract(live.SelectMany(d => d.Capacity.Breaks), unavailable);
        foreach (var piece in Intervals.Intersect(wanted, breaks))
            conflicts.Add(new Conflict(ConflictType.BreakConflict, overridable, piece, "Overlaps a planned break."));

        // Outside the offered time: not in a working window, not extra availability, and not already
        // explained as unavailable.
        var offered = Intervals.Union(
            live.Where(d => d.Input.Window is not null).Select(d => d.Input.Window!.Value),
            live.SelectMany(d => d.Input.Additional));
        foreach (var piece in Intervals.Subtract(Intervals.Subtract(wanted, offered), unavailable))
            conflicts.Add(new Conflict(ConflictType.OutsideWorkingWindow, overridable, piece, "Outside this person's working hours."));

        // More than the day has left, however it is arranged: the remaining capacity of every shift
        // date whose offered time the proposal touches (or, when it touches none, nothing at all).
        var touched = context.Days.Where(d => d.Capacity.Available.Any(a => a.Overlaps(proposal))).ToList();
        var remaining = touched.Sum(d => d.Capacity.RemainingConfirmedMinutes);
        var needed = Intervals.Minutes(wanted);
        if (touched.Count > 0 && needed > remaining)
            conflicts.Add(new Conflict(ConflictType.OverCapacity, overridable, proposal,
                $"Needs {Duration(needed)}, and only {Duration(remaining)} of capacity is left."));

        foreach (var skill in context.RequiredSkills.Where(s => !s.Held))
            conflicts.Add(new Conflict(ConflictType.SkillWarning, ConflictSeverity.Warning, proposal, $"Does not hold the skill \"{skill.Name}\"."));

        var blocked = conflicts.Any(c => c.Severity == ConflictSeverity.Block);
        var needsOverride = conflicts.Any(c => c.Severity == ConflictSeverity.Overridable);
        return new ConflictResult(!blocked && !needsOverride, !blocked && needsOverride, conflicts);
    }

    private static string Duration(int minutes)
        => minutes % 60 == 0 ? $"{minutes / 60}h" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
}
