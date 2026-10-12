using System.Globalization;
using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Management insights (Phase 8), on the same load and tallies as Phase 7 rather than a copy of them:
/// what is ahead (capacity against confirmed, tentative and unscheduled demand), what changed (a
/// period against the one before), which optional quality data the records support, and what needs
/// attention. Every figure is a deterministic sum of records and every statement names its rule;
/// nothing is forecast statistically, and nothing scores or ranks anyone. Definitions:
/// docs/workforce-scheduling/PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md.
/// </summary>
public sealed partial class WorkforceAnalyticsService
{
    /// <summary>The fixed thresholds behind every severity in the attention list. One place, so a statement can always be traced to its rule.</summary>
    public static class InsightThresholds
    {
        /// <summary>Projected load at or above this share of capacity is worth watching.</summary>
        public const int WatchPercent = 90;
        /// <summary>Above this, projected demand exceeds capacity.</summary>
        public const int AttentionPercent = 100;
        public const int CriticalPercent = 120;
        /// <summary>From this many unestimated open items, the unknown demand is itself worth watching.</summary>
        public const int UnestimatedWatchItems = 10;
        /// <summary>Reactive share moving by this many percentage points against the previous period is reported.</summary>
        public const double ReactiveSharePoints = 5;
        /// <summary>An enabled connection with no successful sync for this long is stale.</summary>
        public const int StaleSyncHours = 24;
        /// <summary>From this share of the completed work carrying the data, a signal's data quality is High.</summary>
        public const int HighCoveragePercent = 90;
        /// <summary>From this share of completed client work rated, satisfaction's data quality is High.</summary>
        public const int HighRatedPercent = 50;
    }

    public const int MaxForecastDays = 62;
    public const string NotYetAssigned = "Not yet assigned";
    private static readonly string[] KnownStatuses = ["NEW", "IN_PROGRESS", "WAITING_CUSTOMER", "ON_HOLD", "RESOLVED", "CLOSED"];
    private static readonly string[] KnownPriorities = ["CRITICAL", "URGENT", "HIGH", "NORMAL", "MEDIUM", "LOW"];

    // ---- windows and comparisons -----------------------------------------------------------------------

    /// <summary>
    /// A forecast window: whole dates in the organization's zone, from today forward. Pure, so it is
    /// tested without a database.
    /// </summary>
    public static AnalyticsPeriodDto ResolveWindow(InsightsQuery q, DateOnly today, string zoneId)
    {
        var key = (q.Window ?? (q.From is not null || q.To is not null ? "custom" : "next-7")).Trim().ToLowerInvariant();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateOnly from, to;
        switch (key)
        {
            case "next-7": from = today; to = today.AddDays(6); break;
            case "next-14": from = today; to = today.AddDays(13); break;
            case "next-30": from = today; to = today.AddDays(29); break;
            case "this-week": from = today; to = monday.AddDays(6); break;
            case "next-week": from = monday.AddDays(7); to = monday.AddDays(13); break;
            case "this-month": from = today; to = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1); break;
            case "custom":
                if (q.From is null || q.To is null) throw new ValidationFailedException("A custom window needs both a first and a last date.");
                from = q.From.Value;
                to = q.To.Value;
                break;
            default: throw new ValidationFailedException($"Unknown window \"{key}\". Use next-7, next-14, next-30, this-week, next-week, this-month or custom.");
        }
        if (to < from) throw new ValidationFailedException("The last date is before the first.");
        if (from < today) throw new ValidationFailedException("A forecast starts today or later.");
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > MaxForecastDays) throw new ValidationFailedException($"A forecast covers at most {MaxForecastDays} days.");
        if (to > today.AddYears(1)) throw new ValidationFailedException("Choose dates within a year of today.");
        return new AnalyticsPeriodDto(key, from, to, zoneId, Describe(from, to), days, true);
    }

    /// <summary>A period and the one immediately before it, of the same length (a calendar month against the calendar month before).</summary>
    public static (AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous) ResolveComparison(string? compare, DateOnly today, string zoneId)
    {
        var key = (compare ?? "last-30").Trim().ToLowerInvariant();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateOnly from, to, prevFrom, prevTo;
        switch (key)
        {
            case "last-7": to = today; from = today.AddDays(-6); prevTo = from.AddDays(-1); prevFrom = prevTo.AddDays(-6); break;
            case "last-30": to = today; from = today.AddDays(-29); prevTo = from.AddDays(-1); prevFrom = prevTo.AddDays(-29); break;
            case "last-week": from = monday.AddDays(-7); to = monday.AddDays(-1); prevFrom = from.AddDays(-7); prevTo = from.AddDays(-1); break;
            case "last-month":
                to = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
                from = new DateOnly(to.Year, to.Month, 1);
                prevTo = from.AddDays(-1);
                prevFrom = new DateOnly(prevTo.Year, prevTo.Month, 1);
                break;
            default: throw new ValidationFailedException($"Unknown comparison \"{key}\". Use last-7, last-30, last-week or last-month.");
        }
        AnalyticsPeriodDto P(string k, DateOnly a, DateOnly b) => new(k, a, b, zoneId, Describe(a, b), b.DayNumber - a.DayNumber + 1, b > today);
        return (P(key, from, to), P("previous", prevFrom, prevTo));
    }

    // ---- forecast: loading --------------------------------------------------------------------------------

    /// <summary>An open work item as the forecast reads it: who holds it, what it needs, what is already allocated to it.</summary>
    private sealed record Demand(
        Guid TicketId, Guid? HolderId, Guid? TeamId, int? Required, int Confirmed, int Tentative, bool PlanAhead,
        DateTimeOffset? EarliestStart, Guid? SkillId, DateTimeOffset? DueAt, bool Paused, string Status)
    {
        public bool Estimated => Required is not null;
        public int Allocated => Confirmed + Tentative;
        /// <summary>Estimated effort with no time allocated to it yet. Tentative time counts as allocated here: it is already tentative demand.</summary>
        public int Remaining => Required is { } r ? Math.Max(0, r - Allocated) : 0;
    }

    /// <summary>The forecast for one grouping, accumulated; <see cref="ToDto"/> derives the projection and the gap once.</summary>
    private sealed class Fig
    {
        public bool HasCapacity;
        public long Capacity, Confirmed, Tentative, Unscheduled;
        public int UnscheduledItems, Unestimated;
        public readonly HashSet<Guid> People = [];
        public long Projected => Confirmed + Tentative + Unscheduled;

        public void AddCapacity(int minutes) { HasCapacity = true; Capacity += minutes; }
        public void AddDemand(Demand d, bool eligible)
        {
            if (eligible && d.Remaining > 0) { Unscheduled += d.Remaining; UnscheduledItems++; }
            if (!d.Estimated && !d.PlanAhead) Unestimated++;
        }

        public ForecastFiguresDto ToDto()
        {
            long? cap = HasCapacity ? Capacity : null;
            return new ForecastFiguresDto(
                (int?)cap, (int)Confirmed, (int)Tentative, (int)Unscheduled, UnscheduledItems, Unestimated, (int)Projected,
                cap is null ? null : (int)(cap - Confirmed), cap is null ? null : (int)(cap - Projected),
                cap is > 0 ? Pct(Confirmed, cap.Value) : null, cap is > 0 ? Pct(Projected, cap.Value) : null);
        }
    }

    private sealed class Forecast
    {
        public required Facts F;
        public required AnalyticsPeriodDto Window;
        public required List<Demand> Open;
        public required HashSet<Guid> Eligible;
        public readonly Fig Total = new();
        public readonly Fig Unassigned = new();
        public readonly Dictionary<Guid, Fig> People = [];
        public readonly Dictionary<Guid, Fig> Teams = [];
        public readonly Dictionary<string, (string Name, Fig Fig)> Clients = [], Sources = [];
        /// <summary>Capacity (today: from now), confirmed and tentative minutes per person-day of the window.</summary>
        public readonly Dictionary<(Guid, DateOnly), (int Cap, int Confirmed, int Tentative)> Days = [];
        public readonly Dictionary<DateOnly, int> DueByDay = [];
        public int OverdueMinutes, NoDateMinutes;
        public Dictionary<Guid, string> SkillNames = [];
        public ILookup<Guid, Guid> SkillHolders = Array.Empty<(Guid, Guid)>().ToLookup(x => x.Item1, x => x.Item2);
        public readonly List<(Demand Work, string Risk, int? FreeBeforeDue)> AtRisk = [];
        public int DueInWindow;
        public RecurringDemandDto? Recurring;
        public int FailedTimeEntries;
        public int? OpenWithoutHolder;
        public bool CanSeeHealth;
        public List<(Guid Id, string Name, DateTimeOffset? LastSuccess, ConnectionStatus Status)> SyncProblems = [];
        /// <summary>
        /// A client, source or priority filter is on. Capacity, the gap, whether work fits before its due date and a skill's
        /// free time all depend on ALL of a person's work, so none of them is stated for a slice of it.
        /// </summary>
        public bool Narrowed;
        /// <summary>Whether work can fit before its due date is judged only from today: the free days before a later window are outside it.</summary>
        public bool JudgesFit;

        public Fig Person(Guid id) => Get(People, id);
        public Fig Team(Guid id) => Get(Teams, id);
        public static Fig Get<TKey>(Dictionary<TKey, Fig> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var fig)) map[key] = fig = new Fig();
            return fig;
        }
        public static Fig Get(Dictionary<string, (string Name, Fig Fig)> map, string key, string name)
        {
            if (!map.TryGetValue(key, out var x)) map[key] = x = (name, new Fig());
            return x.Fig;
        }
        /// <summary>A person's free time on a day once confirmed and tentative work is taken out.</summary>
        public int ProjectedFree(Guid person, DateOnly day) => Days.TryGetValue((person, day), out var x) ? Math.Max(0, x.Cap - x.Confirmed - x.Tentative) : 0;
    }

    /// <summary>What is still ahead of an allocation: all of it, the part after now, or nothing.</summary>
    private static int FromNow(Alloc a, DateTimeOffset now)
        => a.EndsAt <= now ? 0 : a.StartsAt >= now ? a.Minutes : (int)Math.Round((a.EndsAt - now).TotalMinutes, MidpointRounding.AwayFromZero);

    private async Task<bool> HoldsAsync(Guid callerId, string permission, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, permission, ct)).Scope == PermissionScope.All;

    private async Task<Forecast> LoadForecastAsync(Guid callerId, InsightsQuery q, CancellationToken ct)
    {
        var zoneId = access.OrganizationTimeZone();
        var window = ResolveWindow(q, WorkforceCalendar.LocalDate(clock.GetUtcNow(), TimeZones.Resolve(zoneId)), zoneId);
        // The Phase 7 load over the window: the people in scope, their capacity (today from now), and the allocations that start in it.
        var f = await LoadAsync(callerId, new AnalyticsQuery("custom", window.From, window.To, q.TeamId, q.DepartmentId, q.AppUserId, q.ClientId, q.Source, q.Priority), withNow: false, ct);
        var now = f.Now;
        var ids = f.People.Select(p => p.Id).ToList();
        var idSet = ids.ToHashSet();
        // The teams the caller's scope reaches: their rows, and the unheld work routed to them.
        var teams = await ScopeTeamsAsync(callerId, q, f, ct);
        // A question about one person or one department is about held work only: unheld work has neither.
        var scopeTeams = q.AppUserId is not null || q.DepartmentId is not null ? [] : teams.ToList();
        var narrowed = f.Filter.Ids is not null;

        var open = new List<Demand>();
        if (ids.Count > 0 || scopeTeams.Count > 0)
        {
            // Three set-based reads (the open work, what it needs, what is allocated to it) rather than one row with
            // six subqueries per ticket: the cost then follows the rows read, not tickets x allocations.
            var openQuery = f.Filter.Apply(db.Tickets.AsNoTracking().Where(TicketStatusRules.Open()))
                .Where(t => (t.AssignedAppUserId != null && ids.Contains(t.AssignedAppUserId.Value))
                            || (t.AssignedAppUserId == null && t.AssignedTeamId != null && scopeTeams.Contains(t.AssignedTeamId.Value)));
            var openIds = openQuery.Select(t => t.Id);
            var work = await openQuery.Select(t => new { t.Id, t.AssignedAppUserId, t.AssignedTeamId, t.SlaDueAt, Paused = t.SlaPausedAt != null, t.PortalStatus }).ToListAsync(ct);
            var needs = (await db.WorkPlannings.AsNoTracking().Where(p => openIds.Contains(p.TicketId))
                    .Select(p => new { p.TicketId, p.RequiredMinutes, p.EarliestStart, p.RequiredSkillId }).ToListAsync(ct))
                .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.First());
            var allocated = (await db.WorkAllocations.AsNoTracking()
                    .Where(a => openIds.Contains(a.TicketId) && (a.Status == WorkAllocationStatus.Planned || a.Status == WorkAllocationStatus.Tentative))
                    .GroupBy(a => new { a.TicketId, a.Status })
                    .Select(g => new { g.Key.TicketId, g.Key.Status, Minutes = g.Sum(a => a.PlannedMinutes), Ahead = g.Count(a => a.EndsAt > now) })
                    .ToListAsync(ct))
                .ToLookup(a => a.TicketId);
            open = work.Select(t =>
            {
                var need = needs.GetValueOrDefault(t.Id);
                var sums = allocated[t.Id].ToList();
                return new Demand(t.Id, t.AssignedAppUserId, t.AssignedAppUserId is null ? t.AssignedTeamId : null, need?.RequiredMinutes,
                    sums.Where(a => a.Status == WorkAllocationStatus.Planned).Sum(a => a.Minutes), sums.Where(a => a.Status == WorkAllocationStatus.Tentative).Sum(a => a.Minutes),
                    sums.Any(a => a.Ahead > 0), need?.EarliestStart, need?.RequiredSkillId, t.SlaDueAt, t.Paused, t.PortalStatus);
            }).ToList();
        }
        // The tickets the demand points at, for the ones the Phase 7 load did not already describe.
        var missing = open.Select(d => d.TicketId).Where(id => !f.Tickets.ContainsKey(id)).Distinct().ToList();
        foreach (var (id, meta) in await TicketMetaAsync(callerId, missing, ct)) f.Tickets[id] = meta;

        var fc = new Forecast
        {
            F = f, Window = window, Open = open, Narrowed = narrowed, JudgesFit = !narrowed && window.From == f.Today,
            // Work whose planning window does not let it start until after this window is not this window's demand.
            Eligible = open.Where(d => d.EarliestStart is not { } e || WorkforceCalendar.LocalDate(e, f.OrgZone) <= window.To).Select(d => d.TicketId).ToHashSet(),
        };

        // Capacity, confirmed and tentative per person-day; today from now.
        foreach (var p in f.People)
            foreach (var d in f.Dates)
                fc.Days[(p.Id, d)] = (!p.IsSchedulable ? 0 : f.CapacityFromNow.TryGetValue((p.Id, d), out var c) ? c : f.Capacity.GetValueOrDefault((p.Id, d), 0), 0, 0);
        foreach (var a in f.Allocations)
        {
            var minutes = FromNow(a, now);
            if (minutes <= 0 || !fc.Days.TryGetValue((a.AppUserId, a.Date), out var x)) continue;
            fc.Days[(a.AppUserId, a.Date)] = a.Confirmed ? (x.Cap, x.Confirmed + minutes, x.Tentative) : (x.Cap, x.Confirmed, x.Tentative + minutes);
            if (!f.Tickets.TryGetValue(a.TicketId, out var m)) continue;
            foreach (var g in new[] { Forecast.Get(fc.Clients, m.ClientKey, m.ClientLabel), Forecast.Get(fc.Sources, m.SourceKey, m.SourceGroupLabel) })
            {
                if (a.Confirmed) g.Confirmed += minutes; else g.Tentative += minutes;
                g.People.Add(a.AppUserId);
            }
        }
        foreach (var p in f.People)
        {
            var figs = new List<Fig> { fc.Total, fc.Person(p.Id) };
            // Only the teams the caller's scope reaches: a colleague's other team is not the caller's to read a row or a shortage for.
            figs.AddRange((f.TeamIdsOf.GetValueOrDefault(p.Id) ?? []).Where(teams.Contains).Select(fc.Team));
            foreach (var d in f.Dates)
            {
                var day = fc.Days[(p.Id, d)];
                foreach (var fig in figs)
                {
                    if (p.IsSchedulable && !narrowed) fig.AddCapacity(day.Cap);
                    fig.Confirmed += day.Confirmed;
                    fig.Tentative += day.Tentative;
                    fig.People.Add(p.Id);
                }
            }
        }

        // Demand that has no time allocated: to its holder (and their teams), or to the team it is routed to when nobody holds it.
        foreach (var d in open)
        {
            var eligible = fc.Eligible.Contains(d.TicketId);
            var holder = d.HolderId is { } h && idSet.Contains(h) ? h : (Guid?)null;
            var figs = new List<Fig> { fc.Total };
            if (holder is { } who)
            {
                figs.Add(fc.Person(who));
                figs.AddRange((f.TeamIdsOf.GetValueOrDefault(who) ?? []).Where(teams.Contains).Select(fc.Team));
            }
            else
            {
                figs.Add(fc.Unassigned);
                if (d.TeamId is { } team) figs.Add(fc.Team(team));
            }
            var m = f.Tickets.GetValueOrDefault(d.TicketId);
            var counts = (eligible && d.Remaining > 0) || (!d.Estimated && !d.PlanAhead);
            if (m is not null)
                foreach (var group in new[] { Forecast.Get(fc.Clients, m.ClientKey, m.ClientLabel), Forecast.Get(fc.Sources, m.SourceKey, m.SourceGroupLabel) })
                {
                    figs.Add(group);
                    // Whoever holds work for a client is one of the people on that client's row, planned or not.
                    if (counts && holder is { } on) group.People.Add(on);
                }
            foreach (var fig in figs) fig.AddDemand(d, eligible);

            // Unscheduled effort has no day of its own: it is shown on the day its work is due, never spread by guesswork.
            if (eligible && d.Remaining > 0)
            {
                var due = d.Paused ? null : d.DueAt;
                // Past its due date by the clock, as the overdue list counts it; not by the calendar.
                if (due is { } at && at < now) fc.OverdueMinutes += d.Remaining;
                // A due day is the ticket's own: work the caller cannot open is counted, and put on no day.
                else if (m?.Visible == true && due is { } then && WorkforceCalendar.LocalDate(then, f.OrgZone) is var day && day >= window.From && day <= window.To)
                    fc.DueByDay[day] = fc.DueByDay.GetValueOrDefault(day) + d.Remaining;
                else fc.NoDateMinutes += d.Remaining;
            }
        }

        // Work at risk: past its due date, or with less free time before the due date than it still needs (the planning queue's rule, for all due work).
        foreach (var d in open.Where(d => d.DueAt is not null && !d.Paused))
        {
            var due = d.DueAt!.Value;
            if (due < now) { fc.AtRisk.Add((d, "Overdue", null)); continue; }
            var holder = d.HolderId is { } h && idSet.Contains(h) ? h : (Guid?)null;
            var dueDate = holder is { } zoned
                ? WorkforceCalendar.LocalDate(due, f.Calendar.Zone(f.ZoneOf(zoned, WorkforceCalendar.LocalDate(due, TimeZoneInfo.Utc))))
                : WorkforceCalendar.LocalDate(due, f.OrgZone);
            if (dueDate < window.From || dueDate > window.To) continue;
            fc.DueInWindow++;
            // Whether it fits is a statement about all of the holder's time between now and the due date.
            if (!fc.JudgesFit || holder is not { } who || d.Remaining <= 0) continue;
            var free = f.Dates.Where(day => day <= dueDate).Sum(day => fc.Days.TryGetValue((who, day), out var x) ? Math.Max(0, x.Cap - x.Confirmed) : 0);
            if (free < d.Remaining) fc.AtRisk.Add((d, "Not enough free time before the due date", free));
        }

        // Skills, only for those some eligible work requires and still has effort to allocate.
        var skillIds = narrowed ? [] : open.Where(d => d.SkillId is not null && d.Remaining > 0 && fc.Eligible.Contains(d.TicketId)).Select(d => d.SkillId!.Value).Distinct().ToList();
        if (skillIds.Count > 0)
        {
            fc.SkillNames = await db.Skills.AsNoTracking().Where(s => skillIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            fc.SkillHolders = (await db.StaffSkills.AsNoTracking().Where(s => skillIds.Contains(s.SkillId) && ids.Contains(s.AppUserId))
                .Select(s => new { s.SkillId, s.AppUserId }).ToListAsync(ct)).ToLookup(s => s.SkillId, s => s.AppUserId);
        }

        fc.FailedTimeEntries = ids.Count == 0 ? 0 : await db.TicketTimeEntries.AsNoTracking()
            .CountAsync(e => e.AppUserId != null && ids.Contains(e.AppUserId.Value) && e.SyncStatus == TimeEntrySyncStatus.Failed, ct);
        if (f.SeesOthers)
            fc.OpenWithoutHolder = await (await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct))
                .Where(TicketStatusRules.Open()).CountAsync(t => t.AssignedAppUserId == null, ct);

        fc.CanSeeHealth = await HoldsAsync(callerId, Permissions.IntegrationHealthView, ct);
        if (fc.CanSeeHealth)
            fc.SyncProblems = (await db.PsaConnections.AsNoTracking().Where(c => c.IsEnabled).OrderBy(c => c.Name).ThenBy(c => c.Id)
                    .Select(c => new { c.Id, c.Name, c.LastSuccessfulSyncAt, c.Status }).ToListAsync(ct))
                .Where(c => SyncProblem(c.Status, c.LastSuccessfulSyncAt, now)).Select(c => (c.Id, c.Name, c.LastSuccessfulSyncAt, c.Status)).ToList();

        if (await HoldsAsync(callerId, Permissions.BoardsManage, ct)) fc.Recurring = await RecurringAsync(f, window, q, idSet, ct);
        return fc;
    }

    /// <summary>
    /// The teams the caller's scope reaches, narrowed by a team filter: every team for someone who sees
    /// everyone, the caller's own teams for a narrower scope, none for someone who sees only themselves.
    /// These are the team rows the forecast shows and the teams whose unheld, routed work it counts;
    /// never the other teams a colleague happens to belong to.
    /// </summary>
    private async Task<HashSet<Guid>> ScopeTeamsAsync(Guid callerId, InsightsQuery q, Facts f, CancellationToken ct)
    {
        if (!f.SeesOthers) return [];
        var all = (await permissions.ResolveAsync(callerId, Permissions.ScheduleView, ct)).Scope == PermissionScope.All;
        var teams = db.Teams.AsNoTracking().Where(t => all || db.UserTeams.Any(m => m.TeamId == t.Id && m.AppUserId == callerId));
        if (q.TeamId is { } only) teams = teams.Where(t => t.Id == only);
        var rows = await teams.OrderBy(t => t.Name).ThenBy(t => t.Id).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        foreach (var r in rows) f.TeamNames.TryAdd(r.Id, r.Name);
        return rows.Select(r => r.Id).ToHashSet();
    }

    private static bool SyncProblem(ConnectionStatus status, DateTimeOffset? lastSuccess, DateTimeOffset now)
        => status is ConnectionStatus.Degraded or ConnectionStatus.Failed || lastSuccess is null || now - lastSuccess.Value > TimeSpan.FromHours(InsightThresholds.StaleSyncHours);

    /// <summary>Recurring tickets scheduled to be raised in the window, by the same pure schedule the worker uses. Counts only: an occurrence carries no estimate.</summary>
    private async Task<RecurringDemandDto> RecurringAsync(Facts f, AnalyticsPeriodDto window, InsightsQuery q, HashSet<Guid> ids, CancellationToken ct)
    {
        var rows = await db.RecurringTickets.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Title).ThenBy(r => r.Id).ToListAsync(ct);
        if (q.TeamId is not null || q.DepartmentId is not null || q.AppUserId is not null)
            rows = rows.Where(r => r.AssignedAppUserId is { } a && ids.Contains(a)).ToList();
        if (q.ClientId is { } client) rows = rows.Where(r => r.ClientCompanyId == client).ToList();
        // A recurring ticket is raised on a team board: any other source has none, and a priority filter keeps its own.
        if (!string.IsNullOrWhiteSpace(q.Source) && q.Source.Trim().ToLowerInvariant() != "internal") rows = [];
        if (!string.IsNullOrWhiteSpace(q.Priority)) rows = rows.Where(r => string.Equals(r.Priority?.Trim(), q.Priority.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var (from, to) = StaffReportPeriods.UtcBounds(window.From, window.To, f.OrgZone);
        var start = from > f.Now ? from.AddTicks(-1) : f.Now;
        var names = f.People.ToDictionary(p => p.Id, p => p.Name);
        var items = new List<RecurringItemDto>();
        foreach (var r in rows)
        {
            var count = 0;
            DateTimeOffset? first = null;
            for (var at = RecurrenceSchedule.Next(r, start, f.OrgZone); at <= to && count < MaxForecastDays * 24; at = RecurrenceSchedule.Next(r, at, f.OrgZone))
            {
                first ??= at;
                count++;
            }
            if (count > 0) items.Add(new RecurringItemDto(r.Id, r.Title, RecurrenceSchedule.Describe(r), r.AssignedAppUserId is { } a ? names.GetValueOrDefault(a) : null, count, first));
        }
        return new RecurringDemandDto(items.Count, items.Sum(i => i.Occurrences), items);
    }

    // ---- forecast: the public reads -------------------------------------------------------------------------

    public async Task<ForecastDto> ForecastAsync(Guid callerId, InsightsQuery query, CancellationToken ct = default)
    {
        var fc = await LoadForecastAsync(callerId, query, ct);
        var f = fc.F;
        var totals = fc.Total.ToDto();

        var people = f.People.Select(p => new ForecastPersonDto(p.Id, p.Name, f.TeamsOf.GetValueOrDefault(p.Id) ?? [], p.IsSchedulable, f.Calendar.HasSchedule(p.Id), FigOf(fc, p).ToDto())).ToList();
        var members = f.TeamIdsOf.Values.SelectMany(teams => teams).GroupBy(team => team).ToDictionary(g => g.Key, g => g.Count());
        var teams = fc.Teams.Where(x => query.TeamId is null || x.Key == query.TeamId)
            .OrderBy(x => f.TeamNames.GetValueOrDefault(x.Key, ""), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key)
            .Select(x => new ForecastGroupDto(x.Key.ToString(), f.TeamNames.GetValueOrDefault(x.Key, "Team"), members.GetValueOrDefault(x.Key), x.Value.ToDto())).ToList();
        var daily = f.Dates.Select(d =>
        {
            var rows = f.People.Where(p => p.IsSchedulable).Select(p => fc.Days[(p.Id, d)]).ToList();
            var all = f.People.Select(p => fc.Days[(p.Id, d)]).ToList();
            return new ForecastDayDto(d, rows.Count == 0 || fc.Narrowed ? null : rows.Sum(x => x.Cap), all.Sum(x => x.Confirmed), all.Sum(x => x.Tentative), fc.DueByDay.GetValueOrDefault(d));
        }).ToList();

        var skills = fc.SkillNames.OrderBy(s => s.Value, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key).Select(s =>
        {
            var work = fc.Open.Where(d => d.SkillId == s.Key && d.Remaining > 0 && fc.Eligible.Contains(d.TicketId)).ToList();
            var holders = fc.SkillHolders[s.Key].Distinct().ToList();
            var free = holders.Sum(h => f.Dates.Sum(d => fc.ProjectedFree(h, d)));
            var names = f.People.Where(p => holders.Contains(p.Id)).Select(p => p.Name).ToList();
            return new SkillCapacityDto(s.Key, s.Value, work.Sum(d => d.Remaining), work.Count, holders.Count, free, free - work.Sum(d => d.Remaining), names);
        }).ToList();

        var estimated = fc.Open.Where(d => d.Estimated).ToList();
        var scheduled = estimated.Sum(d => Math.Min(d.Required!.Value, d.Allocated));
        var coverage = new ScheduleCoverageDto(fc.Open.Count, estimated.Count, fc.Open.Count - estimated.Count, estimated.Sum(d => d.Required!.Value), scheduled,
            estimated.Sum(d => d.Remaining), estimated.Count == 0 ? null : Pct(scheduled, estimated.Sum(d => d.Required!.Value)));
        var quality = new PlanningDataQualityDto(f.People.Count, f.WithoutSchedule, f.NotOffered, fc.Open.Count(d => d.HolderId is not null), fc.OpenWithoutHolder,
            estimated.Count(d => d.SkillId is not null), f.FinishedWithoutDate);
        var atRisk = new WorkAtRiskDto(fc.AtRisk.Count(r => r.Risk == "Overdue"), fc.AtRisk.Count(r => r.Risk != "Overdue"), fc.DueInWindow);

        return new ForecastDto(fc.Window, f.Now, totals, fc.OverdueMinutes, fc.NoDateMinutes, coverage, quality, atRisk, daily, teams, people,
            fc.Unassigned.UnscheduledItems + fc.Unassigned.Unestimated == 0 ? null : fc.Unassigned.ToDto(),
            ForecastGroups(fc.Clients), ForecastGroups(fc.Sources), skills, fc.Recurring,
            ForecastAttention(fc, totals, teams, people, skills, atRisk), ForecastNotes(fc, totals), f.SeesOthers, f.CanExport, fc.CanSeeHealth);
    }

    /// <summary>A person's forecast: with capacity only when they are offered for planned work, as everywhere else.</summary>
    private static Fig FigOf(Forecast fc, Person p) => fc.Person(p.Id);

    private static List<ForecastGroupDto> ForecastGroups(Dictionary<string, (string Name, Fig Fig)> groups)
        => groups.Where(g => g.Value.Fig.Projected > 0 || g.Value.Fig.Unestimated > 0)
            .Select(g => new ForecastGroupDto(g.Key, g.Value.Name, g.Value.Fig.People.Count, g.Value.Fig.ToDto()))
            .OrderByDescending(g => g.Figures.ProjectedMinutes).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();

    private static string Hm(long minutes)
    {
        var m = Math.Abs(minutes);
        return m >= 60 ? (m % 60 == 0 ? $"{m / 60}h" : $"{m / 60}h {m % 60:00}m") : $"{m}m";
    }

    private static string Percent(double? value) => value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "N/A";

    private static InsightSeverity? LoadSeverity(double? projectedPercent)
        => projectedPercent is not { } p ? null
            : p > InsightThresholds.CriticalPercent ? InsightSeverity.Critical
            : p > InsightThresholds.AttentionPercent ? InsightSeverity.Attention
            : p >= InsightThresholds.WatchPercent ? InsightSeverity.Watch : null;

    private static List<InsightFactDto> Components(ForecastFiguresDto x) =>
    [
        new("Capacity", x.CapacityMinutes is { } c ? Hm(c) : "N/A"), new("Confirmed", Hm(x.ConfirmedMinutes)), new("Tentative", Hm(x.TentativeMinutes)),
        new("Estimated unscheduled", Hm(x.UnscheduledMinutes)), new("Projected", Hm(x.ProjectedMinutes)),
        new("Gap", x.GapMinutes is { } g ? (g < 0 ? "−" : "+") + Hm(g) : "N/A"), new("Projected load", Percent(x.ProjectedPercent)),
    ];

    /// <summary>The attention list: each statement from one fixed rule over figures already in the answer, with those figures attached.</summary>
    private static List<InsightDto> ForecastAttention(
        Forecast fc, ForecastFiguresDto totals, List<ForecastGroupDto> teams, List<ForecastPersonDto> people, List<SkillCapacityDto> skills, WorkAtRiskDto atRisk)
    {
        var list = new List<InsightDto>();
        const string loadRule = "Projected load = (confirmed + tentative + estimated unscheduled) ÷ capacity. From 90% watch, above 100% attention, above 120% critical.";
        // Demand against no capacity at all has no percentage, and is a shortage all the same.
        static InsightSeverity? Load(ForecastFiguresDto x) => LoadSeverity(x.ProjectedPercent) ?? (x.GapMinutes < 0 ? InsightSeverity.Attention : null);
        if (Load(totals) is { } overall)
            list.Add(new InsightDto("capacity", overall,
                totals.GapMinutes < 0 ? $"Projected demand is {Hm(-totals.GapMinutes!.Value)} over capacity in this window."
                    : $"Projected demand takes {Percent(totals.ProjectedPercent)} of capacity in this window; {Hm(totals.GapMinutes ?? 0)} is left.",
                loadRule, Components(totals)));
        // A team is held to the same thresholds as everyone together.
        foreach (var t in teams)
            if (Load(t.Figures) is { } load)
                list.Add(new InsightDto("team:" + t.Key, load,
                    t.Figures.GapMinutes < 0 ? $"{t.Name} is projected {Hm(-t.Figures.GapMinutes!.Value)} over capacity."
                        : $"{t.Name} is at {Percent(t.Figures.ProjectedPercent)} of capacity; {Hm(t.Figures.GapMinutes ?? 0)} is left.",
                    loadRule, Components(t.Figures), TargetKind: "team", TargetId: t.Key));

        var over = people.Where(p => p.Figures.ProjectedPercent > InsightThresholds.AttentionPercent).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (over.Count > 0)
            list.Add(new InsightDto("people-over", InsightSeverity.Attention,
                $"{over.Count} {(over.Count == 1 ? "person is" : "people are")} projected over 100% of their capacity.",
                "A person's projected load above 100%. A scheduling condition, not a judgement of the person.",
                over.Take(8).Select(p => new InsightFactDto(p.DisplayName, $"{Percent(p.Figures.ProjectedPercent)} ({Hm(p.Figures.ProjectedMinutes)} of {Hm(p.Figures.CapacityMinutes ?? 0)})")).ToList()));
        var stranded = fc.Narrowed ? [] : people.Where(p => (p.Figures.CapacityMinutes ?? 0) == 0 && p.Figures.ProjectedMinutes > 0).ToList();
        if (stranded.Count > 0)
            list.Add(new InsightDto("no-capacity", InsightSeverity.Attention,
                $"{Hm(stranded.Sum(p => p.Figures.ProjectedMinutes))} of demand is with {stranded.Count} {(stranded.Count == 1 ? "person who has" : "people who have")} no capacity in this window.",
                "Projected demand attributed to someone whose capacity in the window is zero (no working schedule, time away, or not offered for planned work).",
                stranded.Take(8).Select(p => new InsightFactDto(p.DisplayName, Hm(p.Figures.ProjectedMinutes))).ToList()));

        if (totals.UnscheduledMinutes > 0)
            list.Add(new InsightDto("unscheduled", InsightSeverity.Watch,
                $"{Hm(totals.UnscheduledMinutes)} of estimated work across {totals.UnscheduledItems} work item{(totals.UnscheduledItems == 1 ? "" : "s")} has no time allocated.",
                "Estimated effort of open work less the time already allocated to it (confirmed and tentative).",
                [new("Estimated unscheduled", Hm(totals.UnscheduledMinutes)), new("Work items", totals.UnscheduledItems.ToString(CultureInfo.InvariantCulture)),
                 new("Past its due date", Hm(fc.OverdueMinutes)), new("No due date in this window", Hm(fc.NoDateMinutes))], List: "unscheduled"));
        if (totals.UnestimatedItems > 0)
            list.Add(new InsightDto("unestimated", totals.UnestimatedItems >= InsightThresholds.UnestimatedWatchItems ? InsightSeverity.Watch : InsightSeverity.Info,
                $"{totals.UnestimatedItems} open work item{(totals.UnestimatedItems == 1 ? " has" : "s have")} no estimate and no plan, so {(totals.UnestimatedItems == 1 ? "its" : "their")} demand is unknown.",
                "Open work with no estimate and no time allocated ahead. Counted, never turned into hours. Watch from 10 items.",
                [new("Unestimated work items", totals.UnestimatedItems.ToString(CultureInfo.InvariantCulture))], List: "unestimated"));

        foreach (var s in skills.Where(s => s.GapMinutes < 0))
            list.Add(new InsightDto("skill:" + s.SkillId, InsightSeverity.Attention,
                $"{s.Name}: demand is {Hm(-s.GapMinutes)} above the free time of the {s.SkilledPeople} {(s.SkilledPeople == 1 ? "person who holds" : "people who hold")} it.",
                "Unallocated effort of work that requires the skill, against the free time (after confirmed and tentative work) of the people who hold it.",
                [new("Demand", Hm(s.DemandMinutes)), new("Work items", s.DemandItems.ToString(CultureInfo.InvariantCulture)), new("Skilled capacity", Hm(s.CapacityMinutes)),
                 new("People holding it", s.SkilledPeople.ToString(CultureInfo.InvariantCulture))], List: "skill", TargetKind: "skill", TargetId: s.SkillId.ToString()));

        if (atRisk.CapacityShortfall > 0)
            list.Add(new InsightDto("at-risk", InsightSeverity.Attention,
                $"{atRisk.CapacityShortfall} work item{(atRisk.CapacityShortfall == 1 ? " does" : "s do")} not have enough free time before {(atRisk.CapacityShortfall == 1 ? "its" : "their")} due date.",
                "Remaining unallocated effort greater than the holder's free time (after confirmed work) on the days up to the due date: the planning queue's rule, applied to all due work.",
                [new("Work items", atRisk.CapacityShortfall.ToString(CultureInfo.InvariantCulture)), new("Due in this window", atRisk.DueInWindow.ToString(CultureInfo.InvariantCulture))], List: "at-risk"));
        if (atRisk.Overdue > 0)
            list.Add(new InsightDto("overdue", InsightSeverity.Attention,
                $"{atRisk.Overdue} open work item{(atRisk.Overdue == 1 ? " is" : "s are")} past {(atRisk.Overdue == 1 ? "its" : "their")} due date.",
                "Open work whose due date has passed and whose SLA clock is not paused (the boards' rule).",
                [new("Overdue work items", atRisk.Overdue.ToString(CultureInfo.InvariantCulture))], List: "overdue"));

        if (fc.FailedTimeEntries > 0)
            list.Add(new InsightDto("time-not-in-psa", InsightSeverity.Watch,
                $"{fc.FailedTimeEntries} time entr{(fc.FailedTimeEntries == 1 ? "y is" : "ies are")} not in the PSA: the push failed.",
                "Portal time entries of the people in scope whose push to the PSA failed. The time is kept here and can be retried from the ticket.",
                [new("Failed pushes", fc.FailedTimeEntries.ToString(CultureInfo.InvariantCulture))]));
        list.AddRange(fc.SyncProblems.Select(c => SyncInsight(c.Id, c.Name, c.LastSuccess, c.Status)));

        return list.OrderByDescending(i => i.Severity).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
    }

    private static InsightDto SyncInsight(Guid id, string name, DateTimeOffset? lastSuccess, ConnectionStatus status)
        => new("sync:" + id, InsightSeverity.Attention,
            lastSuccess is { } at
                ? $"{name} has not synced successfully since {at.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} UTC."
                : $"{name} has never synced successfully.",
            $"An enabled connection that is degraded or failed, or has had no successful sync for {InsightThresholds.StaleSyncHours} hours. PSA-side figures are as old as that sync.",
            [new("Connection state", status.ToString()), new("Last successful sync", lastSuccess?.ToString("u", CultureInfo.InvariantCulture) ?? "never")], TargetKind: "health");

    private static List<string> ForecastNotes(Forecast fc, ForecastFiguresDto totals)
    {
        var f = fc.F;
        var notes = new List<string>();
        if (fc.Window.From == f.Today) notes.Add("Today is counted from now: capacity is the working time still ahead today, and planned work is the part of today's plan still ahead.");
        if (totals.UnestimatedItems > 0) notes.Add($"{totals.UnestimatedItems} open work item{(totals.UnestimatedItems == 1 ? " has" : "s have")} no estimate and no plan. That demand is of unknown size and is in no hours figure here.");
        if (f.WithoutSchedule > 0) notes.Add($"{(f.WithoutSchedule == 1 ? "1 person has" : $"{f.WithoutSchedule} people have")} no working schedule, so their capacity is 0.");
        if (f.NotOffered > 0) notes.Add($"{(f.NotOffered == 1 ? "1 person is" : $"{f.NotOffered} people are")} not offered for planned work; their capacity does not count.");
        if (fc.Unassigned.UnscheduledItems + fc.Unassigned.Unestimated > 0) notes.Add("Work routed to a team that nobody holds yet is counted in its team and in the totals, and in no person's row.");
        if (f.People.Any(p => (f.TeamIdsOf.GetValueOrDefault(p.Id)?.Count ?? 0) > 1)) notes.Add("A person in more than one team appears under each of them; the totals count them once.");
        if (fc.SkillNames.Count > 0) notes.Add("A person who holds two skills is counted under both, so the skill rows do not add up. Anyone holding a skill counts, at any level.");
        if (fc.Clients.ContainsKey("hidden")) notes.Add("Some demand is on work you cannot open: it counts, and is listed as one row without its client or source. Its unscheduled effort is put on no day.");
        if (fc.Narrowed) notes.Add("A client, source or priority filter shows that work's demand only. Capacity, the gap, projected load, whether work fits before its due date and a skill's free time depend on all of a person's work, and are not stated under such a filter.");
        else if (!fc.JudgesFit) notes.Add("Whether due work can fit before its due date is worked out only for a window that starts today: the free days before a later window are outside it.");
        notes.Add("Remaining effort is the estimate less the time allocated to the work, as the planning queue counts it: a planned hour that has passed counts as allocated whether or not it was worked.");
        notes.Add("A forecast from records: capacity, plans and estimates as they stand now. Nothing is predicted statistically.");
        return notes;
    }

    public async Task<ForecastWorkPageDto> ForecastWorkAsync(Guid callerId, InsightsQuery query, ForecastWorkKind kind, int skip, int take, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = DefaultTake;
        if (take > MaxTake) take = MaxTake;
        var fc = await LoadForecastAsync(callerId, query, ct);
        var rows = ForecastRows(fc, kind, query.SkillId);
        return new ForecastWorkPageDto(kind, fc.Window, rows.Count, rows.Sum(r => r.Minutes ?? 0), skip, take, rows.Skip(skip).Take(take).ToList());
    }

    private static List<ForecastWorkRowDto> ForecastRows(Forecast fc, ForecastWorkKind kind, Guid? skillId)
    {
        var f = fc.F;
        var names = f.People.ToDictionary(p => p.Id, p => p.Name);
        TicketMeta Meta(Guid id) => f.Tickets.TryGetValue(id, out var m) ? m : new TicketMeta(id, HiddenWork, null, null, null, "hidden", "PSA", null, TicketOrigin.Psa, false, false, null, null, "");
        ForecastWorkRowDto Ticket(Demand d, int? minutes, string? risk = null, int? free = null)
        {
            var m = Meta(d.TicketId);
            var holder = d.HolderId is { } h && names.ContainsKey(h) ? h : (Guid?)null;
            return new ForecastWorkRowDto("ticket", d.TicketId, d.TicketId, m.Visible ? m.Reference : HiddenWork, m.Visible ? m.Title : null, m.Visible ? m.ClientName : null, m.SourceName, m.Priority, m.Visible,
                holder, holder is { } p ? names[p] : null, d.TeamId is { } t ? f.TeamNames.GetValueOrDefault(t) : null, null, null,
                minutes, d.Required, d.Estimated ? d.Allocated : null, d.Estimated ? d.Remaining : null,
                m.Visible && !d.Paused ? d.DueAt : null, m.Visible ? free : null, risk, m.Visible && d.SkillId is { } s ? fc.SkillNames.GetValueOrDefault(s) : null, m.Visible ? Blank(d.Status) : null);
        }
        static IEnumerable<ForecastWorkRowDto> Ordered(IEnumerable<ForecastWorkRowDto> rows)
            => rows.OrderBy(r => r.DueAt ?? DateTimeOffset.MaxValue).ThenByDescending(r => r.Minutes ?? 0).ThenBy(r => r.Reference, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.TicketId);

        switch (kind)
        {
            case ForecastWorkKind.Confirmed:
            case ForecastWorkKind.Tentative:
                return f.Allocations.Where(a => a.Confirmed == (kind == ForecastWorkKind.Confirmed) && FromNow(a, f.Now) > 0 && fc.Days.ContainsKey((a.AppUserId, a.Date)))
                    .Select(a =>
                    {
                        var m = Meta(a.TicketId);
                        return new ForecastWorkRowDto("allocation", a.Id, a.TicketId, m.Visible ? m.Reference : HiddenWork, m.Visible ? m.Title : null, m.Visible ? m.ClientName : null, m.SourceName, m.Priority, m.Visible,
                            a.AppUserId, names.GetValueOrDefault(a.AppUserId), null, a.Date, a.StartsAt, FromNow(a, f.Now), null, null, null, null, null, null, null, a.Confirmed ? "Planned" : "Tentative");
                    })
                    .OrderBy(r => r.At).ThenBy(r => r.PersonName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
            case ForecastWorkKind.Unscheduled:
                return Ordered(fc.Open.Where(d => d.Remaining > 0 && fc.Eligible.Contains(d.TicketId)).Select(d => Ticket(d, d.Remaining))).ToList();
            case ForecastWorkKind.Unestimated:
                return Ordered(fc.Open.Where(d => !d.Estimated && !d.PlanAhead).Select(d => Ticket(d, null))).ToList();
            case ForecastWorkKind.AtRisk:
                return Ordered(fc.AtRisk.Where(r => r.Risk != "Overdue").Select(r => Ticket(r.Work, r.Work.Remaining, r.Risk, r.FreeBeforeDue))).ToList();
            case ForecastWorkKind.Overdue:
                return Ordered(fc.AtRisk.Where(r => r.Risk == "Overdue").Select(r => Ticket(r.Work, r.Work.Estimated ? r.Work.Remaining : null, r.Risk))).ToList();
            case ForecastWorkKind.Unassigned:
                return Ordered(fc.Open.Where(d => !(d.HolderId is { } h && names.ContainsKey(h)) && d.Remaining > 0 && fc.Eligible.Contains(d.TicketId)).Select(d => Ticket(d, d.Remaining))).ToList();
            default:
                if (skillId is not { } skill || !fc.SkillNames.ContainsKey(skill)) throw new NotFoundException("Skill");
                var asking = fc.Open.Where(d => d.SkillId == skill && d.Remaining > 0 && fc.Eligible.Contains(d.TicketId)).ToList();
                var rows = Ordered(asking.Where(d => Meta(d.TicketId).Visible).Select(d => Ticket(d, d.Remaining))).ToList();
                // What a piece of work asks for is the ticket's own. Work the caller cannot open is one line here: its effort counts,
                // and nothing (an id, a holder, a size of its own) says which ticket asks for this skill.
                var hidden = asking.Where(d => !Meta(d.TicketId).Visible).ToList();
                if (hidden.Count > 0)
                    rows.Add(new ForecastWorkRowDto("hidden", null, Guid.Empty, hidden.Count == 1 ? HiddenWork : $"{hidden.Count} work items you cannot open", null, null, "", null, false,
                        null, null, null, null, null, hidden.Sum(d => d.Remaining), hidden.Sum(d => d.Required!.Value), hidden.Sum(d => d.Allocated), hidden.Sum(d => d.Remaining), null, null, null, null, null));
                return rows;
        }
    }

    // ---- history: comparison, weekly trend, estimate variance, quality signals ---------------------------------

    public const string OnePersonQuality = "Quality signals describe the work of a team or the organization. They are not shown for one person.";
    public const string OnePersonEstimates = "Estimate variance by kind of work is not shown for one person.";

    public async Task<TrendsDto> TrendsAsync(Guid callerId, InsightsQuery query, CancellationToken ct = default)
    {
        var zoneId = access.OrganizationTimeZone();
        var today = WorkforceCalendar.LocalDate(clock.GetUtcNow(), TimeZones.Resolve(zoneId));
        var (cur, prev) = ResolveComparison(query.Compare, today, zoneId);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeksFrom = monday.AddDays(-49);
        var from = prev.From < weeksFrom ? prev.From : weeksFrom;
        // One load over everything the page needs; each period and each week is then a tally of a sub-range of the same rows.
        var f = await LoadAsync(callerId, new AnalyticsQuery("custom", from, today, query.TeamId, query.DepartmentId, query.AppUserId, query.ClientId, query.Source, query.Priority), withNow: false, ct);
        var tc = Tallies.Over(f, cur.From, cur.To);
        var tp = Tallies.Over(f, prev.From, prev.To);
        // One person's own view of their own work is theirs. Anyone else narrowing to a single person (by name, or by a team of one)
        // gets no quality figure and no estimate breakdown for them: these describe work, and are not a way to grade someone.
        var onePerson = f.People.Count == 1 && f.People[0].Id != callerId;
        var c = tc.Total.ToDto();
        var p = tp.Total.ToDto();

        static ComparisonDto Row(string key, string label, string unit, double? current, double? previous)
            => new(key, label, unit, current, previous, current is { } a && previous is { } b ? Math.Round(a - b, 2) : null,
                unit != "percent" && current is { } x && previous is { } y && y != 0 ? Math.Round(100.0 * (x - y) / y, 2, MidpointRounding.AwayFromZero) : null);
        var totals = new List<ComparisonDto>
        {
            Row("actual", "Actual work", "seconds", c.ActualSeconds, p.ActualSeconds),
            Row("planned-actual", "Planned actual", "seconds", c.PlannedActualSeconds, p.PlannedActualSeconds),
            Row("reactive", "Reactive work", "seconds", c.ReactiveActualSeconds, p.ReactiveActualSeconds),
            Row("reactive-share", "Reactive share", "percent", c.ReactiveSharePercent, p.ReactiveSharePercent),
            Row("planned", "Planned work", "minutes", c.PlannedMinutes, p.PlannedMinutes),
            Row("completed", "Completed work", "count", c.CompletedWork, p.CompletedWork),
            Row("work-items", "Work items", "count", c.WorkItems, p.WorkItems),
        };

        var weeks = Enumerable.Range(0, 8).Select(i =>
        {
            var ws = weeksFrom.AddDays(7 * i);
            var w = Tallies.Over(f, ws, ws.AddDays(6)).Total.ToDto();
            return new WeekTrendDto(ws, ws.AddDays(6), ws.AddDays(6) > today, w.ActualSeconds, w.PlannedActualSeconds, w.ReactiveActualSeconds, w.ReactiveSharePercent, w.CompletedWork, w.WorkItems);
        }).ToList();

        static List<GroupComparisonDto> Compare(Dictionary<string, (string Name, Tally Tally)> current, Dictionary<string, (string Name, Tally Tally)> previous)
            => current.Keys.Union(previous.Keys).Select(key =>
                {
                    var now = current.TryGetValue(key, out var a) ? a.Tally : null;
                    var before = previous.TryGetValue(key, out var b) ? b.Tally : null;
                    long x = now?.Actual ?? 0, y = before?.Actual ?? 0;
                    return new GroupComparisonDto(key, now is not null ? a.Name : b.Name, x, y, x - y, y == 0 ? null : Pct(x - y, y),
                        now?.Reactive ?? 0, now?.Completed.Count ?? 0, before?.Completed.Count ?? 0, now?.Items.Count ?? 0);
                })
                .Where(g => g.CurrentSeconds > 0 || g.PreviousSeconds > 0 || g.CurrentCompleted > 0 || g.PreviousCompleted > 0)
                .OrderByDescending(g => g.CurrentSeconds).ThenByDescending(g => g.PreviousSeconds).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();

        static List<EstimateVarianceRowDto> Estimates(Dictionary<string, (string Name, Tally Tally)> groups)
            => groups.Where(g => g.Value.Tally.Compared > 0)
                .Select(g =>
                {
                    var t = g.Value.Tally;
                    return new EstimateVarianceRowDto(g.Key, g.Value.Name, (int)t.PlannedCompared, (int)t.ComparedActual, (int)(t.ComparedActual - t.PlannedCompared),
                        Pct(t.ComparedActual - t.PlannedCompared, t.PlannedCompared), (int)t.AbsVariance, Pct(t.AbsVariance, t.PlannedCompared), t.Compared);
                })
                .OrderByDescending(r => r.PlannedMinutes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();

        var attention = new List<InsightDto>();
        if (c.ReactiveSharePercent is { } share && p.ReactiveSharePercent is { } before && Math.Abs(share - before) >= InsightThresholds.ReactiveSharePoints)
            attention.Add(new InsightDto("reactive-share", InsightSeverity.Watch,
                $"Reactive work share went from {Percent(before)} to {Percent(share)} against the previous period ({(share > before ? "+" : "−")}{Math.Abs(Math.Round(share - before, 1)).ToString("0.#", CultureInfo.InvariantCulture)} points).",
                $"Reactive share = reactive time ÷ actual time. Reported when it moves by {InsightThresholds.ReactiveSharePoints} percentage points or more between the two periods. Reactive work is not a fault: incidents and urgent support are reactive by nature.",
                [new("This period", $"{Percent(share)} ({Hm(c.ReactiveActualSeconds / 60)} of {Hm(c.ActualSeconds / 60)})"), new("Previous period", $"{Percent(before)} ({Hm(p.ReactiveActualSeconds / 60)} of {Hm(p.ActualSeconds / 60)})")]));

        var notes = new List<string>
        {
            $"{cur.Label} against {prev.Label}. A percentage change is N/A when the previous value is 0; both values are always shown.",
            "Estimate variance compares planned time with the time recorded on that same planned work, over the days that had a plan. It describes estimates, not people.",
            "Quality signals are computed over the work completed in the period and are shown with what they rest on. There is no per-technician quality table.",
            "Time entered directly in a PSA has no portal row and is not in any figure here.",
        };
        if (cur.To >= today) notes.Insert(1, "The current period includes today, which is not over.");
        if (onePerson) notes.Add(OnePersonQuality + " " + OnePersonEstimates);

        return new TrendsDto(cur, prev, f.Now, totals, weeks, Compare(tc.Clients, tp.Clients), Compare(tc.Sources, tp.Sources),
            onePerson ? [] : Estimates(tc.Categories), onePerson ? [] : Estimates(tc.Clients), onePerson ? [] : Estimates(tc.Sources),
            QualitySignals(f, cur, prev, onePerson), attention, f.Sync, notes);
    }

    /// <summary>
    /// The quality signals the records support, over the work completed in each period. Each comes with
    /// its numerator, its denominator, the population and a data-quality label set by fixed rules; a
    /// signal with no explicit source says so instead of being inferred.
    /// </summary>
    private static List<QualitySignalDto> QualitySignals(Facts f, AnalyticsPeriodDto cur, AnalyticsPeriodDto prev, bool onePerson)
    {
        List<Done> In(AnalyticsPeriodDto period) => f.Completed.Where(d => d.Date >= period.From && d.Date <= period.To).GroupBy(d => d.TicketId).Select(g => g.First()).ToList();
        var now = In(cur);
        var before = In(prev);
        var hasPsa = now.Any(d => d.Origin == TicketOrigin.Psa);
        static double? Share(int met, int eligible) => eligible == 0 ? null : Pct(met, eligible);
        static DataQuality Coverage(int eligible, int population)
            => eligible == 0 ? DataQuality.NotAvailable : 100.0 * eligible / population >= InsightThresholds.HighCoveragePercent ? DataQuality.High : DataQuality.Partial;

        QualitySignalDto Signal(string key, string name, string definition, Func<Done, bool> eligible, Func<Done, bool> met, DataQuality quality, string reason)
        {
            if (onePerson) return new QualitySignalDto(key, name, definition, DataQuality.NotAvailable, OnePersonQuality, null, null, null, now.Count, null, null, null);
            int e = now.Count(eligible), m = now.Count(d => eligible(d) && met(d)), pe = before.Count(eligible), pm = before.Count(d => eligible(d) && met(d));
            return quality == DataQuality.NotAvailable
                ? new QualitySignalDto(key, name, definition, quality, reason, null, null, null, now.Count, null, null, null)
                : new QualitySignalDto(key, name, definition, quality, reason, m, e, Share(m, e), now.Count, pm, pe, Share(pm, pe));
        }

        var due = now.Count(d => d.DueAt is not null);
        var promised = now.Count(d => d.FirstResponseDueAt is not null);
        var rated = now.Count(d => d.Rating is not null);
        var clientWork = now.Count(d => d.Origin == TicketOrigin.Psa);
        var reviewed = now.Count(d => d.Reviewed);
        return
        [
            Signal("due-date-met", "Resolved by its due date", "Completed work finished at or before its due date, out of the completed work that has one.",
                d => d.DueAt is not null, d => d.FinishedAt <= d.DueAt, Coverage(due, Math.Max(1, now.Count)),
                due == 0 ? "None of the completed work carries a due date."
                    : $"{due} of {now.Count} completed work items carry a due date. The date is the PSA's target (or a typed due date when it has none) or the SLA plan's; time paused is not reconstructed."),
            Signal("first-response-met", "First response promise kept", "Completed work answered at or before the reply time its SLA plan promised, out of the work that carried a promise.",
                d => d.FirstResponseDueAt is not null, d => d.FirstRespondedAt is { } at && at <= d.FirstResponseDueAt,
                promised == 0 ? DataQuality.NotAvailable : DataQuality.Partial,
                promised == 0 ? "None of the completed work carried a reply promise. PSA tickets never carry one here."
                    : $"{promised} of {now.Count} completed work items carried a reply promise (board and monitoring work with an SLA plan). Only replies made through the portal are seen."),
            Signal("reopened", "Reopened", "Completed work that was brought back to work at least once, out of all the completed work.",
                _ => true, d => d.ReopenCount > 0, now.Count == 0 ? DataQuality.NotAvailable : hasPsa ? DataQuality.Partial : DataQuality.High,
                now.Count == 0 ? "No work was completed in the period."
                    : hasPsa ? "A reopen is recorded when the status is changed back in the portal. A ticket reopened in the PSA itself is not counted."
                    : "A reopen is recorded when a finished ticket's status is changed back; all of this work is the team's own."),
            Signal("satisfaction", "Client satisfaction", "Ratings of 4 or 5 out of 5, out of the completed work a client rated.",
                d => d.Rating is not null, d => d.Rating >= TicketSatisfaction.Satisfied,
                rated == 0 ? DataQuality.NotAvailable : 100.0 * rated / Math.Max(1, clientWork) >= InsightThresholds.HighRatedPercent ? DataQuality.High : DataQuality.Partial,
                rated == 0 ? "No completed work has a client rating." : $"{rated} of {clientWork} completed client work items were rated, by the clients who chose to answer."),
            Signal("review-first-time", "Passed review first time", "Reviewed work approved without being sent back, out of the completed work that was reviewed.",
                d => d.Reviewed, d => d.ReviewSendBacks == 0, reviewed == 0 ? DataQuality.NotAvailable : DataQuality.Partial,
                reviewed == 0 ? "None of the completed work went through review." : $"{reviewed} of {now.Count} completed work items were reviewed: only boards and topics that require review."),
            new QualitySignalDto("escalated", "Escalated work", "Work that was escalated.", DataQuality.NotAvailable,
                onePerson ? OnePersonQuality : "No escalation is recorded anywhere: a reassignment is not an escalation, and none is inferred from one.", null, null, null, now.Count, null, null, null),
            new QualitySignalDto("repeat-issues", "Repeat issues", "Work that repeats an earlier issue.", DataQuality.NotAvailable,
                onePerson ? OnePersonQuality : "Links between tickets are entered by hand, and a duplicate is not a repeat. Nothing is inferred from titles.", null, null, null, now.Count, null, null, null),
        ];
    }

    // ---- mapping and integration health -------------------------------------------------------------------------

    public async Task<IntegrationInsightsDto> HealthAsync(Guid callerId, CancellationToken ct = default)
    {
        if (!await HoldsAsync(callerId, Permissions.IntegrationHealthView, ct))
            throw new ForbiddenException("Mapping and integration health needs the integration.health.view permission.");
        var now = clock.GetUtcNow();
        var connections = await db.PsaConnections.AsNoTracking().Where(c => c.ArchivedAt == null).OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Provider, c.Status, c.IsEnabled, c.LastSuccessfulSyncAt, c.LastHealthCheckAt, HasError = c.LastError != null && c.LastError != "" }).ToListAsync(ct);
        var psa = db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId != null);
        var statuses = await psa.GroupBy(t => new { t.PsaConnectionId, t.PortalStatus }).Select(g => new { g.Key.PsaConnectionId, Value = g.Key.PortalStatus, N = g.Count() }).ToListAsync(ct);
        var priorities = await psa.GroupBy(t => new { t.PsaConnectionId, t.PortalPriority }).Select(g => new { g.Key.PsaConnectionId, Value = g.Key.PortalPriority, N = g.Count() }).ToListAsync(ct);
        var syncErrors = await psa.Where(t => t.SyncStatus == TicketSyncStatus.Error).GroupBy(t => t.PsaConnectionId).Select(g => new { Conn = g.Key, N = g.Count() }).ToListAsync(ct);
        var logins = await psa.Where(t => t.AssignedTechnicianExternalId != null && t.AssignedTechnicianExternalId != "")
            .Select(t => new { t.PsaConnectionId, t.AssignedTechnicianExternalId }).Distinct().ToListAsync(ct);
        var placeholders = await psa.Where(t => db.ClientCompanies.Any(c => c.Id == t.ClientCompanyId && c.ExternalCompanyId == "unknown"))
            .GroupBy(t => t.PsaConnectionId).Select(g => new { Conn = g.Key, N = g.Count() }).ToListAsync(ct);
        var time = await db.TicketTimeEntries.AsNoTracking().Where(e => e.SyncStatus != TimeEntrySyncStatus.Synced && e.Ticket!.PsaConnectionId != null)
            .GroupBy(e => new { Conn = e.Ticket!.PsaConnectionId, e.SyncStatus }).Select(g => new { g.Key.Conn, g.Key.SyncStatus, N = g.Count() }).ToListAsync(ct);
        var links = await PsaLinks.LoadAsync(db, ct);
        var account = await IntegrationIdentity.LoadAsync(db, ct);

        static MappingCoverageDto Mapped(IEnumerable<(string? Value, int N)> rows, string[] known)
        {
            var list = rows.ToList();
            var total = list.Sum(r => r.N);
            var unmapped = list.Where(r => !known.Contains((r.Value ?? "").Trim().ToUpperInvariant())).ToList();
            return new MappingCoverageDto(total - unmapped.Sum(r => r.N), total, total == 0 ? null : Pct(total - unmapped.Sum(r => r.N), total),
                unmapped.OrderByDescending(r => r.N).ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase).Take(5).Select(r => string.IsNullOrWhiteSpace(r.Value) ? "(empty)" : r.Value!).ToList());
        }

        var rows = connections.Select(c =>
        {
            var mine = logins.Where(l => l.PsaConnectionId == c.Id && !account.IsAccount(c.Id, l.AssignedTechnicianExternalId)).Select(l => l.AssignedTechnicianExternalId!).ToList();
            var linked = mine.Count(l => links.UserFor(c.Id, l) is not null);
            return new ConnectionInsightDto(c.Id, c.Name, WorkPlanService.Source(TicketOrigin.Psa, c.Provider), c.Status.ToString(), c.IsEnabled,
                c.LastSuccessfulSyncAt, c.LastHealthCheckAt, c.HasError, c.IsEnabled && SyncProblem(c.Status, c.LastSuccessfulSyncAt, now),
                statuses.Where(s => s.PsaConnectionId == c.Id).Sum(s => s.N),
                Mapped(statuses.Where(s => s.PsaConnectionId == c.Id).Select(s => ((string?)s.Value, s.N)), KnownStatuses),
                Mapped(priorities.Where(s => s.PsaConnectionId == c.Id).Select(s => ((string?)s.Value, s.N)), KnownPriorities),
                new MappingCoverageDto(linked, mine.Count, mine.Count == 0 ? null : Pct(linked, mine.Count), []),
                placeholders.Where(x => x.Conn == c.Id).Sum(x => x.N), syncErrors.Where(x => x.Conn == c.Id).Sum(x => x.N),
                time.Where(x => x.Conn == c.Id && x.SyncStatus == TimeEntrySyncStatus.Failed).Sum(x => x.N),
                time.Where(x => x.Conn == c.Id && x.SyncStatus == TimeEntrySyncStatus.Pending).Sum(x => x.N));
        }).ToList();

        var attention = rows.Where(r => r.Stale).Select(r => SyncInsight(r.ConnectionId, r.Name, r.LastSuccessfulSyncAt, Enum.Parse<ConnectionStatus>(r.Status))).ToList();
        foreach (var r in rows.Where(r => r.StatusMapping.Mapped < r.StatusMapping.Total || r.PriorityMapping.Mapped < r.PriorityMapping.Total))
            attention.Add(new InsightDto("mapping:" + r.ConnectionId, InsightSeverity.Watch,
                $"{r.Name}: {r.StatusMapping.Total - r.StatusMapping.Mapped} ticket{(r.StatusMapping.Total - r.StatusMapping.Mapped == 1 ? "" : "s")} carry a status and {r.PriorityMapping.Total - r.PriorityMapping.Mapped} a priority that no mapping rule covers.",
                "A portal status or priority outside the normalized set: the PSA's own value passed through because no rule maps it. Reports group such tickets under the raw value.",
                [new("Unmapped statuses", string.Join(", ", r.StatusMapping.Unmapped.DefaultIfEmpty("none"))), new("Unmapped priorities", string.Join(", ", r.PriorityMapping.Unmapped.DefaultIfEmpty("none")))]));
        return new IntegrationInsightsDto(now, rows, attention.OrderByDescending(i => i.Severity).ThenBy(i => i.Key, StringComparer.Ordinal).ToList());
    }
}
