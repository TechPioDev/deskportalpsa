using Desk.Application.Workforce;
using Desk.Domain.Common;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Everything that decides capacity for a set of people over a run of dates, read in a fixed number
/// of queries however many people or days are asked for (schedules, exceptions, planned work,
/// holidays - never one query per person or per day), then turned into the capacity engine's inputs
/// in memory.
///
/// Dates are SHIFT dates: the day a working window starts on, in the zone of the schedule in force
/// that day. One day either side of the asked-for range is always loaded too, because a night shift
/// that began the day before, or extra availability early the next morning, can change a day's
/// free time.
/// </summary>
public sealed class WorkforceCalendar
{
    private readonly Dictionary<Guid, List<WorkSchedule>> _versions;
    private readonly ILookup<Guid, CapacityException> _exceptions;
    private readonly ILookup<Guid, AllocatedSpan> _allocations;
    private readonly Dictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);

    public string OrganizationTimeZone { get; }
    public IReadOnlyDictionary<DateOnly, string> Holidays { get; }
    /// <summary>The first and last shift date loaded (the asked-for range and a day either side).</summary>
    public DateOnly First { get; }
    public DateOnly Last { get; }

    private WorkforceCalendar(
        string organizationTimeZone, DateOnly first, DateOnly last,
        Dictionary<Guid, List<WorkSchedule>> versions, ILookup<Guid, CapacityException> exceptions,
        ILookup<Guid, AllocatedSpan> allocations, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        OrganizationTimeZone = organizationTimeZone;
        First = first;
        Last = last;
        _versions = versions;
        _exceptions = exceptions;
        _allocations = allocations;
        Holidays = holidays;
    }

    /// <summary>Schedule versions for these people, oldest first - enough to know each person's zone and "today".</summary>
    public static async Task<Dictionary<Guid, List<WorkSchedule>>> VersionsAsync(
        DeskDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        // One query: breaks nest under days (they are not sibling collections), so the joined rows are
        // one per break at most - a handful per person.
        var rows = await db.WorkSchedules.AsNoTracking().Include(s => s.Days).ThenInclude(d => d.Breaks).AsSingleQuery()
            .Where(s => ids.Contains(s.AppUserId)).OrderBy(s => s.EffectiveFrom).ThenBy(s => s.Id).ToListAsync(ct);
        return rows.GroupBy(s => s.AppUserId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public static async Task<WorkforceCalendar> LoadAsync(
        DeskDbContext db, IWorkAllocationReader allocations, Guid callerId, string organizationTimeZone,
        IReadOnlyCollection<Guid> ids, DateOnly from, DateOnly to,
        Dictionary<Guid, List<WorkSchedule>>? versions, CancellationToken ct)
    {
        var first = from.AddDays(-1);
        var last = to.AddDays(1);
        versions ??= await VersionsAsync(db, ids, ct);

        // By date for both shapes: a part-day exception carries the dates it starts and ends on. One
        // more day either side covers a person whose zone puts those dates a day apart from the asker's.
        var lo = first.AddDays(-1);
        var hi = last.AddDays(1);
        var exceptions = await db.CapacityExceptions.AsNoTracking()
            .Where(e => ids.Contains(e.AppUserId) && e.FromDate <= hi && e.ToDate >= lo)
            .OrderBy(e => e.FromDate).ThenBy(e => e.Id).ToListAsync(ct);

        // Planned work, over real time wide enough for any zone's version of these dates.
        var spans = await allocations.ForAsync(callerId, ids,
            new DateTimeOffset(lo.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(hi.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ct);

        var holidays = await db.DeskHolidays.AsNoTracking().Where(h => h.Date >= first && h.Date <= last)
            .OrderBy(h => h.Date).ThenBy(h => h.Name).Select(h => new { h.Date, h.Name }).ToListAsync(ct);

        return new WorkforceCalendar(organizationTimeZone, first, last, versions,
            exceptions.ToLookup(e => e.AppUserId), spans.ToLookup(a => a.AppUserId),
            holidays.GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First().Name));
    }

    public bool HasSchedule(Guid appUserId) => _versions.TryGetValue(appUserId, out var v) && v.Count > 0;

    public IEnumerable<CapacityException> ExceptionsOf(Guid appUserId) => _exceptions[appUserId];

    /// <summary>The schedule version in force on a shift date: the latest one starting on or before it.</summary>
    public static WorkSchedule? InForce(IReadOnlyList<WorkSchedule>? versions, DateOnly date)
    {
        if (versions is null) return null;
        WorkSchedule? found = null;
        foreach (var v in versions)
        {
            if (v.EffectiveFrom > date) break;
            found = v;
        }
        return found;
    }

    /// <summary>The IANA zone id a person's dates are read in on a shift date: their schedule's, else the organization's.</summary>
    public string ZoneId(Guid appUserId, DateOnly date)
        => InForce(_versions.GetValueOrDefault(appUserId), date)?.TimeZone ?? OrganizationTimeZone;

    public TimeZoneInfo Zone(string zoneId)
    {
        if (!_zones.TryGetValue(zoneId, out var zone)) _zones[zoneId] = zone = TimeZones.Resolve(zoneId);
        return zone;
    }

    /// <summary>
    /// The engine's inputs for every loaded shift date of one person, in date order. Pure in-memory
    /// work on what <see cref="LoadAsync"/> read.
    /// </summary>
    public IReadOnlyList<DayCapacityInput> InputsFor(Guid appUserId)
    {
        var versions = _versions.GetValueOrDefault(appUserId);
        var exceptions = _exceptions[appUserId].ToList();
        var allocations = _allocations[appUserId].ToList();

        var dates = new List<DateOnly>();
        for (var d = First; d <= Last; d = d.AddDays(1)) dates.Add(d);

        // Each date's working window as real instants, and whether a full-day exception takes it off.
        var windows = new Dictionary<DateOnly, WorkingDayInstants?>(dates.Count);
        var allDay = new HashSet<DateOnly>();
        foreach (var d in dates)
        {
            var version = InForce(versions, d);
            var day = version?.Days.FirstOrDefault(x => x.Day == d.DayOfWeek);
            windows[d] = day is null ? null : WorkingWindow.Instants(d,
                new DayWindow(day.Day, day.Start, day.End, day.Breaks.Select(b => new BreakSpan(b.Start, b.End)).ToList()),
                Zone(version!.TimeZone));
            if (exceptions.Any(e => e.AllDay && e.Kind == CapacityExceptionKind.Unavailable && e.FromDate <= d && d <= e.ToDate))
                allDay.Add(d);
        }

        var unavailable = exceptions
            .Where(e => !e.AllDay && e.Kind == CapacityExceptionKind.Unavailable && e.StartsAt is not null && e.EndsAt is not null)
            .Select(e => new Interval(e.StartsAt!.Value, e.EndsAt!.Value)).ToList();

        // Extra availability belongs to the shift date it starts on (in that date's zone), and only
        // adds time outside every working window - so no minute is offered twice, on two dates, when
        // it runs into a night shift that began the day before. A window taken off by a full day
        // unavailable counts too: that time is unavailable, and offering it from a neighbouring
        // date's extra availability would contradict the conflict check, which blocks it.
        var liveWindows = dates.Where(d => windows[d] is not null)
            .Select(d => new Interval(windows[d]!.StartUtc, windows[d]!.EndUtc)).ToList();
        var additional = new Dictionary<DateOnly, List<Interval>>();
        foreach (var e in exceptions.Where(e => !e.AllDay && e.Kind == CapacityExceptionKind.AdditionalAvailability && e.StartsAt is not null && e.EndsAt is not null))
        {
            var owner = dates.Cast<DateOnly?>().FirstOrDefault(d => LocalDate(e.StartsAt!.Value, Zone(ZoneId(appUserId, d!.Value))) == d);
            if (owner is null) continue;
            if (!additional.TryGetValue(owner.Value, out var list)) additional[owner.Value] = list = [];
            list.AddRange(Intervals.Subtract([new Interval(e.StartsAt!.Value, e.EndsAt!.Value)], liveWindows));
        }

        return dates.Select(d =>
        {
            var w = windows[d];
            return new DayCapacityInput(d,
                w is null ? null : new Interval(w.StartUtc, w.EndUtc),
                w is null ? Intervals.None : w.BreaksUtc.Select(b => new Interval(b.StartUtc, b.EndUtc)).ToList(),
                allDay.Contains(d), unavailable,
                additional.TryGetValue(d, out var extra) ? extra : Intervals.None,
                allocations);
        }).ToList();
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
