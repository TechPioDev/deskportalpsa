using Desk.Domain.Common;
using Desk.Domain.Workforce;

namespace Desk.Tests.Unit;

/// <summary>One person's day, built the way the calendar builds it: wall-clock times in a zone, turned into instants.</summary>
internal sealed class CapacityDay(DateOnly date, TimeZoneInfo zone)
{
    private Interval? _window;
    private IReadOnlyList<Interval> _breaks = [];
    private bool _allDay;
    private readonly List<Interval> _unavailable = [];
    private readonly List<Interval> _additional = [];
    private readonly List<AllocatedSpan> _allocations = [];
    public static readonly Guid Person = Guid.NewGuid();

    // Real zone rules on every host: IANA where the host reads it (the Linux servers and CI), else
    // the same zone's Windows id (a Windows machine in invariant-globalization mode).
    public static TimeZoneInfo Zone(string iana, string windows)
        => TimeZones.HostKnowsIana ? TimeZoneInfo.FindSystemTimeZoneById(iana) : TimeZoneInfo.FindSystemTimeZoneById(windows);
    public static readonly TimeZoneInfo Kolkata = Zone("Asia/Kolkata", "India Standard Time");
    public static readonly TimeZoneInfo NewYork = Zone("America/New_York", "Eastern Standard Time");
    public static readonly TimeZoneInfo London = Zone("Europe/London", "GMT Standard Time");

    /// <summary>A stretch of wall-clock time starting on this date (or <paramref name="plusDays"/> later); an end at or before the start is the next day.</summary>
    public Interval At(string from, string to, int plusDays = 0)
    {
        var start = date.AddDays(plusDays).ToDateTime(TimeOnly.Parse(from));
        var end = date.AddDays(plusDays).ToDateTime(TimeOnly.Parse(to));
        if (end <= start) end = end.AddDays(1);
        return new Interval(TimeZones.WallToUtc(start, zone, true), TimeZones.WallToUtc(end, zone, false));
    }

    public CapacityDay Works(string start, string end, params (string From, string To)[] breaks)
    {
        var w = WorkingWindow.Instants(date,
            new DayWindow(date.DayOfWeek, TimeOnly.Parse(start), TimeOnly.Parse(end),
                breaks.Select(b => new BreakSpan(TimeOnly.Parse(b.From), TimeOnly.Parse(b.To))).ToList()), zone);
        _window = new Interval(w.StartUtc, w.EndUtc);
        _breaks = w.BreaksUtc.Select(b => new Interval(b.StartUtc, b.EndUtc)).ToList();
        return this;
    }

    public CapacityDay RawBreaks(params Interval[] breaks) { _breaks = breaks; return this; }
    public CapacityDay Unavailable(string from, string to, int plusDays = 0) { _unavailable.Add(At(from, to, plusDays)); return this; }
    public CapacityDay UnavailableAllDay() { _allDay = true; return this; }
    public CapacityDay Extra(string from, string to) { _additional.Add(At(from, to)); return this; }
    public CapacityDay Confirmed(string from, string to, int plusDays = 0, Guid? workId = null) { _allocations.Add(new AllocatedSpan(Person, At(from, to, plusDays), true, workId)); return this; }
    public CapacityDay Tentative(string from, string to, Guid? workId = null) { _allocations.Add(new AllocatedSpan(Person, At(from, to), false, workId)); return this; }

    public DayCapacityInput Input => new(date, _window, _breaks, _allDay, _unavailable, _additional, _allocations);
    public DayCapacity Capacity => CapacityCalculator.ForDay(Input);
}
