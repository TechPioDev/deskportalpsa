using Desk.Domain.Common;

namespace Desk.Domain.Workforce;

/// <summary>A break as wall-clock times, before it is checked.</summary>
public sealed record BreakSpan(TimeOnly Start, TimeOnly End);

/// <summary>One weekday's window and its breaks, before they are checked.</summary>
public sealed record DayWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End, IReadOnlyList<BreakSpan> Breaks);

/// <summary>One working day turned into real instants: the window, its breaks, and what is left to work.</summary>
public sealed record WorkingDayInstants(
    DateOnly ShiftDate, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> BreaksUtc, int UsableMinutes);

/// <summary>
/// The arithmetic of a working window, in one place so the schedule screen, the API and (later) the
/// capacity engine can never disagree about what "18:00 - 03:00 with a 30-minute break" means.
///
/// Two views of the same day. NOMINAL minutes are wall-clock arithmetic (9h window, 1h break, 8h to
/// work) - what a schedule screen shows. INSTANTS are the real moments in the person's time zone on a
/// given date, so a night that loses or gains an hour to a clock change has that much less or more.
/// </summary>
public static class WorkingWindow
{
    public const int MaxBreaksPerDay = 4;
    private const int MinutesPerDay = 24 * 60;

    /// <summary>The window runs past midnight: it ends at or before the time it starts.</summary>
    public static bool CrossesMidnight(TimeOnly start, TimeOnly end) => end <= start;

    /// <summary>Minutes from the window's start to <paramref name="t"/> on the window's own timeline.</summary>
    public static int Offset(TimeOnly windowStart, TimeOnly t)
    {
        var d = (int)(t.ToTimeSpan() - windowStart.ToTimeSpan()).TotalMinutes;
        return d < 0 ? d + MinutesPerDay : d;
    }

    /// <summary>Nominal length of the window. Never 0 for a valid window (start and end differ).</summary>
    public static int GrossMinutes(TimeOnly start, TimeOnly end)
    {
        var m = Offset(start, end);
        return m == 0 ? MinutesPerDay : m;
    }

    public static int BreakMinutes(DayWindow day) => day.Breaks.Sum(b => Offset(day.Start, b.End) - Offset(day.Start, b.Start));

    /// <summary>Nominal working time: the window less its breaks.</summary>
    public static int UsableMinutes(DayWindow day) => GrossMinutes(day.Start, day.End) - BreakMinutes(day);

    /// <summary>
    /// What is wrong with a day, in words an administrator can act on; empty when it is fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(DayWindow day)
    {
        var name = day.Day.ToString();
        var problems = new List<string>();
        if (day.Start == day.End)
        {
            problems.Add($"{name}: a working window can't start and end at the same time. Mark the day as not working instead.");
            return problems;
        }
        var gross = GrossMinutes(day.Start, day.End);
        if (day.Breaks.Count > MaxBreaksPerDay)
            problems.Add($"{name}: at most {MaxBreaksPerDay} breaks a day.");

        var spans = new List<(int From, int To, BreakSpan Break)>();
        foreach (var b in day.Breaks)
        {
            var label = $"{name}: the break {b.Start:HH\\:mm}–{b.End:HH\\:mm}";
            if (b.Start == b.End) { problems.Add($"{label} has no length."); continue; }
            var from = Offset(day.Start, b.Start);
            var to = Offset(day.Start, b.End);
            // A break must sit wholly inside the window, on the window's timeline: it can't start
            // before the window opens, end after it closes, or wrap around past the window's start.
            if (to <= from || to > gross || from >= gross)
            {
                problems.Add($"{label} is outside the working window {day.Start:HH\\:mm}–{day.End:HH\\:mm}.");
                continue;
            }
            spans.Add((from, to, b));
        }
        foreach (var pair in spans.OrderBy(s => s.From).Zip(spans.OrderBy(s => s.From).Skip(1)))
            if (pair.Second.From < pair.First.To)
                problems.Add($"{name}: the breaks {pair.First.Break.Start:HH\\:mm}–{pair.First.Break.End:HH\\:mm} and {pair.Second.Break.Start:HH\\:mm}–{pair.Second.Break.End:HH\\:mm} overlap.");

        if (problems.Count == 0 && gross - spans.Sum(s => s.To - s.From) <= 0)
            problems.Add($"{name}: the breaks leave no working time.");
        return problems;
    }

    /// <summary>
    /// The day's window as real instants, for a shift that starts on <paramref name="shiftDate"/> in
    /// <paramref name="zone"/>. An overnight window ends on the next calendar day but belongs to this
    /// one. Clock changes are handled by <see cref="TimeZones.WallToUtc"/>: a start that falls in a
    /// skipped hour moves forward by the gap; in a repeated hour a start takes the first occurrence and
    /// an end the second, so the window keeps its true elapsed length.
    ///
    /// A BREAK keeps its own length whatever the clocks do: it starts at its wall-clock time and
    /// lasts as long as it is set to. Reading its end off the clock instead would turn a 01:15-01:45
    /// break on a fall-back night into ninety minutes (01:45 comes round twice) and quietly take an
    /// hour of capacity with it.
    /// </summary>
    public static WorkingDayInstants Instants(DateOnly shiftDate, DayWindow day, TimeZoneInfo zone)
    {
        var windowStartLocal = shiftDate.ToDateTime(day.Start);
        var start = TimeZones.WallToUtc(windowStartLocal, zone, earlierIfAmbiguous: true);
        var end = TimeZones.WallToUtc(windowStartLocal.AddMinutes(GrossMinutes(day.Start, day.End)), zone, earlierIfAmbiguous: false);

        var breaks = day.Breaks
            .Select(b =>
            {
                var breakStart = TimeZones.WallToUtc(windowStartLocal.AddMinutes(Offset(day.Start, b.Start)), zone, earlierIfAmbiguous: true);
                var breakEnd = breakStart.AddMinutes(Offset(day.Start, b.End) - Offset(day.Start, b.Start));
                return (StartUtc: breakStart, EndUtc: breakEnd > end ? end : breakEnd);
            })
            .OrderBy(b => b.StartUtc)
            .ToList();

        var usable = (end - start).TotalMinutes - breaks.Sum(b => Math.Max(0, (b.EndUtc - b.StartUtc).TotalMinutes));
        return new WorkingDayInstants(shiftDate, start, end, breaks, (int)Math.Max(0, Math.Round(usable)));
    }
}
