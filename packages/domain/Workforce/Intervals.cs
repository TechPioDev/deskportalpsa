namespace Desk.Domain.Workforce;

/// <summary>
/// A stretch of real time, half-open: it includes its start and stops just before its end. Two
/// stretches that only touch (one ends at 10:00, the next starts at 10:00) therefore do not overlap -
/// work can follow work with no gap and no conflict.
/// </summary>
public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End)
{
    public bool IsEmpty => End <= Start;
    public TimeSpan Length => IsEmpty ? TimeSpan.Zero : End - Start;
    public bool Overlaps(Interval other) => Start < other.End && other.Start < End;
    public bool Contains(Interval other) => Start <= other.Start && other.End <= End;

    /// <summary>The part both share, or an empty interval when they do not overlap.</summary>
    public Interval Intersect(Interval other)
    {
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return end > start ? new Interval(start, end) : new Interval(start, start);
    }
}

/// <summary>
/// Set arithmetic over stretches of time - the one place capacity is added and taken away, so no
/// screen or report can count the same minute twice.
///
/// Every result is NORMALIZED: sorted, with overlapping stretches merged into one, and stretches that
/// touch merged too (10:00-11:00 and 11:00-12:00 are 10:00-12:00). Merging touching stretches is
/// what makes a free hour followed directly by another free hour one continuous two-hour slot, and
/// what stops two back-to-back unavailable periods being treated as separate things.
/// </summary>
public static class Intervals
{
    public static readonly IReadOnlyList<Interval> None = [];

    public static IReadOnlyList<Interval> Normalize(IEnumerable<Interval> intervals)
    {
        var sorted = intervals.Where(i => !i.IsEmpty).OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
        if (sorted.Count <= 1) return sorted;
        var merged = new List<Interval>(sorted.Count) { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            var next = sorted[i];
            // Overlapping or touching: one stretch.
            if (next.Start <= last.End)
            {
                if (next.End > last.End) merged[^1] = new Interval(last.Start, next.End);
            }
            else merged.Add(next);
        }
        return merged;
    }

    public static IReadOnlyList<Interval> Union(IEnumerable<Interval> a, IEnumerable<Interval> b) => Normalize(a.Concat(b));

    /// <summary>What is left of <paramref name="from"/> once <paramref name="remove"/> is taken out.</summary>
    public static IReadOnlyList<Interval> Subtract(IEnumerable<Interval> from, IEnumerable<Interval> remove)
    {
        var keep = Normalize(from);
        var cut = Normalize(remove);
        if (keep.Count == 0 || cut.Count == 0) return keep;

        var result = new List<Interval>(keep.Count + cut.Count);
        var c = 0;
        foreach (var k in keep)
        {
            var cursor = k.Start;
            // Cuts that end before this stretch starts can never matter again (both lists are sorted).
            while (c < cut.Count && cut[c].End <= k.Start) c++;
            for (var j = c; j < cut.Count && cut[j].Start < k.End; j++)
            {
                if (cut[j].Start > cursor) result.Add(new Interval(cursor, cut[j].Start));
                if (cut[j].End > cursor) cursor = cut[j].End;
                if (cursor >= k.End) break;
            }
            if (cursor < k.End) result.Add(new Interval(cursor, k.End));
        }
        return result;
    }

    /// <summary>The time present in both.</summary>
    public static IReadOnlyList<Interval> Intersect(IEnumerable<Interval> a, IEnumerable<Interval> b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        var result = new List<Interval>();
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count)
        {
            var both = left[i].Intersect(right[j]);
            if (!both.IsEmpty) result.Add(both);
            if (left[i].End < right[j].End) i++; else j++;
        }
        return result;
    }

    public static IReadOnlyList<Interval> Clip(IEnumerable<Interval> intervals, Interval window) => Intersect(intervals, [window]);

    /// <summary>
    /// Total length in whole minutes. Call it on a normalized set (every method here returns one), or
    /// overlapping stretches would be counted more than once.
    /// </summary>
    public static int Minutes(IEnumerable<Interval> intervals)
        => (int)Math.Round(intervals.Sum(i => i.Length.TotalMinutes), MidpointRounding.AwayFromZero);
}
