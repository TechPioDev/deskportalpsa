using System.Globalization;
using System.Text;
using Desk.Application.Admin;
using Desk.Application.Authorization;
using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Application.Tickets;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Reporting;
using Desk.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Workforce analytics (Phase 7): capacity, planned time, actual time, utilization, planned against
/// actual, reactive work and completed work over a period, for the people the caller's
/// <c>schedule.view</c> scope reaches. Every definition is in
/// docs/workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md; nothing here redefines one.
///
/// How it works: one load reads everything the period needs in a fixed number of queries, however
/// many people or days (the capacity calendar as Phase 2 builds it, the allocations, the time
/// entries, the live clocks, the finished work, the open work, the tickets they point at), then
/// every figure, breakdown, drill-down and export is tallied from those same rows in memory. A card
/// and the list it opens cannot disagree, because they are the same rows counted twice.
///
/// Facts, not judgements: nothing is a score, nobody is ranked, and a ratio with nothing to divide
/// by is null (N/A). Internal only: client accounts are a different table and never reach this.
/// </summary>
public sealed class WorkforceAnalyticsService(
    DeskDbContext db, WorkforceAccess access, ICapacityService capacity, ITicketScopeQuery tickets,
    IEffectivePermissionService permissions, IAuditWriter audit, TimeProvider clock) : IWorkforceAnalyticsService
{
    public const int MaxDays = 366;
    public const int MaxPeople = 1000;
    public const int HeatmapMaxDays = 31;
    public const int HeatmapMaxPeople = 200;
    public const int MaxItems = 200;
    public const int DefaultTake = 50;
    public const int MaxTake = 200;
    public const int MaxExportRows = 50_000;
    public const string HiddenWork = "Work you cannot open";
    public const string NoClient = "No client";
    public const string NotSet = "Not set";
    public const string RunningClock = "Running clock (not logged yet)";
    private const int IdChunk = 2000;

    // ---- period ---------------------------------------------------------------------------------------

    /// <summary>
    /// The dates a query means, in the organization's zone, from the server's today - so two people
    /// asking for "this week" get the same week. Pure, so it is tested without a database.
    /// </summary>
    public static AnalyticsPeriodDto ResolvePeriod(AnalyticsQuery q, DateOnly today, string zoneId)
    {
        var key = (q.Period ?? (q.From is not null || q.To is not null ? "custom" : "this-week")).Trim().ToLowerInvariant();
        DateOnly from, to;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        switch (key)
        {
            case "today": from = to = today; break;
            case "yesterday": from = to = today.AddDays(-1); break;
            case "this-week": from = monday; to = monday.AddDays(6); break;
            case "last-week": from = monday.AddDays(-7); to = monday.AddDays(-1); break;
            case "this-month": from = new DateOnly(today.Year, today.Month, 1); to = from.AddMonths(1).AddDays(-1); break;
            case "last-month": to = new DateOnly(today.Year, today.Month, 1).AddDays(-1); from = new DateOnly(to.Year, to.Month, 1); break;
            case "7d": to = today; from = today.AddDays(-6); break;
            case "30d": to = today; from = today.AddDays(-29); break;
            case "custom":
                if (q.From is null || q.To is null) throw new ValidationFailedException("A custom period needs both a first and a last date.");
                from = q.From.Value;
                to = q.To.Value;
                break;
            default: throw new ValidationFailedException($"Unknown period \"{key}\". Use today, yesterday, this-week, last-week, this-month, last-month, 7d, 30d or custom.");
        }
        if (to < from) throw new ValidationFailedException("The last date is before the first.");
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > MaxDays) throw new ValidationFailedException($"Ask for at most {MaxDays} days at a time.");
        if (from < today.AddYears(-1) || to > today.AddYears(1)) throw new ValidationFailedException("Choose dates within a year of today.");
        return new AnalyticsPeriodDto(key, from, to, zoneId, Describe(from, to), days, to > today);
    }

    public static string Describe(DateOnly from, DateOnly to)
    {
        var inv = CultureInfo.InvariantCulture;
        if (from == to) return from.ToString("ddd d MMM yyyy", inv);
        return from.Year == to.Year
            ? string.Create(inv, $"{from:ddd d MMM} – {to:ddd d MMM yyyy}")
            : string.Create(inv, $"{from:ddd d MMM yyyy} – {to:ddd d MMM yyyy}");
    }

    // ---- the public reads -------------------------------------------------------------------------

    public async Task<AnalyticsFilterOptionsDto> FiltersAsync(Guid callerId, CancellationToken ct = default)
    {
        var groups = await capacity.GroupsAsync(callerId, ct);
        var staff = (await access.VisibleStaffAsync(callerId, ct)).Where(u => u.IsActive);
        var people = await staff.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(MaxPeople)
            .Select(u => new AnalyticsOptionDto(u.Id.ToString(), u.DisplayName)).ToListAsync(ct);

        // Only clients and priorities of tickets the caller may open: a filter list is a list of facts too.
        var visible = await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct);
        var clientIds = await visible.Where(t => t.ClientCompanyId != null).Select(t => t.ClientCompanyId!.Value).Distinct().ToListAsync(ct);
        var clients = clientIds.Count == 0 ? []
            : await db.ClientCompanies.AsNoTracking().Where(c => clientIds.Contains(c.Id)).OrderBy(c => c.Name).ThenBy(c => c.Id)
                .Select(c => new AnalyticsOptionDto(c.Id.ToString(), c.Name)).ToListAsync(ct);
        var priorities = (await visible.Where(t => t.PortalPriority != null && t.PortalPriority != "").Select(t => t.PortalPriority!).Distinct().ToListAsync(ct))
            .OrderBy(PriorityRank).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var connections = await db.PsaConnections.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var sources = new List<AnalyticsOptionDto>();
        if (connections.Count > 1) sources.Add(new("client", "Client work (any PSA)"));
        sources.AddRange(connections.Select(c => new AnalyticsOptionDto("psa:" + c.Id, c.Name)));
        sources.Add(new("internal", "Team boards"));
        sources.Add(new("monitoring", "Monitoring"));

        return new AnalyticsFilterOptionsDto(groups.Teams, groups.Departments, people, clients, sources, priorities,
            await SeesOthersAsync(callerId, ct), await CanExportAsync(callerId, ct), access.OrganizationTimeZone());
    }

    public async Task<AnalyticsOverviewDto> OverviewAsync(Guid callerId, AnalyticsQuery query, CancellationToken ct = default)
    {
        var f = await LoadAsync(callerId, query, withNow: true, ct);
        var t = Tallies.Over(f);

        var people = f.People.Select(p => PersonDto(f, p, t.Person(p.Id))).ToList();
        var teams = t.Teams.OrderBy(x => f.TeamNames.GetValueOrDefault(x.Key, ""), StringComparer.OrdinalIgnoreCase)
            .Select(x => new AnalyticsGroupDto(x.Key.ToString(), f.TeamNames.GetValueOrDefault(x.Key, "Team"), f.People.Count(p => f.TeamIdsOf.GetValueOrDefault(p.Id)?.Contains(x.Key) == true), x.Value.ToDto())).ToList();

        var heatmap = Heatmap(f, t);
        var daily = f.Dates.Select(d => new AnalyticsDayDto(d, t.Day(d).ToDto())).ToList();
        var totals = t.Total.ToDto();
        var demand = new CapacityDemandDto(
            totals.CapacityMinutes ?? 0, totals.PlannedMinutes, totals.TentativeMinutes, totals.PlannedMinutes + totals.TentativeMinutes,
            Math.Max(0, totals.PlannedMinutes + totals.TentativeMinutes - (totals.CapacityMinutes ?? 0)),
            Math.Max(0, (totals.CapacityMinutes ?? 0) - totals.PlannedMinutes));

        return new AnalyticsOverviewDto(f.Period, f.Now, totals, f.NowDto!, demand, people, teams,
            Groups(t.Clients, f), Groups(t.Sources, f), Groups(t.Priorities, f), Groups(t.WorkTypes, f),
            daily, heatmap, f.Sync, Notes(f, t), f.SeesOthers, f.CanExport);
    }

    public async Task<TechnicianAnalyticsDto> TechnicianAsync(Guid callerId, Guid appUserId, AnalyticsQuery query, CancellationToken ct = default)
    {
        var f = await LoadAsync(callerId, query with { AppUserId = appUserId }, withNow: false, ct);
        var p = f.People.Single();
        var t = Tallies.Over(f);

        var items = t.Items.Values
            .Select(i =>
            {
                var m = f.Tickets[i.TicketId];
                var plannedMin = i.PlannedMinutes;
                var actualMin = (int)Math.Round(i.ActualSeconds / 60.0, MidpointRounding.AwayFromZero);
                return new AnalyticsWorkItemDto(i.TicketId, m.Visible ? m.Reference : HiddenWork, m.Visible ? m.Title : null, m.Visible ? m.ClientName : null,
                    m.SourceName, m.Priority, m.Visible, m.Finished, m.FinishedAt,
                    plannedMin, i.TentativeMinutes, i.ActualSeconds, i.ReactiveSeconds,
                    plannedMin > 0 ? actualMin - plannedMin : null, plannedMin > 0 ? Pct(actualMin - plannedMin, plannedMin) : null, i.Entries, i.Days.Count);
            })
            .OrderByDescending(i => i.ActualSeconds).ThenByDescending(i => i.PlannedMinutes).ThenBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TechnicianAnalyticsDto(f.Period, f.Now, PersonDto(f, p, t.Person(p.Id)),
            Groups(t.Clients, f), Groups(t.Sources, f), Groups(t.WorkTypes, f), Groups(t.Priorities, f),
            f.Dates.Select(d => new AnalyticsDayDto(d, t.Day(d).ToDto())).ToList(),
            items.Take(MaxItems).ToList(), items.Count > MaxItems, Notes(f, t));
    }

    public async Task<AnalyticsWorkPageDto> WorkAsync(Guid callerId, AnalyticsQuery query, AnalyticsWorkKind kind, int skip, int take, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = DefaultTake;
        if (take > MaxTake) take = MaxTake;
        var now = kind is AnalyticsWorkKind.Open or AnalyticsWorkKind.Unscheduled or AnalyticsWorkKind.Overdue;
        var f = await LoadAsync(callerId, query, withNow: now, ct);
        var rows = Rows(f, kind);
        var page = rows.Skip(skip).Take(take).ToList();
        return new AnalyticsWorkPageDto(kind, f.Period, rows.Count, rows.Sum(r => (long)(r.Seconds ?? 0)), rows.Sum(r => r.Minutes ?? 0), skip, take, page);
    }

    public async Task<AnalyticsExportDto> ExportAsync(Guid callerId, AnalyticsQuery query, AnalyticsExportReport report, CancellationToken ct = default)
    {
        // The controller requires the claim; the service checks it again, so no other caller can export without it.
        if (!await CanExportAsync(callerId, ct)) throw new ForbiddenException("Exporting workforce analytics needs the workforce.analytics.export permission.");
        var f = await LoadAsync(callerId, query, withNow: false, ct);
        var t = Tallies.Over(f);
        var sb = new StringBuilder();
        var rows = 0;
        void Line(params object?[] cells) { sb.Append(string.Join(',', cells.Select(TechnicianReportRenderer.Cell))).Append("\r\n"); }
        void Head(string title)
        {
            Line($"Workforce analytics: {title}");
            Line("Period", f.Period.Label, f.Period.TimeZone, f.Period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.Period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Line("Generated", f.Now.ToString("u", CultureInfo.InvariantCulture), "Facts about planned and recorded work; not a performance score.");
            Line();
        }
        static decimal H(long minutes) => Math.Round(minutes / 60m, 2);
        static decimal Hs(long seconds) => Math.Round(seconds / 3600m, 2);
        static string P(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "N/A";
        string[] figureHeader = ["Capacity (h)", "Planned (h)", "Tentative (h)", "Actual (h)", "Planned actual (h)", "Reactive (h)", "Reactive share %", "Scheduled utilization %", "Capacity utilization %",
            "Variance (h)", "Estimate variance %", "Completed", "Work items", "Billable (h)", "Client work (h)", "Internal work (h)", "Monitoring work (h)", "Over capacity (h)"];
        object?[] Figures(AnalyticsFiguresDto d) =>
        [
            d.CapacityMinutes is { } c ? H(c) : "N/A", H(d.PlannedMinutes), H(d.TentativeMinutes), Hs(d.ActualSeconds), Hs(d.PlannedActualSeconds), Hs(d.ReactiveActualSeconds),
            P(d.ReactiveSharePercent), P(d.ScheduledUtilizationPercent), P(d.CapacityUtilizationPercent),
            d.VarianceMinutes is { } v ? H(v) : "N/A", P(d.EstimateVariancePercent), d.CompletedWork, d.WorkItems, Hs(d.BillableSeconds), Hs(d.ClientSeconds), Hs(d.InternalSeconds), Hs(d.MonitoringSeconds), H(d.OverCapacityMinutes),
        ];

        switch (report)
        {
            case AnalyticsExportReport.Technicians:
                Head("technicians");
                Line(["Technician", "Teams", "Offered for planned work", .. figureHeader]);
                foreach (var p in f.People)
                {
                    Line([p.Name, string.Join("; ", f.TeamsOf.GetValueOrDefault(p.Id) ?? []), p.IsSchedulable ? "yes" : "no", .. Figures(t.Person(p.Id).ToDto())]);
                    rows++;
                }
                break;
            case AnalyticsExportReport.Teams:
            case AnalyticsExportReport.Clients:
            case AnalyticsExportReport.Sources:
                var (title, groups) = report switch
                {
                    AnalyticsExportReport.Teams => ("teams", t.Teams.OrderBy(x => f.TeamNames.GetValueOrDefault(x.Key, ""), StringComparer.OrdinalIgnoreCase)
                        .Select(x => new AnalyticsGroupDto(x.Key.ToString(), f.TeamNames.GetValueOrDefault(x.Key, "Team"), 0, x.Value.ToDto())).ToList()),
                    AnalyticsExportReport.Clients => ("clients", Groups(t.Clients, f)),
                    _ => ("sources", Groups(t.Sources, f)),
                };
                Head(title);
                Line([title == "teams" ? "Team" : title == "clients" ? "Client" : "Source", .. figureHeader]);
                foreach (var g in groups) { Line([g.Name, .. Figures(g.Figures)]); rows++; }
                break;
            case AnalyticsExportReport.Daily:
                Head("planned against actual by day");
                Line(["Date", "Capacity (h)", "Planned (h)", "Tentative (h)", "Actual (h)", "Planned actual (h)", "Reactive (h)", "Variance (h)", "Completed"]);
                foreach (var d in f.Dates)
                {
                    var g = t.Day(d).ToDto();
                    Line(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.CapacityMinutes is { } c ? H(c) : "N/A", H(g.PlannedMinutes), H(g.TentativeMinutes),
                        Hs(g.ActualSeconds), Hs(g.PlannedActualSeconds), Hs(g.ReactiveActualSeconds), g.VarianceMinutes is { } v ? H(v) : "N/A", g.CompletedWork);
                    rows++;
                }
                break;
            default:
                var work = Rows(f, AnalyticsWorkKind.Actual);
                if (work.Count > MaxExportRows) throw new ValidationFailedException($"That is {work.Count:N0} rows; the export holds at most {MaxExportRows:N0}. Narrow the period or the filters.");
                Head("recorded work");
                Line("Date", "Technician", "Work", "Title", "Client", "Source", "Priority", "Minutes", "Billable", "Planned or reactive", "State");
                foreach (var r in work)
                {
                    Line(r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.PersonName, r.Reference, r.Title, r.ClientName, r.Source, r.Priority,
                        r.Seconds is { } s ? Math.Round(s / 60.0, 2) : r.Minutes, r.Billable is { } b ? (b ? "yes" : "no") : "", r.PlannedWork == true ? "planned" : "reactive", r.Status);
                    rows++;
                }
                break;
        }

        var name = $"workforce-analytics-{report.ToString().ToLowerInvariant()}-{f.Period.From:yyyy-MM-dd}-{f.Period.To:yyyy-MM-dd}.csv";
        // Audited: named people's figures leave the system in a file.
        await audit.WriteAsync("workforce.analytics.exported", "WorkforceAnalytics", null,
            new { report = report.ToString(), period = new { f.Period.Key, f.Period.From, f.Period.To, f.Period.TimeZone }, filters = query, rows, people = f.People.Count }, ct);
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new AnalyticsExportDto(name, "text/csv", content, rows);
    }

    // ---- loading ------------------------------------------------------------------------------------------

    private sealed record Person(Guid Id, string Name, bool IsSchedulable);
    private sealed record TicketMeta(
        Guid Id, string Reference, string? Title, Guid? ClientId, string? ClientName, string SourceKey, string SourceName, string? Priority,
        TicketOrigin Origin, bool Visible, bool Finished, DateTimeOffset? FinishedAt, DateTimeOffset? DueAt, string Status)
    {
        public string ClientKey => !Visible ? "hidden" : ClientId?.ToString() ?? "none";
        public string ClientLabel => !Visible ? HiddenWork : ClientName ?? NoClient;
        public string PriorityKey => string.IsNullOrWhiteSpace(Priority) ? "none" : Priority!;
        public string PriorityLabel => string.IsNullOrWhiteSpace(Priority) ? NotSet : Priority!;
    }
    private sealed record Alloc(Guid Id, Guid AppUserId, Guid TicketId, DateOnly Date, int Minutes, bool Confirmed, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
    private sealed record Entry(Guid Id, Guid AppUserId, Guid TicketId, DateOnly Date, DateTimeOffset At, int Seconds, bool Billable, string? WorkType, TimeEntrySyncStatus Sync);
    private sealed record Live(Guid Id, Guid AppUserId, Guid TicketId, DateOnly Date, DateTimeOffset StartedAt, int Seconds, WorkSessionStatus Status);
    private sealed record Done(Guid TicketId, Guid CreditUserId, DateTimeOffset FinishedAt, DateOnly Date);
    private sealed record OpenWork(Guid TicketId, Guid HolderId, DateTimeOffset? DueAt, bool Unscheduled, string Status);
    private sealed record TicketRow(Guid Id, string? Number, ProviderType? Provider, string? External, string Title, Guid? ClientId, Guid? Conn, string? Priority,
        TicketOrigin Origin, string Status, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt, DateTimeOffset? DueAt);

    /// <summary>Everything one request needs, read once.</summary>
    private sealed class Facts
    {
        public required AnalyticsPeriodDto Period;
        public required TimeZoneInfo OrgZone;
        public required DateTimeOffset Now;
        public required DateOnly Today;
        public required IReadOnlyList<DateOnly> Dates;
        public required List<Person> People;
        public required Dictionary<Guid, List<string>> TeamsOf;
        public required Dictionary<Guid, List<Guid>> TeamIdsOf;
        public required Dictionary<Guid, string> TeamNames;
        public required WorkforceCalendar Calendar;
        /// <summary>Usable minutes per schedulable person-day with a schedule; absent = 0.</summary>
        public required Dictionary<(Guid, DateOnly), int> Capacity;
        public required List<Alloc> Allocations;
        public required List<Entry> Entries;
        public required List<Live> Lives;
        public required List<Done> Completed;
        public List<OpenWork>? Open;
        public WorkNowDto? NowDto;
        public required Dictionary<Guid, TicketMeta> Tickets;
        public required List<SyncFreshnessDto> Sync;
        public required ActualKindFilter Kind;
        public required bool SeesOthers;
        public required bool CanExport;
        public required int WithoutSchedule;
        public required int NotOffered;
        public required int FinishedWithoutDate;

        public int? CapacityOf(Person p, DateOnly d) => !p.IsSchedulable ? null : Capacity.GetValueOrDefault((p.Id, d), 0);
        public string ZoneOf(Guid appUserId, DateOnly date) => Calendar.ZoneId(appUserId, date);
    }

    /// <summary>Planned work never read: the dashboard takes planned minutes from the allocations themselves, and only capacity from the calendar.</summary>
    private sealed class NoAllocations : IWorkAllocationReader
    {
        public static readonly NoAllocations Instance = new();
        public Task<IReadOnlyList<AllocatedSpan>> ForAsync(Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AllocatedSpan>>([]);
    }

    private async Task<Facts> LoadAsync(Guid callerId, AnalyticsQuery q, bool withNow, CancellationToken ct)
    {
        var orgZoneId = access.OrganizationTimeZone();
        var orgZone = TimeZones.Resolve(orgZoneId);
        var now = clock.GetUtcNow();
        var today = WorkforceCalendar.LocalDate(now, orgZone);
        var period = ResolvePeriod(q, today, orgZoneId);
        var dates = new List<DateOnly>(period.Days);
        for (var d = period.From; d <= period.To; d = d.AddDays(1)) dates.Add(d);

        // Who: the caller's scope, narrowed; a person outside it is "not found", the same as nobody.
        var staff = access.Narrow((await access.VisibleStaffAsync(callerId, ct)).Where(u => u.IsActive), q.TeamId, q.DepartmentId, null, true);
        if (q.AppUserId is { } one) staff = staff.Where(u => u.Id == one);
        var people = (await staff.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(MaxPeople + 1)
                .Select(u => new { u.Id, u.DisplayName, u.IsSchedulable }).ToListAsync(ct))
            .Select(u => new Person(u.Id, u.DisplayName, u.IsSchedulable)).ToList();
        if (q.AppUserId is not null && people.Count == 0) throw new NotFoundException("Person");
        if (people.Count > MaxPeople) throw new ValidationFailedException($"That is more than {MaxPeople} people. Choose a team or a department to narrow it down.");
        var ids = people.Select(p => p.Id).ToList();

        // What work: client, source and priority filters, validated against the organization before use.
        var filter = await TicketFilterAsync(q, ct);

        var teamRows = ids.Count == 0 ? [] : await db.UserTeams.AsNoTracking()
            .Join(db.Teams.AsNoTracking(), m => m.TeamId, t => t.Id, (m, t) => new { m.AppUserId, t.Id, t.Name })
            .Where(x => ids.Contains(x.AppUserId)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var teamsOf = teamRows.GroupBy(x => x.AppUserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());
        var teamIdsOf = teamRows.GroupBy(x => x.AppUserId).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var teamNames = teamRows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);

        // Capacity exactly as Phase 2 computes it, over the period, in a fixed number of queries.
        var calendar = await WorkforceCalendar.LoadAsync(db, NoAllocations.Instance, callerId, orgZoneId, ids, period.From, period.To, null, ct);
        var cap = new Dictionary<(Guid, DateOnly), int>();
        var withoutSchedule = 0;
        foreach (var p in people)
        {
            if (!calendar.HasSchedule(p.Id)) { withoutSchedule++; continue; }
            if (!p.IsSchedulable) continue;
            foreach (var input in calendar.InputsFor(p.Id))
            {
                if (input.Date < period.From || input.Date > period.To) continue;
                cap[(p.Id, input.Date)] = CapacityCalculator.ForDay(input).UsableMinutes;
            }
        }

        DateOnly DateOf(Guid person, DateTimeOffset at)
            => WorkforceCalendar.LocalDate(at, calendar.Zone(calendar.ZoneId(person, WorkforceCalendar.LocalDate(at, TimeZoneInfo.Utc))));
        bool InRange(DateOnly d) => d >= period.From && d <= period.To;

        // Instants wide enough for any zone's version of these dates; rows are then placed on their person-day.
        var lo = new DateTimeOffset(period.From.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var hi = new DateTimeOffset(period.To.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var allocations = new List<Alloc>();
        var entries = new List<Entry>();
        var lives = new List<Live>();
        var completed = new List<Done>();
        List<OpenWork>? open = null;
        var finishedWithoutDate = 0;
        if (ids.Count > 0)
        {
            var aq = db.WorkAllocations.AsNoTracking()
                .Where(a => ids.Contains(a.AppUserId) && a.StartsAt >= lo && a.StartsAt < hi && a.Status != WorkAllocationStatus.Cancelled);
            if (filter.Ids is { } allowedA) aq = aq.Where(a => allowedA.Contains(a.TicketId));
            allocations = (await aq.Select(a => new { a.Id, a.AppUserId, a.TicketId, a.StartsAt, a.EndsAt, a.PlannedMinutes, a.Status }).ToListAsync(ct))
                .Select(a => new Alloc(a.Id, a.AppUserId, a.TicketId, DateOf(a.AppUserId, a.StartsAt), a.PlannedMinutes, a.Status == WorkAllocationStatus.Planned, a.StartsAt, a.EndsAt))
                .Where(a => InRange(a.Date)).ToList();

            // Every portal entry, whatever its sync state: rejected time is still work. PSA-side time has no row here.
            var eq = db.TicketTimeEntries.AsNoTracking()
                .Where(e => e.AppUserId != null && ids.Contains(e.AppUserId.Value) && e.EntryDate >= lo && e.EntryDate < hi);
            if (filter.Ids is { } allowedE) eq = eq.Where(e => allowedE.Contains(e.TicketId));
            entries = (await eq.Select(e => new { e.Id, AppUserId = e.AppUserId!.Value, e.TicketId, e.EntryDate, e.Hours, e.Billable, e.WorkTypeLabel, e.SyncStatus }).ToListAsync(ct))
                .Select(e => new Entry(e.Id, e.AppUserId, e.TicketId, DateOf(e.AppUserId, e.EntryDate), e.EntryDate, (int)Math.Round(e.Hours * 3600m, MidpointRounding.AwayFromZero), e.Billable, Blank(e.WorkTypeLabel), e.SyncStatus))
                .Where(e => InRange(e.Date)).ToList();

            // Live clocks: not entries yet, counted by the server's clock, on the day they started.
            var sq = db.WorkSessions.AsNoTracking().Include(s => s.Segments)
                .Where(s => ids.Contains(s.AppUserId) && s.TimeEntryId == null && (s.Status == WorkSessionStatus.Active || s.Status == WorkSessionStatus.Paused)
                            && s.StartedAt >= lo && s.StartedAt < hi);
            if (filter.Ids is { } allowedS) sq = sq.Where(s => allowedS.Contains(s.TicketId));
            lives = (await sq.ToListAsync(ct))
                .Select(s => new Live(s.Id, s.AppUserId, s.TicketId, DateOf(s.AppUserId, s.StartedAt), s.StartedAt, Elapsed(s, now), s.Status))
                .Where(s => InRange(s.Date)).ToList();

            // Finished work: dated in the organization's zone, credited to one person.
            var (cLo, cHi) = StaffReportPeriods.UtcBounds(period.From, period.To, orgZone);
            var cq = filter.Apply(db.Tickets.AsNoTracking().Where(TicketStatusRules.Resolved()))
                .Where(t => (t.ResolvedAt ?? t.ClosedAt) >= cLo && (t.ResolvedAt ?? t.ClosedAt) <= cHi)
                .Where(t => (t.ResolvedByAppUserId != null && ids.Contains(t.ResolvedByAppUserId.Value))
                            || (t.ResolvedByAppUserId == null && t.AssignedAppUserId != null && ids.Contains(t.AssignedAppUserId.Value))
                            || (t.ResolvedByAppUserId == null && t.AssignedAppUserId == null && t.AssignedTechnicianExternalId != null && t.PsaConnectionId != null));
            var done = await cq.Select(t => new { t.Id, t.ResolvedByAppUserId, t.AssignedAppUserId, t.AssignedTechnicianExternalId, t.PsaConnectionId, FinishedAt = (t.ResolvedAt ?? t.ClosedAt)!.Value }).ToListAsync(ct);
            var links = await PsaLinks.LoadAsync(db, ct);
            var idSet = ids.ToHashSet();
            foreach (var d in done)
            {
                var credit = d.ResolvedByAppUserId ?? d.AssignedAppUserId ?? links.UserFor(d.PsaConnectionId, d.AssignedTechnicianExternalId);
                if (credit is { } c && idSet.Contains(c)) completed.Add(new Done(d.Id, c, d.FinishedAt, WorkforceCalendar.LocalDate(d.FinishedAt, orgZone)));
            }
            finishedWithoutDate = await filter.Apply(db.Tickets.AsNoTracking().Where(TicketStatusRules.Resolved()))
                .CountAsync(t => t.ResolvedAt == null && t.ClosedAt == null
                                 && ((t.ResolvedByAppUserId != null && ids.Contains(t.ResolvedByAppUserId.Value)) || (t.ResolvedByAppUserId == null && t.AssignedAppUserId != null && ids.Contains(t.AssignedAppUserId.Value))), ct);

            if (withNow)
            {
                var oq = filter.Apply(db.Tickets.AsNoTracking().Where(TicketStatusRules.Open()))
                    .Where(t => t.AssignedAppUserId != null && ids.Contains(t.AssignedAppUserId.Value));
                open = (await oq.Select(t => new
                {
                    t.Id, Holder = t.AssignedAppUserId!.Value, t.SlaDueAt, t.PortalStatus,
                    Unscheduled = !db.WorkAllocations.Any(a => a.TicketId == t.Id && a.EndsAt > now && (a.Status == WorkAllocationStatus.Planned || a.Status == WorkAllocationStatus.Tentative)),
                }).ToListAsync(ct)).Select(t => new OpenWork(t.Id, t.Holder, t.SlaDueAt, t.Unscheduled, t.PortalStatus)).ToList();
            }
        }

        // The tickets everything points at: what they are, who may open them.
        var ticketIds = allocations.Select(a => a.TicketId).Concat(entries.Select(e => e.TicketId)).Concat(lives.Select(l => l.TicketId))
            .Concat(completed.Select(c => c.TicketId)).Concat(open?.Select(o => o.TicketId) ?? []).Distinct().ToList();
        var metas = await TicketMetaAsync(callerId, ticketIds, ct);

        var connections = await db.PsaConnections.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new { c.Name, c.LastSuccessfulSyncAt }).ToListAsync(ct);

        var facts = new Facts
        {
            Period = period, OrgZone = orgZone, Now = now, Today = today, Dates = dates, People = people,
            TeamsOf = teamsOf, TeamIdsOf = teamIdsOf, TeamNames = teamNames, Calendar = calendar, Capacity = cap,
            Allocations = allocations, Entries = entries, Lives = lives, Completed = completed, Open = open, Tickets = metas,
            Sync = connections.Select(c => new SyncFreshnessDto(c.Name, c.LastSuccessfulSyncAt)).ToList(),
            Kind = q.Kind, SeesOthers = await SeesOthersAsync(callerId, ct), CanExport = await CanExportAsync(callerId, ct),
            WithoutSchedule = withoutSchedule, NotOffered = people.Count(p => !p.IsSchedulable), FinishedWithoutDate = finishedWithoutDate,
        };
        if (open is not null)
        {
            var soon = now.AddHours(TicketStatusRules.DueSoonHours);
            var week = now.AddDays(7);
            facts.NowDto = new WorkNowDto(now, open.Count, open.Count(o => o.Unscheduled), open.Count(o => o.DueAt < now),
                open.Count(o => o.DueAt is { } d && d >= now && WorkforceCalendar.LocalDate(d, orgZone) == today),
                open.Count(o => o.DueAt is { } d && d >= now && d <= soon),
                open.Count(o => o.Unscheduled && o.DueAt is { } d && d <= week));
        }
        return facts;
    }

    private sealed record TicketFilter(IQueryable<Guid>? Ids, Func<IQueryable<Ticket>, IQueryable<Ticket>> Apply);

    /// <summary>The client, source and priority filters as a query over tickets; an id that is not this organization's is "not found".</summary>
    private async Task<TicketFilter> TicketFilterAsync(AnalyticsQuery q, CancellationToken ct)
    {
        var predicates = new List<Func<IQueryable<Ticket>, IQueryable<Ticket>>>();
        if (q.ClientId is { } client)
        {
            if (!await db.ClientCompanies.AnyAsync(c => c.Id == client, ct)) throw new NotFoundException("Client");
            predicates.Add(t => t.Where(x => x.ClientCompanyId == client));
        }
        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            var source = q.Source.Trim().ToLowerInvariant();
            if (source.StartsWith("psa:", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(source[4..], out var connection)) throw new ValidationFailedException("The source is not a PSA connection id.");
                if (!await db.PsaConnections.AnyAsync(c => c.Id == connection, ct)) throw new NotFoundException("Connection");
                predicates.Add(t => t.Where(x => x.PsaConnectionId == connection));
            }
            else predicates.Add(source switch
            {
                "client" => t => t.Where(x => x.Origin == TicketOrigin.Psa),
                "internal" => t => t.Where(x => x.Origin == TicketOrigin.Internal),
                "monitoring" => t => t.Where(x => x.Origin == TicketOrigin.Rmm),
                _ => throw new ValidationFailedException("Unknown source. Use psa:{connectionId}, client, internal or monitoring."),
            });
        }
        if (!string.IsNullOrWhiteSpace(q.Priority))
        {
            var priority = q.Priority.Trim().ToUpperInvariant();
            if (priority.Length > 40) throw new ValidationFailedException("The priority filter is too long.");
            predicates.Add(t => t.Where(x => x.PortalPriority != null && x.PortalPriority.ToUpper() == priority));
        }
        if (predicates.Count == 0) return new TicketFilter(null, t => t);
        IQueryable<Ticket> Apply(IQueryable<Ticket> t) { foreach (var p in predicates) t = p(t); return t; }
        return new TicketFilter(Apply(db.Tickets.AsNoTracking()).Select(t => t.Id), Apply);
    }

    private async Task<Dictionary<Guid, TicketMeta>> TicketMetaAsync(Guid callerId, List<Guid> ticketIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, TicketMeta>();
        if (ticketIds.Count == 0) return result;
        var connections = await db.PsaConnections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var visibleQuery = await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct);
        var rows = new List<TicketRow>();
        var visible = new HashSet<Guid>();
        foreach (var chunk in ticketIds.Chunk(IdChunk))
        {
            var part = chunk.ToList();
            rows.AddRange((await db.Tickets.AsNoTracking().Where(t => part.Contains(t.Id))
                .Select(t => new { t.Id, t.Number, t.Provider, t.ExternalTicketId, t.Title, t.ClientCompanyId, t.PsaConnectionId, t.PortalPriority, t.Origin, t.PortalStatus, t.ResolvedAt, t.ClosedAt, t.SlaDueAt })
                .ToListAsync(ct))
                .Select(t => new TicketRow(t.Id, t.Number, t.Provider, t.ExternalTicketId, t.Title, t.ClientCompanyId, t.PsaConnectionId, t.PortalPriority, t.Origin, t.PortalStatus, t.ResolvedAt, t.ClosedAt, t.SlaDueAt)));
            visible.UnionWith(await visibleQuery.Where(t => part.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct));
        }
        var clientIds = rows.Where(r => r.ClientId != null).Select(r => r.ClientId!.Value).Distinct().ToList();
        var clients = clientIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.ClientCompanies.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        foreach (var r in rows)
        {
            var (sourceKey, sourceName) = r.Origin switch
            {
                TicketOrigin.Internal => ("internal", "Team boards"),
                TicketOrigin.Rmm => ("monitoring", "Monitoring"),
                _ => r.Conn is { } c ? ("psa:" + c, connections.GetValueOrDefault(c) ?? WorkPlanService.Source(r.Origin, r.Provider)) : ("client", WorkPlanService.Source(r.Origin, r.Provider)),
            };
            var finished = TicketStatusRules.Finished(r.Status);
            result[r.Id] = new TicketMeta(r.Id, WorkPlanService.Reference(r.Number, r.Provider, r.External), r.Title, r.ClientId,
                r.ClientId is { } cid ? clients.GetValueOrDefault(cid) : null, sourceKey, sourceName, Blank(r.Priority), r.Origin,
                visible.Contains(r.Id), finished, finished ? r.ResolvedAt ?? r.ClosedAt : null, r.DueAt, r.Status);
        }
        return result;
    }

    private async Task<bool> SeesOthersAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.ScheduleView, ct)).Scope is PermissionScope.All or PermissionScope.Department or PermissionScope.Team;

    private async Task<bool> CanExportAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.WorkforceAnalyticsExport, ct)).Scope == PermissionScope.All;

    // ---- tallying ------------------------------------------------------------------------------------------

    /// <summary>The figures for one grouping, accumulated; <see cref="ToDto"/> derives the ratios once.</summary>
    private sealed class Tally
    {
        public bool HasCapacity;
        public long Capacity, Planned, Tentative;
        public long Actual, PlannedActual, Reactive, Live, Billable, Client, Internal, Monitoring;
        public long AbsVariance, PlannedCompared;
        public int Compared;
        public long Over;
        public int OverDays;
        public readonly HashSet<Guid> Completed = [], Items = [], ReactiveItems = [], People = [];

        public void AddCapacity(int minutes) { HasCapacity = true; Capacity += minutes; }
        public void AddPlanned(int planned, int tentative) { Planned += planned; Tentative += tentative; }
        public void AddOver(int minutes) { if (minutes <= 0) return; Over += minutes; OverDays++; }
        public void AddActual(Guid person, Guid ticket, int seconds, bool planned, int live, int billable, TicketOrigin origin)
        {
            if (seconds <= 0) return;
            Actual += seconds;
            if (planned) PlannedActual += seconds; else { Reactive += seconds; ReactiveItems.Add(ticket); }
            Live += live;
            Billable += billable;
            switch (origin) { case TicketOrigin.Internal: Internal += seconds; break; case TicketOrigin.Rmm: Monitoring += seconds; break; default: Client += seconds; break; }
            Items.Add(ticket);
            People.Add(person);
        }
        public void AddVariance(int plannedMinutes, int actualSeconds)
        {
            if (plannedMinutes <= 0) return;
            var actualMinutes = (int)Math.Round(actualSeconds / 60.0, MidpointRounding.AwayFromZero);
            AbsVariance += Math.Abs(actualMinutes - plannedMinutes);
            PlannedCompared += plannedMinutes;
            Compared++;
        }

        public AnalyticsFiguresDto ToDto()
        {
            var actualMinutes = (int)Math.Round(Actual / 60.0, MidpointRounding.AwayFromZero);
            int? cap = HasCapacity ? (int)Capacity : null;
            return new AnalyticsFiguresDto(
                cap, (int)Planned, (int)Tentative,
                (int)Actual, (int)PlannedActual, (int)Reactive, (int)Live, (int)Billable, (int)Client, (int)Internal, (int)Monitoring,
                cap is > 0 ? Pct(Planned, cap.Value) : null, cap is > 0 ? Pct(actualMinutes, cap.Value) : null, Actual > 0 ? Pct(Reactive, Actual) : null,
                Planned > 0 ? actualMinutes - (int)Planned : null, Planned > 0 ? Pct(actualMinutes - Planned, Planned) : null,
                (int)AbsVariance, PlannedCompared > 0 ? Pct(AbsVariance, PlannedCompared) : null, Compared,
                (int)Over, OverDays,
                Completed.Count, Items.Count, ReactiveItems.Count);
        }
    }

    private sealed class ItemTally(Guid ticketId)
    {
        public Guid TicketId = ticketId;
        public int PlannedMinutes, TentativeMinutes, ActualSeconds, ReactiveSeconds, Entries;
        public readonly HashSet<DateOnly> Days = [];
    }

    /// <summary>Every grouping's tally, from one pass over the facts. Rows of any table add up to the cards because they are these same rows.</summary>
    private sealed class Tallies
    {
        public readonly Tally Total = new();
        private readonly Dictionary<Guid, Tally> _people = [];
        public readonly Dictionary<Guid, Tally> Teams = [];
        public readonly Dictionary<string, (string Name, Tally Tally)> Clients = [], Sources = [], Priorities = [], WorkTypes = [];
        private readonly Dictionary<DateOnly, Tally> _days = [];
        public readonly Dictionary<(Guid, DateOnly), Tally> Cells = [];
        public readonly Dictionary<Guid, ItemTally> Items = [];

        public Tally Person(Guid id) => Get(_people, id);
        public Tally Day(DateOnly d) => Get(_days, d);
        private static Tally Get<TKey>(Dictionary<TKey, Tally> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var t)) map[key] = t = new Tally();
            return t;
        }
        private static Tally Get(Dictionary<string, (string Name, Tally Tally)> map, string key, string name)
        {
            if (!map.TryGetValue(key, out var t)) map[key] = t = (name, new Tally());
            return t.Tally;
        }

        public static Tallies Over(Facts f)
        {
            var t = new Tallies();
            var teamsOf = f.TeamIdsOf;
            IEnumerable<Tally> PersonDayTallies(Guid p, DateOnly d)
            {
                yield return t.Total;
                yield return t.Person(p);
                foreach (var team in teamsOf.GetValueOrDefault(p) ?? []) yield return Get(t.Teams, team);
                yield return t.Day(d);
                yield return Get(t.Cells, (p, d));
            }

            // Planned per person-day and per person-ticket-day, from the allocations themselves.
            var dayPlan = new Dictionary<(Guid, DateOnly), (int Planned, int Tentative)>();
            var itemPlan = new Dictionary<(Guid, Guid, DateOnly), (int Planned, int Tentative)>();
            foreach (var a in f.Allocations)
            {
                var dk = (a.AppUserId, a.Date);
                var ik = (a.AppUserId, a.TicketId, a.Date);
                dayPlan[dk] = Add(dayPlan.GetValueOrDefault(dk), a);
                itemPlan[ik] = Add(itemPlan.GetValueOrDefault(ik), a);
            }
            static (int, int) Add((int Planned, int Tentative) x, Alloc a) => a.Confirmed ? (x.Planned + a.Minutes, x.Tentative) : (x.Planned, x.Tentative + a.Minutes);

            // Capacity and planned per person-day: the organization, the person, their teams, the day, the heatmap cell.
            foreach (var p in f.People)
            {
                t.Person(p.Id);
                foreach (var d in f.Dates)
                {
                    var cap = f.CapacityOf(p, d);
                    var plan = dayPlan.GetValueOrDefault((p.Id, d));
                    foreach (var tally in PersonDayTallies(p.Id, d))
                    {
                        if (cap is { } c) tally.AddCapacity(c);
                        tally.AddPlanned(plan.Planned, plan.Tentative);
                        if (cap is { } c2) tally.AddOver(plan.Planned - c2);
                    }
                }
            }

            // Actual per person-ticket-day: entries plus live clocks; planned or reactive by whether that day had a plan for that ticket.
            var itemActual = new Dictionary<(Guid, Guid, DateOnly), (int Seconds, int Live, int Billable, int Entries)>();
            foreach (var e in f.Entries)
            {
                var k = (e.AppUserId, e.TicketId, e.Date);
                var x = itemActual.GetValueOrDefault(k);
                itemActual[k] = (x.Seconds + e.Seconds, x.Live, x.Billable + (e.Billable ? e.Seconds : 0), x.Entries + 1);
            }
            foreach (var l in f.Lives)
            {
                var k = (l.AppUserId, l.TicketId, l.Date);
                var x = itemActual.GetValueOrDefault(k);
                itemActual[k] = (x.Seconds + l.Seconds, x.Live + l.Seconds, x.Billable, x.Entries);
            }

            foreach (var key in itemPlan.Keys.Union(itemActual.Keys))
            {
                var (p, ticketId, d) = key;
                if (!f.Tickets.TryGetValue(ticketId, out var m)) continue;
                var plan = itemPlan.GetValueOrDefault(key);
                var planned = itemPlan.ContainsKey(key);
                var actual = itemActual.GetValueOrDefault(key);
                var counted = f.Kind switch { ActualKindFilter.Planned => planned, ActualKindFilter.Reactive => !planned, _ => true };
                var seconds = counted ? actual.Seconds : 0;

                // Planned minutes reach the client, source and priority rows here (they have no person-day of their own).
                foreach (var g in new[] { Get(t.Clients, m.ClientKey, m.ClientLabel), Get(t.Sources, m.SourceKey, m.SourceName), Get(t.Priorities, m.PriorityKey, m.PriorityLabel) })
                {
                    g.AddPlanned(plan.Planned, plan.Tentative);
                    g.AddActual(p, ticketId, seconds, planned, counted ? actual.Live : 0, counted ? actual.Billable : 0, m.Origin);
                    g.AddVariance(plan.Planned, actual.Seconds);
                }
                foreach (var tally in PersonDayTallies(p, d))
                {
                    tally.AddActual(p, ticketId, seconds, planned, counted ? actual.Live : 0, counted ? actual.Billable : 0, m.Origin);
                    tally.AddVariance(plan.Planned, actual.Seconds);
                }
                if (!t.Items.TryGetValue(ticketId, out var item)) t.Items[ticketId] = item = new ItemTally(ticketId);
                item.PlannedMinutes += plan.Planned;
                item.TentativeMinutes += plan.Tentative;
                item.ActualSeconds += seconds;
                if (!planned) item.ReactiveSeconds += seconds;
                item.Entries += counted ? actual.Entries : 0;
                if (seconds > 0 || plan.Planned > 0 || plan.Tentative > 0) item.Days.Add(d);
            }

            // Work types are a property of the hour, not the ticket.
            foreach (var e in f.Entries)
            {
                var planned = itemPlan.ContainsKey((e.AppUserId, e.TicketId, e.Date));
                if (f.Kind == ActualKindFilter.Planned && !planned || f.Kind == ActualKindFilter.Reactive && planned) continue;
                if (!f.Tickets.TryGetValue(e.TicketId, out var m)) continue;
                Get(t.WorkTypes, e.WorkType ?? "none", e.WorkType ?? NotSet).AddActual(e.AppUserId, e.TicketId, e.Seconds, planned, 0, e.Billable ? e.Seconds : 0, m.Origin);
            }
            foreach (var l in f.Lives)
            {
                var planned = itemPlan.ContainsKey((l.AppUserId, l.TicketId, l.Date));
                if (f.Kind == ActualKindFilter.Planned && !planned || f.Kind == ActualKindFilter.Reactive && planned) continue;
                if (!f.Tickets.TryGetValue(l.TicketId, out var m)) continue;
                Get(t.WorkTypes, "live", RunningClock).AddActual(l.AppUserId, l.TicketId, l.Seconds, planned, l.Seconds, 0, m.Origin);
            }

            // Completed: one ticket, once, credited to one person, dated in the organization's zone.
            foreach (var c in f.Completed)
            {
                if (!f.Tickets.TryGetValue(c.TicketId, out var m)) continue;
                t.Total.Completed.Add(c.TicketId);
                t.Person(c.CreditUserId).Completed.Add(c.TicketId);
                foreach (var team in teamsOf.GetValueOrDefault(c.CreditUserId) ?? []) Get(t.Teams, team).Completed.Add(c.TicketId);
                if (c.Date >= f.Period.From && c.Date <= f.Period.To) t.Day(c.Date).Completed.Add(c.TicketId);
                Get(t.Clients, m.ClientKey, m.ClientLabel).Completed.Add(c.TicketId);
                Get(t.Sources, m.SourceKey, m.SourceName).Completed.Add(c.TicketId);
                Get(t.Priorities, m.PriorityKey, m.PriorityLabel).Completed.Add(c.TicketId);
            }
            return t;
        }
    }

    // ---- shaping -------------------------------------------------------------------------------------------

    private static AnalyticsPersonDto PersonDto(Facts f, Person p, Tally t)
        => new(p.Id, p.Name, f.TeamsOf.GetValueOrDefault(p.Id) ?? [], p.IsSchedulable, f.Calendar.HasSchedule(p.Id), f.ZoneOf(p.Id, f.Period.From), t.ToDto());

    private static List<AnalyticsGroupDto> Groups(Dictionary<string, (string Name, Tally Tally)> groups, Facts f)
        => groups.Where(g => g.Value.Tally.Actual > 0 || g.Value.Tally.Planned > 0 || g.Value.Tally.Tentative > 0 || g.Value.Tally.Completed.Count > 0)
            .Select(g => new AnalyticsGroupDto(g.Key, g.Value.Name, g.Value.Tally.People.Count, g.Value.Tally.ToDto()))
            .OrderByDescending(g => g.Figures.ActualSeconds).ThenByDescending(g => g.Figures.PlannedMinutes).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static HeatmapDto Heatmap(Facts f, Tallies t)
    {
        if (f.Period.Days > HeatmapMaxDays)
            return new HeatmapDto(f.Dates, [], f.People.Count, false, $"The heatmap shows at most {HeatmapMaxDays} days; choose a shorter period to see it.");
        var shown = f.People.Take(HeatmapMaxPeople).ToList();
        var rows = shown.Select(p => new HeatmapRowDto(p.Id, p.Name, f.Dates.Select(d =>
        {
            var cell = t.Cells.GetValueOrDefault((p.Id, d));
            return new HeatmapCellDto(f.CapacityOf(p, d), (int)(cell?.Planned ?? 0), (int)(cell?.Actual ?? 0));
        }).ToList())).ToList();
        return new HeatmapDto(f.Dates, rows, f.People.Count, f.People.Count > shown.Count, null);
    }

    private static List<string> Notes(Facts f, Tallies t)
    {
        var notes = new List<string>();
        if (f.Period.EndsInFuture) notes.Add("This period has not ended: its later days hold capacity but no recorded time yet, so utilization reads low until it is over.");
        if (t.Total.Live > 0) notes.Add($"{Duration(t.Total.Live)} on clocks still running is included in today's actual time.");
        if (f.WithoutSchedule > 0) notes.Add($"{People(f.WithoutSchedule)} no working schedule, so their capacity is 0 and their utilization is N/A.");
        if (f.NotOffered > 0) notes.Add($"{People(f.NotOffered)} not offered for planned work; their recorded time counts, their capacity does not.");
        if (f.FinishedWithoutDate > 0) notes.Add($"{f.FinishedWithoutDate} finished work item{(f.FinishedWithoutDate == 1 ? " has" : "s have")} no completion date and {(f.FinishedWithoutDate == 1 ? "is" : "are")} in no period.");
        if (f.People.Any(p => (f.TeamIdsOf.GetValueOrDefault(p.Id)?.Count ?? 0) > 1)) notes.Add("A person in more than one team appears under each of them; the organization total counts them once.");
        notes.Add("Time entered directly in a PSA has no portal row and is not in any day's actual time; it reaches the ticket's totals only.");
        if (f.Kind != ActualKindFilter.All) notes.Add($"Only {(f.Kind == ActualKindFilter.Planned ? "planned" : "reactive")} work's recorded time is counted; planned minutes and capacity are unchanged.");
        return notes;
        static string People(int n) => n == 1 ? "1 person has" : $"{n} people have";
    }

    private List<AnalyticsWorkRowDto> Rows(Facts f, AnalyticsWorkKind kind)
    {
        var names = f.People.ToDictionary(p => p.Id, p => p.Name);
        var plannedKeys = f.Allocations.Select(a => (a.AppUserId, a.TicketId, a.Date)).ToHashSet();
        TicketMeta Meta(Guid id) => f.Tickets.TryGetValue(id, out var m) ? m : new TicketMeta(id, HiddenWork, null, null, null, "client", "PSA", null, TicketOrigin.Psa, false, false, null, null, "");
        AnalyticsWorkRowDto Row(string rowKind, Guid? id, DateOnly date, DateTimeOffset? at, Guid? person, TicketMeta m, int? minutes, int? seconds, bool? billable, bool? planned, string? status, DateTimeOffset? due, DateTimeOffset? finished)
            => new(rowKind, id, date, at, person, person is { } p ? names.GetValueOrDefault(p) : null, m.Id, m.Visible ? m.Reference : HiddenWork, m.Visible ? m.Title : null,
                m.Visible ? m.ClientName : null, m.SourceName, m.Priority, m.Visible, minutes, seconds, billable, planned, status, m.Visible ? due : null, finished);

        switch (kind)
        {
            case AnalyticsWorkKind.Actual:
            case AnalyticsWorkKind.PlannedActual:
            case AnalyticsWorkKind.Reactive:
            {
                bool Wanted(bool planned) => kind switch
                {
                    AnalyticsWorkKind.PlannedActual => planned,
                    AnalyticsWorkKind.Reactive => !planned,
                    _ => f.Kind switch { ActualKindFilter.Planned => planned, ActualKindFilter.Reactive => !planned, _ => true },
                };
                var rows = new List<AnalyticsWorkRowDto>();
                foreach (var e in f.Entries)
                {
                    var planned = plannedKeys.Contains((e.AppUserId, e.TicketId, e.Date));
                    if (!Wanted(planned)) continue;
                    rows.Add(Row("entry", e.Id, e.Date, e.At, e.AppUserId, Meta(e.TicketId), (int)Math.Round(e.Seconds / 60.0, MidpointRounding.AwayFromZero), e.Seconds, e.Billable, planned,
                        e.Sync switch { TimeEntrySyncStatus.Synced => "Recorded", TimeEntrySyncStatus.Pending => "Not in the PSA yet", _ => "PSA push failed" }, null, null));
                }
                foreach (var l in f.Lives)
                {
                    var planned = plannedKeys.Contains((l.AppUserId, l.TicketId, l.Date));
                    if (!Wanted(planned)) continue;
                    rows.Add(Row("live", l.Id, l.Date, l.StartedAt, l.AppUserId, Meta(l.TicketId), (int)Math.Round(l.Seconds / 60.0, MidpointRounding.AwayFromZero), l.Seconds, null, planned,
                        l.Status == WorkSessionStatus.Active ? "Running" : "Paused", null, null));
                }
                return rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.At).ThenBy(r => r.PersonName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            case AnalyticsWorkKind.Planned:
            case AnalyticsWorkKind.Tentative:
                return f.Allocations.Where(a => a.Confirmed == (kind == AnalyticsWorkKind.Planned))
                    .Select(a => Row("allocation", a.Id, a.Date, a.StartsAt, a.AppUserId, Meta(a.TicketId), a.Minutes, null, null, true, a.Confirmed ? "Planned" : "Tentative", null, null))
                    .OrderByDescending(r => r.Date).ThenByDescending(r => r.At).ThenBy(r => r.PersonName, StringComparer.OrdinalIgnoreCase).ToList();
            case AnalyticsWorkKind.Completed:
                return f.Completed
                    .Select(c => { var m = Meta(c.TicketId); return Row("ticket", c.TicketId, c.Date, c.FinishedAt, c.CreditUserId, m, null, null, null, null, m.Status, null, c.FinishedAt); })
                    .OrderByDescending(r => r.FinishedAt).ThenBy(r => r.Reference, StringComparer.OrdinalIgnoreCase).ToList();
            default:
            {
                var open = f.Open ?? [];
                var now = f.Now;
                open = kind switch
                {
                    AnalyticsWorkKind.Unscheduled => open.Where(o => o.Unscheduled).ToList(),
                    AnalyticsWorkKind.Overdue => open.Where(o => o.DueAt < now).ToList(),
                    _ => open,
                };
                return open.Select(o => { var m = Meta(o.TicketId); return Row("ticket", o.TicketId, f.Today, null, o.HolderId, m, null, null, null, !o.Unscheduled, o.Status, o.DueAt, null); })
                    .OrderBy(r => r.DueAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.Reference, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static double Pct(double part, double whole) => Math.Round(100.0 * part / whole, 2, MidpointRounding.AwayFromZero);

    private static int Elapsed(WorkSession s, DateTimeOffset now)
    {
        var open = s.Segments.FirstOrDefault(x => x.EndedAt == null);
        return s.ActiveSeconds + (open is null ? 0 : Math.Max(0, (int)Math.Floor((now - open.StartedAt).TotalSeconds)));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Duration(long seconds)
    {
        var m = seconds / 60;
        return m >= 60 ? $"{m / 60} h {m % 60:00} m" : $"{m} m";
    }

    private static int PriorityRank(string p) => p.ToUpperInvariant() switch
    {
        "CRITICAL" => 0, "URGENT" => 1, "HIGH" => 2, "NORMAL" => 3, "MEDIUM" => 4, "LOW" => 5, _ => 9,
    };
}
