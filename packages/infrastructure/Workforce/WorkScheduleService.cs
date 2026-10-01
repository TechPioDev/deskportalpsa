using System.Globalization;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Working schedules: when each person normally works, as a capacity boundary for planning work.
/// Versioned by the day a change starts, so earlier days keep the schedule they had.
/// </summary>
public sealed class WorkScheduleService(DeskDbContext db, WorkforceAccess access, IAuditWriter audit, TimeProvider clock)
    : IWorkScheduleService
{
    private const int MaxCopyTargets = 200;

    public async Task<PersonScheduleDto> GetAsync(Guid callerId, Guid appUserId, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, appUserId, ct);
        return await DtoAsync(person, await access.CanManageAsync(callerId, ct), ct);
    }

    public async Task<PersonScheduleDto> SaveAsync(Guid callerId, Guid appUserId, WorkScheduleInput input, CancellationToken ct = default)
    {
        var person = await access.ManagedPersonAsync(callerId, appUserId, ct);
        var (zoneId, days) = Validate(input);
        var effectiveFrom = EffectiveFrom(input.EffectiveFrom, zoneId);

        var before = await InForceAsync(appUserId, effectiveFrom, ct);
        var existing = await db.WorkSchedules.Include(s => s.Days).ThenInclude(d => d.Breaks)
            .FirstOrDefaultAsync(s => s.AppUserId == appUserId && s.EffectiveFrom == effectiveFrom, ct);
        if (existing is not null)
        {
            // Same starting day: this version is being corrected, not a new one added.
            db.WorkScheduleDays.RemoveRange(existing.Days);
            existing.TimeZone = zoneId;
            existing.UpdatedByUserId = callerId;
            AddDays(existing, days);
        }
        else
        {
            var schedule = new WorkSchedule
            {
                MspOrganizationId = person.MspOrganizationId ?? Guid.Empty, AppUserId = appUserId,
                EffectiveFrom = effectiveFrom, TimeZone = zoneId, UpdatedByUserId = callerId,
            };
            AddDays(schedule, days);
            db.WorkSchedules.Add(schedule);
        }
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("workforce.schedule.saved", "AppUser", appUserId.ToString(), new
        {
            person = person.DisplayName, effectiveFrom = effectiveFrom.ToString("yyyy-MM-dd"), timeZone = zoneId,
            before = before is null ? null : Describe(before.TimeZone, Windows(before)),
            after = Describe(zoneId, days), correctedVersion = existing is not null,
        }, ct);
        return await DtoAsync(person, true, ct);
    }

    public async Task<PersonScheduleDto> RemoveUpcomingAsync(Guid callerId, Guid appUserId, DateOnly effectiveFrom, CancellationToken ct = default)
    {
        var person = await access.ManagedPersonAsync(callerId, appUserId, ct);
        var version = await db.WorkSchedules.Include(s => s.Days).ThenInclude(d => d.Breaks)
            .FirstOrDefaultAsync(s => s.AppUserId == appUserId && s.EffectiveFrom == effectiveFrom, ct)
            ?? throw new NotFoundException("Schedule");
        // Only a change that has not started yet can be withdrawn: a day already worked under a
        // schedule keeps it.
        if (effectiveFrom <= Today(version.TimeZone))
            throw new ValidationFailedException("Only a schedule that has not started yet can be removed. Save a new one from today instead.");
        db.WorkSchedules.Remove(version);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.schedule.removed", "AppUser", appUserId.ToString(), new
        {
            person = person.DisplayName, effectiveFrom = effectiveFrom.ToString("yyyy-MM-dd"),
            removed = Describe(version.TimeZone, Windows(version)),
        }, ct);
        return await DtoAsync(person, true, ct);
    }

    public async Task<int> CopyAsync(Guid callerId, Guid fromUserId, IReadOnlyList<Guid> toUserIds, DateOnly? effectiveFrom, CancellationToken ct = default)
    {
        var source = await access.ManagedPersonAsync(callerId, fromUserId, ct);
        var targets = toUserIds.Where(id => id != fromUserId).Distinct().ToList();
        if (targets.Count == 0) throw new ValidationFailedException("Choose at least one other person to copy the schedule to.");
        if (targets.Count > MaxCopyTargets) throw new ValidationFailedException($"Copy to at most {MaxCopyTargets} people at a time.");

        // The schedule in force today, else the first one set to start.
        var versions = await db.WorkSchedules.AsNoTracking().Include(s => s.Days).ThenInclude(d => d.Breaks)
            .Where(s => s.AppUserId == fromUserId).OrderBy(s => s.EffectiveFrom).ToListAsync(ct);
        var template = InForce(versions) ?? versions.FirstOrDefault()
            ?? throw new ValidationFailedException($"{source.DisplayName} has no schedule to copy yet.");
        var days = Windows(template);
        var input = new WorkScheduleInput(effectiveFrom, template.TimeZone,
            days.Select(d => new WorkDayInput(d.Day, Hm(d.Start), Hm(d.End), d.Breaks.Select(b => new WorkBreakDto(Hm(b.Start), Hm(b.End))).ToList())).ToList());

        var copied = 0;
        foreach (var target in targets)
        {
            // Each target is checked on its own: a person from another organization is "not found"
            // and stops the copy before anything is written for them.
            await SaveAsync(callerId, target, input, ct);
            copied++;
        }
        await audit.WriteAsync("workforce.schedule.copied", "AppUser", fromUserId.ToString(),
            new { from = source.DisplayName, to = targets.Count, schedule = Describe(template.TimeZone, days) }, ct);
        return copied;
    }

    public async Task<PersonScheduleDto> SetSchedulableAsync(Guid callerId, Guid appUserId, bool schedulable, CancellationToken ct = default)
    {
        var person = await access.ManagedPersonAsync(callerId, appUserId, ct);
        if (person.IsSchedulable != schedulable)
        {
            person.IsSchedulable = schedulable;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("workforce.schedulable.changed", "AppUser", appUserId.ToString(),
                new { person = person.DisplayName, from = !schedulable, to = schedulable }, ct);
        }
        return await DtoAsync(person, true, ct);
    }

    public async Task<IReadOnlyList<WorkforcePersonDto>> PeopleAsync(Guid callerId, WorkforceQuery query, CancellationToken ct = default)
    {
        var staff = await access.VisibleStaffAsync(callerId, ct);
        if (!query.IncludeInactive) staff = staff.Where(u => u.IsActive);
        if (query.TeamId is { } team) staff = staff.Where(u => db.UserTeams.Any(m => m.AppUserId == u.Id && m.TeamId == team));
        if (query.DepartmentId is { } dept) staff = staff.Where(u => db.UserDepartments.Any(m => m.AppUserId == u.Id && m.DepartmentId == dept));
        if (query.SkillIds is { Count: > 0 } skillIds)
        {
            var wanted = skillIds.Distinct().ToList();
            staff = query.MatchAllSkills
                ? staff.Where(u => db.StaffSkills.Count(s => s.AppUserId == u.Id && wanted.Contains(s.SkillId)) == wanted.Count)
                : staff.Where(u => db.StaffSkills.Any(s => s.AppUserId == u.Id && wanted.Contains(s.SkillId)));
        }
        var people = await staff.AsNoTracking().OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive, u.IsSchedulable }).ToListAsync(ct);
        var ids = people.Select(p => p.Id).ToList();

        var teams = await db.UserTeams.AsNoTracking().Where(m => ids.Contains(m.AppUserId))
            .Select(m => new { m.AppUserId, m.Team!.Name }).ToListAsync(ct);
        var departments = await db.UserDepartments.AsNoTracking().Where(m => ids.Contains(m.AppUserId))
            .Select(m => new { m.AppUserId, m.Department!.Name }).ToListAsync(ct);
        var skills = await SkillsOfAsync(ids, ct);
        var versions = await db.WorkSchedules.AsNoTracking().Include(s => s.Days).ThenInclude(d => d.Breaks)
            .Where(s => ids.Contains(s.AppUserId)).ToListAsync(ct);

        return people.Select(p =>
        {
            var current = InForce(versions.Where(v => v.AppUserId == p.Id));
            var windows = current is null ? null : Windows(current);
            return new WorkforcePersonDto(p.Id, p.DisplayName, p.Email, p.IsActive, p.IsSchedulable,
                teams.Where(t => t.AppUserId == p.Id).Select(t => t.Name).OrderBy(n => n).ToList(),
                departments.Where(d => d.AppUserId == p.Id).Select(d => d.Name).OrderBy(n => n).ToList(),
                current?.TimeZone, windows?.Sum(WorkingWindow.UsableMinutes),
                windows is null ? null : Describe(null, windows),
                skills.GetValueOrDefault(p.Id) ?? []);
        }).ToList();
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private async Task<PersonScheduleDto> DtoAsync(AppUser person, bool canManage, CancellationToken ct)
    {
        var versions = await db.WorkSchedules.AsNoTracking().Include(s => s.Days).ThenInclude(d => d.Breaks)
            .Where(s => s.AppUserId == person.Id).OrderBy(s => s.EffectiveFrom).ToListAsync(ct);
        var current = InForce(versions);
        var upcoming = versions.Where(v => v.EffectiveFrom > Today(v.TimeZone)).OrderBy(v => v.EffectiveFrom).FirstOrDefault();
        var names = await UserNamesAsync(versions.Select(v => v.UpdatedByUserId), ct);
        return new PersonScheduleDto(person.Id, person.DisplayName, person.IsActive, person.IsSchedulable,
            access.OrganizationTimeZone(), current is null ? null : VersionDto(current, names),
            upcoming is null ? null : VersionDto(upcoming, names), versions.Select(v => v.EffectiveFrom).ToList(), canManage);
    }

    private static WorkScheduleVersionDto VersionDto(WorkSchedule s, IReadOnlyDictionary<Guid, string> names)
    {
        var windows = Windows(s);
        return new WorkScheduleVersionDto(s.EffectiveFrom, s.TimeZone,
            windows.Select(d => new WorkDayDto(d.Day, Hm(d.Start), Hm(d.End),
                d.Breaks.Select(b => new WorkBreakDto(Hm(b.Start), Hm(b.End))).ToList(),
                WorkingWindow.CrossesMidnight(d.Start, d.End), WorkingWindow.GrossMinutes(d.Start, d.End),
                WorkingWindow.BreakMinutes(d), WorkingWindow.UsableMinutes(d))).ToList(),
            windows.Sum(WorkingWindow.UsableMinutes), s.UpdatedAt,
            s.UpdatedByUserId is { } by ? names.GetValueOrDefault(by) : null);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<StaffSkillDto>>> SkillsOfAsync(List<Guid> ids, CancellationToken ct)
    {
        var rows = await db.StaffSkills.AsNoTracking().Where(s => ids.Contains(s.AppUserId))
            .Select(s => new { s.AppUserId, s.SkillId, s.Skill!.Name, s.Level, s.Skill.IsActive }).ToListAsync(ct);
        return rows.GroupBy(r => r.AppUserId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<StaffSkillDto>)g.OrderBy(r => r.Name).Select(r => new StaffSkillDto(r.SkillId, r.Name, r.Level, r.IsActive)).ToList());
    }

    private async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, string>()
            : await db.AppUsers.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private async Task<WorkSchedule?> InForceAsync(Guid appUserId, DateOnly on, CancellationToken ct)
        => await db.WorkSchedules.AsNoTracking().Include(s => s.Days).ThenInclude(d => d.Breaks)
            .Where(s => s.AppUserId == appUserId && s.EffectiveFrom <= on)
            .OrderByDescending(s => s.EffectiveFrom).FirstOrDefaultAsync(ct);

    /// <summary>The version in force today, each version judged in its own time zone.</summary>
    private WorkSchedule? InForce(IEnumerable<WorkSchedule> versions)
        => versions.Where(v => v.EffectiveFrom <= Today(v.TimeZone)).OrderByDescending(v => v.EffectiveFrom).FirstOrDefault();

    private DateOnly Today(string zoneId)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZones.Resolve(zoneId)).DateTime);

    private DateOnly EffectiveFrom(DateOnly? requested, string zoneId)
    {
        var today = Today(zoneId);
        var from = requested ?? today;
        if (from < today)
            throw new ValidationFailedException($"A schedule can start today ({today:d MMM yyyy}) or later. Days already past keep the schedule they had.");
        if (from > today.AddYears(1))
            throw new ValidationFailedException("A schedule can start at most a year ahead.");
        return from;
    }

    private static void AddDays(WorkSchedule schedule, IReadOnlyList<DayWindow> days)
    {
        foreach (var d in days)
        {
            var day = new WorkScheduleDay { MspOrganizationId = schedule.MspOrganizationId, Day = d.Day, Start = d.Start, End = d.End };
            foreach (var b in d.Breaks.OrderBy(b => WorkingWindow.Offset(d.Start, b.Start)))
                day.Breaks.Add(new WorkScheduleBreak { MspOrganizationId = schedule.MspOrganizationId, Start = b.Start, End = b.End });
            schedule.Days.Add(day);
        }
    }

    /// <summary>Checks a submitted schedule completely and reports every problem at once.</summary>
    public static (string ZoneId, IReadOnlyList<DayWindow> Days) Validate(WorkScheduleInput input)
    {
        var problems = new List<string>();
        var zoneId = TimeZones.ToIana(input.TimeZone);
        if (zoneId is null) problems.Add($"\"{input.TimeZone}\" is not a time zone this system knows. Choose one from the list.");

        var days = new List<DayWindow>();
        if (input.Days is null || input.Days.Count == 0)
            problems.Add("Choose at least one working day. To keep someone out of planned work, turn off \"Offered for planned work\" instead.");
        else
        {
            foreach (var dup in input.Days.GroupBy(d => d.Day).Where(g => g.Count() > 1))
                problems.Add($"{dup.Key} appears more than once.");
            foreach (var d in input.Days)
            {
                if (!Enum.IsDefined(d.Day)) { problems.Add("A working day is not a day of the week."); continue; }
                var start = ParseTime(d.Start, $"{d.Day} start", problems);
                var end = ParseTime(d.End, $"{d.Day} end", problems);
                var breaks = new List<BreakSpan>();
                foreach (var b in d.Breaks ?? [])
                {
                    var bs = ParseTime(b.Start, $"{d.Day} break start", problems);
                    var be = ParseTime(b.End, $"{d.Day} break end", problems);
                    if (bs is not null && be is not null) breaks.Add(new BreakSpan(bs.Value, be.Value));
                }
                if (start is null || end is null) continue;
                var window = new DayWindow(d.Day, start.Value, end.Value, breaks);
                problems.AddRange(WorkingWindow.Problems(window));
                days.Add(window);
            }
        }
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems.Distinct()));
        return (zoneId!, days.OrderBy(d => ((int)d.Day + 6) % 7).ToList());
    }

    private static TimeOnly? ParseTime(string? value, string what, List<string> problems)
    {
        if (TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        problems.Add($"{what} \"{value}\" is not a time like 08:30.");
        return null;
    }

    private static IReadOnlyList<DayWindow> Windows(WorkSchedule s)
        => s.Days.OrderBy(d => ((int)d.Day + 6) % 7)
            .Select(d => new DayWindow(d.Day, d.Start, d.End, d.Breaks.OrderBy(b => WorkingWindow.Offset(d.Start, b.Start))
                .Select(b => new BreakSpan(b.Start, b.End)).ToList()))
            .ToList();

    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// "Mon–Fri 08:30–17:30 (break 12:30–13:30)" - days with the same window and breaks grouped, for
    /// the audit trail and the overview.
    /// </summary>
    public static string Describe(string? zoneId, IReadOnlyList<DayWindow> days)
    {
        static string Short(DayOfWeek d) => d.ToString()[..3];
        static string Key(DayWindow d) => $"{Hm(d.Start)}–{Hm(d.End)}" + (d.Breaks.Count == 0 ? "" :
            " (break " + string.Join(", ", d.Breaks.Select(b => $"{Hm(b.Start)}–{Hm(b.End)}")) + ")");
        var parts = new List<string>();
        var ordered = days.OrderBy(d => ((int)d.Day + 6) % 7).ToList();
        for (var i = 0; i < ordered.Count;)
        {
            var j = i;
            while (j + 1 < ordered.Count && Key(ordered[j + 1]) == Key(ordered[i])
                   && ((int)ordered[j + 1].Day + 6) % 7 == ((int)ordered[j].Day + 6) % 7 + 1) j++;
            var span = i == j ? Short(ordered[i].Day) : $"{Short(ordered[i].Day)}–{Short(ordered[j].Day)}";
            parts.Add($"{span} {Key(ordered[i])}");
            i = j + 1;
        }
        var text = string.Join(", ", parts);
        return zoneId is null ? text : $"{text} · {zoneId}";
    }
}
