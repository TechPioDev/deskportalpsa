using System.Globalization;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Capacity, free time and conflicts for the people the caller may see. Internal only: nothing here
/// is reachable by, or returned to, a client account.
///
/// Every answer is worked out from what is in the database at that moment - nothing is cached, so a
/// changed schedule, exception or booking shows at once, and a free slot shown earlier is never
/// treated as a reservation.
/// </summary>
public sealed class CapacityService(DeskDbContext db, WorkforceAccess access, IWorkAllocationReader allocations, TimeProvider clock)
    : ICapacityService
{
    /// <summary>The most dates one capacity request may cover.</summary>
    public const int MaxRangeDays = 31;
    /// <summary>The most dates one availability search may cover.</summary>
    public const int MaxSearchDays = 14;
    /// <summary>The most people one team view or search will work through; beyond it, narrow by team.</summary>
    public const int MaxPeople = 1000;
    public const int MinDurationMinutes = 5;
    public const int MaxDurationMinutes = 12 * 60;
    private const int MaxMatches = 200;

    public async Task<PersonCapacityDto> ForPersonAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, appUserId, ct);
        var orgZone = access.OrganizationTimeZone();
        var versions = await WorkforceCalendar.VersionsAsync(db, [person.Id], ct);
        var today = Today(versions.GetValueOrDefault(person.Id), orgZone);

        var start = from ?? today;
        var end = to ?? start;
        CheckRange(start, end, today, MaxRangeDays);

        var calendar = await WorkforceCalendar.LoadAsync(db, allocations, callerId, orgZone, [person.Id], start, end, versions, ct);
        var days = Days(calendar, person.Id).Where(d => d.Date >= start && d.Date <= end).ToList();
        var names = await NamesAsync(calendar.ExceptionsOf(person.Id), ct);

        return new PersonCapacityDto(person.Id, person.DisplayName, person.IsActive, person.IsSchedulable,
            calendar.HasSchedule(person.Id), calendar.ZoneId(person.Id, today), today,
            days.Select(d => DayDto(calendar, person.Id, d, names)).ToList(),
            await access.CanManageAvailabilityAsync(callerId, person.Id, ct));
    }

    public async Task<TeamCapacityDto> ForTeamAsync(Guid callerId, TeamCapacityQuery query, CancellationToken ct = default)
    {
        var orgZone = access.OrganizationTimeZone();
        var today = WorkforceCalendar.LocalDate(clock.GetUtcNow(), TimeZones.Resolve(orgZone));
        var date = query.Date ?? today;
        CheckRange(date, date, today, MaxRangeDays);

        var people = await PeopleAsync(callerId, query.TeamId, query.DepartmentId, await KnownSkillsAsync(query.SkillIds, ct), query.MatchAllSkills, null, ct);
        var ids = people.Select(p => p.Id).ToList();
        var calendar = await WorkforceCalendar.LoadAsync(db, allocations, callerId, orgZone, ids, date, date, null, ct);
        var teams = await TeamsOfAsync(ids, ct);
        var skills = await SkillsOfAsync(ids, ct);
        var names = await NamesAsync(ids.SelectMany(calendar.ExceptionsOf), ct);

        var rows = people.Select(p =>
        {
            var day = Days(calendar, p.Id).Single(d => d.Date == date);
            return new TeamCapacityRowDto(p.Id, p.DisplayName, p.IsSchedulable, calendar.HasSchedule(p.Id),
                teams.GetValueOrDefault(p.Id) ?? [], skills.GetValueOrDefault(p.Id) ?? [], DayDto(calendar, p.Id, day, names));
        }).ToList();

        // Totals are the capacity actually on offer: someone not offered for planned work is listed,
        // so nobody wonders where they went, but adds nothing.
        var offered = rows.Where(r => r.IsSchedulable).Select(r => r.Day).ToList();
        return new TeamCapacityDto(date, rows, offered.Sum(d => d.UsableMinutes), offered.Sum(d => d.ConfirmedMinutes),
            offered.Sum(d => d.TentativeMinutes), offered.Sum(d => d.RemainingConfirmedMinutes));
    }

    public async Task<AvailabilitySearchResultDto> FindAsync(Guid callerId, AvailabilitySearch search, CancellationToken ct = default)
    {
        var orgZone = access.OrganizationTimeZone();
        var problems = new List<string>();
        var zoneId = string.IsNullOrWhiteSpace(search.TimeZone) ? orgZone : TimeZones.ToIana(search.TimeZone);
        if (zoneId is null) problems.Add($"\"{search.TimeZone}\" is not a time zone this system knows.");
        var zone = TimeZones.Resolve(zoneId ?? orgZone);
        var now = clock.GetUtcNow();
        var today = WorkforceCalendar.LocalDate(now, zone);

        if (search.DurationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            problems.Add($"The work must take between {MinDurationMinutes} minutes and {MaxDurationMinutes / 60} hours.");
        var from = search.From;
        var to = search.To ?? from;
        if (to < from) problems.Add("The last date is before the first.");
        else if (to.DayNumber - from.DayNumber >= MaxSearchDays) problems.Add($"Search at most {MaxSearchDays} days at a time.");
        if (to < today) problems.Add("Those dates have passed. Search from today.");
        if (from > today.AddYears(1)) problems.Add("Search at most a year ahead.");
        var earliest = ParseTime(search.EarliestTime, "The earliest time", TimeOnly.MinValue, problems);
        var latest = ParseTime(search.LatestTime, "The latest time", TimeOnly.MinValue, problems);
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));

        var wanted = await KnownSkillsAsync(search.SkillIds, ct);
        // Skills are checked in memory rather than in the query, so the answer can say how many
        // people were left out for want of them.
        var people = await PeopleAsync(callerId, search.TeamId, search.DepartmentId, [], true, search.AppUserIds, ct);
        var ids = people.Select(p => p.Id).ToList();
        var skills = await SkillsOfAsync(ids, ct);

        var withoutSkills = 0;
        var notOffered = 0;
        var candidates = new List<(Guid Id, string DisplayName, bool IsSchedulable)>();
        foreach (var p in people)
        {
            var held = (skills.GetValueOrDefault(p.Id) ?? []).Select(s => s.SkillId).ToHashSet();
            var hasSkills = wanted.Count == 0 || (search.MatchAllSkills ? wanted.All(held.Contains) : wanted.Any(held.Contains));
            if (!hasSkills) withoutSkills++;
            else if (!p.IsSchedulable) notOffered++;
            else candidates.Add(p);
        }

        var candidateIds = candidates.Select(c => c.Id).ToList();
        var calendar = await WorkforceCalendar.LoadAsync(db, allocations, callerId, orgZone, candidateIds, from, to, null, ct);
        var teams = await TeamsOfAsync(candidateIds, ct);

        // Nothing is offered in the past: a search for today starts from the next whole minute.
        var notBefore = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(now.Ticks % TimeSpan.TicksPerMinute == 0 ? 0 : 1);
        var windows = new List<(DateOnly Date, Interval Window)>();
        for (var d = from < today ? today : from; d <= to; d = d.AddDays(1))
        {
            var start = TimeZones.WallToUtc(d.ToDateTime(earliest), zone, earlierIfAmbiguous: true);
            // A latest time at or before the earliest (or none at all) runs to that time the next day.
            var endDate = latest > earliest ? d : d.AddDays(1);
            var end = TimeZones.WallToUtc(endDate.ToDateTime(latest), zone, earlierIfAmbiguous: false);
            if (start < notBefore) start = notBefore;
            if (end > start) windows.Add((d, new Interval(start, end)));
        }

        var matches = new List<AvailabilityMatchDto>();
        var withoutSchedule = 0;
        var noSlot = 0;
        foreach (var c in candidates)
        {
            // Free time across the loaded dates as one set: a night shift's free hours after midnight
            // belong to the day before, but they are still free at 01:00 on the date being searched.
            var free = Intervals.Normalize(Days(calendar, c.Id).SelectMany(d => d.FreeSlots));
            AvailabilityMatchDto? match = null;
            foreach (var (date, window) in windows)
            {
                var fitting = CapacityCalculator.Fitting(free, search.DurationMinutes, window);
                if (fitting.Count == 0) continue;
                var recommended = new Interval(fitting[0].Start, fitting[0].Start.AddMinutes(search.DurationMinutes));
                match = new AvailabilityMatchDto(c.Id, c.DisplayName, calendar.ZoneId(c.Id, date), teams.GetValueOrDefault(c.Id) ?? [],
                    (skills.GetValueOrDefault(c.Id) ?? []).Where(s => wanted.Contains(s.SkillId)).ToList(),
                    date, Slot(recommended), fitting.Select(Slot).ToList(), Intervals.Minutes(Intervals.Clip(free, window)));
                break;
            }
            if (match is not null) matches.Add(match);
            else if (!calendar.HasSchedule(c.Id) && free.Count == 0) withoutSchedule++;
            else noSlot++;
        }

        // Earliest first; then whoever has more free time in the window; then by name. Facts only -
        // no score and no judgement about people.
        var ordered = matches.OrderBy(m => m.Recommended.Start).ThenByDescending(m => m.FreeMinutes)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).Take(MaxMatches).ToList();
        return new AvailabilitySearchResultDto(zoneId!, search.DurationMinutes, search.MatchAllSkills, ordered,
            people.Count, withoutSkills, notOffered, withoutSchedule, noSlot);
    }

    public async Task<ConflictResultDto> EvaluateAsync(Guid callerId, Guid appUserId, ProposedWork proposal, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, appUserId, ct);
        var now = clock.GetUtcNow();
        var start = proposal.Start.ToUniversalTime();
        var end = proposal.End.ToUniversalTime();
        if (end <= start) throw new ValidationFailedException("The work must end after it starts.");
        if (end - start > TimeSpan.FromHours(24)) throw new ValidationFailedException("One piece of work can be at most 24 hours long.");
        if (start < now.AddDays(-MaxRangeDays) || start > now.AddYears(1))
            throw new ValidationFailedException("Choose a time between a month ago and a year ahead.");

        var wanted = await KnownSkillsAsync(proposal.SkillIds, ct);
        var orgZone = access.OrganizationTimeZone();
        // UTC dates either side of the proposal; the calendar loads a further day each way, which
        // covers every zone's idea of the dates the proposal touches.
        var calendar = await WorkforceCalendar.LoadAsync(db, allocations, callerId, orgZone, [person.Id],
            DateOnly.FromDateTime(start.UtcDateTime).AddDays(-1), DateOnly.FromDateTime(end.UtcDateTime).AddDays(1), null, ct);

        var inputs = calendar.InputsFor(person.Id);
        var held = (await SkillsOfAsync([person.Id], ct)).GetValueOrDefault(person.Id) ?? [];
        var names = wanted.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Skills.AsNoTracking().Where(s => wanted.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var result = ConflictEvaluator.Evaluate(new Interval(start, end), proposal.Tentative, new ConflictContext(
            person.IsActive, person.IsSchedulable,
            inputs.Select(i => (i, CapacityCalculator.ForDay(i))).ToList(),
            wanted.Select(id => new RequiredSkill(id, names[id], held.Any(h => h.SkillId == id))).ToList()));

        return new ConflictResultDto(result.CanSchedule, result.CanOverride,
            result.Conflicts.Select(c => new ConflictDto(c.Type, c.Severity, c.When.Start, c.When.End, c.Message, c.BlockingWorkId)).ToList());
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private static IReadOnlyList<DayCapacity> Days(WorkforceCalendar calendar, Guid appUserId)
        => calendar.InputsFor(appUserId).Select(CapacityCalculator.ForDay).ToList();

    private DayCapacityDto DayDto(WorkforceCalendar calendar, Guid appUserId, DayCapacity day, IReadOnlyDictionary<Guid, string> names)
    {
        var zoneId = calendar.ZoneId(appUserId, day.Date);
        // The exceptions that touch this day: those covering its date, and any that reach into its
        // working window from another date (an appointment at 01:00 inside a night shift).
        var touching = calendar.ExceptionsOf(appUserId).Where(e =>
            (e.FromDate <= day.Date && day.Date <= e.ToDate)
            || (!e.AllDay && day.Window is { } w && e.StartsAt is not null && e.EndsAt is not null && new Interval(e.StartsAt.Value, e.EndsAt.Value).Overlaps(w)));
        return new DayCapacityDto(day.Date, zoneId, day.Window is not null,
            day.Window?.Start, day.Window?.End,
            day.GrossMinutes, day.BreakMinutes, day.UnavailableMinutes, day.AdditionalMinutes, day.UsableMinutes,
            day.ConfirmedMinutes, day.TentativeMinutes, day.RemainingConfirmedMinutes, day.ProjectedRemainingMinutes,
            day.UnavailableAllDay,
            day.Breaks.Select(Slot).ToList(), day.FreeSlots.Select(Slot).ToList(), day.ProjectedFreeSlots.Select(Slot).ToList(),
            touching.Select(e => CapacityExceptionService.Dto(e, calendar.Zone(e.TimeZone), names)).ToList(),
            calendar.Holidays.GetValueOrDefault(day.Date));
    }

    private static SlotDto Slot(Interval i) => new(i.Start, i.End, (int)Math.Round(i.Length.TotalMinutes, MidpointRounding.AwayFromZero));

    /// <summary>Active staff the caller may see, narrowed as asked; refuses a set too large to work through.</summary>
    private async Task<List<(Guid Id, string DisplayName, bool IsSchedulable)>> PeopleAsync(
        Guid callerId, Guid? teamId, Guid? departmentId, IReadOnlyList<Guid> skillIds, bool matchAllSkills, IReadOnlyList<Guid>? only, CancellationToken ct)
    {
        var staff = (await access.VisibleStaffAsync(callerId, ct)).Where(u => u.IsActive);
        staff = access.Narrow(staff, teamId, departmentId, skillIds, matchAllSkills);
        if (only is { Count: > 0 })
        {
            var pick = only.Distinct().Take(MaxPeople).ToList();
            staff = staff.Where(u => pick.Contains(u.Id));
        }
        var rows = await staff.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(MaxPeople + 1)
            .Select(u => new { u.Id, u.DisplayName, u.IsSchedulable }).ToListAsync(ct);
        if (rows.Count > MaxPeople)
            throw new ValidationFailedException($"That is more than {MaxPeople} people. Choose a team or a department to narrow it down.");
        return rows.Select(r => (r.Id, r.DisplayName, r.IsSchedulable)).ToList();
    }

    /// <summary>
    /// The requested skills that exist in this organization. One that does not exist and one that
    /// belongs to someone else's organization are refused with the same words.
    /// </summary>
    private async Task<List<Guid>> KnownSkillsAsync(IReadOnlyList<Guid>? skillIds, CancellationToken ct)
    {
        if (skillIds is not { Count: > 0 }) return [];
        var wanted = skillIds.Distinct().ToList();
        if (wanted.Count > 20) throw new ValidationFailedException("Ask for at most 20 skills at a time.");
        var known = await db.Skills.AsNoTracking().Where(s => wanted.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        if (known.Count != wanted.Count) throw new ValidationFailedException("One of the skills asked for is not in the skill catalogue.");
        return wanted;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> TeamsOfAsync(List<Guid> ids, CancellationToken ct)
    {
        var rows = await db.UserTeams.AsNoTracking().Where(m => ids.Contains(m.AppUserId))
            .Select(m => new { m.AppUserId, m.Team!.Name }).ToListAsync(ct);
        return rows.GroupBy(r => r.AppUserId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<string>)g.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<Dictionary<Guid, IReadOnlyList<StaffSkillDto>>> SkillsOfAsync(List<Guid> ids, CancellationToken ct)
    {
        var rows = await db.StaffSkills.AsNoTracking().Where(s => ids.Contains(s.AppUserId))
            .Select(s => new { s.AppUserId, s.SkillId, s.Skill!.Name, s.Level, s.Skill.IsActive }).ToListAsync(ct);
        return rows.GroupBy(r => r.AppUserId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<StaffSkillDto>)g.OrderBy(r => r.Name).Select(r => new StaffSkillDto(r.SkillId, r.Name, r.Level, r.IsActive)).ToList());
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<CapacityException> exceptions, CancellationToken ct)
    {
        var ids = exceptions.Select(e => e.UpdatedByUserId ?? e.CreatedByUserId).Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>()
            : await db.AppUsers.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>Today for a person: in the zone of their schedule in force now, else the organization's.</summary>
    private DateOnly Today(IReadOnlyList<WorkSchedule>? versions, string organizationTimeZone)
    {
        var now = clock.GetUtcNow();
        // The latest version already in force in its own zone decides; a version that has not
        // started yet has no say in what day it is.
        var zoneId = versions?.LastOrDefault(v => v.EffectiveFrom <= WorkforceCalendar.LocalDate(now, TimeZones.Resolve(v.TimeZone)))?.TimeZone
                     ?? organizationTimeZone;
        return WorkforceCalendar.LocalDate(now, TimeZones.Resolve(zoneId));
    }

    private static void CheckRange(DateOnly from, DateOnly to, DateOnly today, int maxDays)
    {
        if (to < from) throw new ValidationFailedException("The last date is before the first.");
        if (to.DayNumber - from.DayNumber >= maxDays) throw new ValidationFailedException($"Ask for at most {maxDays} days at a time.");
        if (from < today.AddYears(-1) || to > today.AddYears(1).AddDays(maxDays))
            throw new ValidationFailedException("Choose dates within a year of today.");
    }

    private static TimeOnly ParseTime(string? value, string what, TimeOnly fallback, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        problems.Add($"{what} \"{value}\" is not a time like 13:00.");
        return fallback;
    }
}

/// <summary>
/// No planned work yet. Booking work into people's time arrives with work allocation; until then
/// nothing is allocated, and capacity is the schedule less breaks and exceptions. The engine is
/// written and tested against real allocations, so only this reader changes when booking arrives.
/// </summary>
public sealed class NoWorkAllocations : IWorkAllocationReader
{
    public Task<IReadOnlyList<AllocatedSpan>> ForAsync(
        Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AllocatedSpan>>([]);
}
