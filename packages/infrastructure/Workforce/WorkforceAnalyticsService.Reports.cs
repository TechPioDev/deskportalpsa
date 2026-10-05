using System.Globalization;
using System.Text;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// The report center (Phase 8). A report is a definition in code and one builder that returns columns
/// and rows from the analytics and insights reads above; the preview and the export call the same
/// builder with the same query, so an exported total cannot differ from the one previewed. Nothing is
/// stored: there is no saved report, generated file or export job whose id could be guessed.
/// </summary>
public sealed partial class WorkforceAnalyticsService
{
    public const int ReportPreviewRows = 500;

    private static readonly string[] WorkFilters = ["team", "department", "person", "client", "source", "priority"];

    /// <summary>Every report there is, in the order the catalogue shows them.</summary>
    public static readonly IReadOnlyList<ReportDefinitionDto> ReportDefinitions =
    [
        new("workforce-utilization", "Workforce", "Workforce utilization",
            "Capacity, planned work and actual work per person, with scheduled and capacity utilization.", "history", WorkFilters, false),
        new("technician-work-summary", "Workforce", "Technician work summary",
            "Actual work per person: planned and reactive, client, internal and monitoring, billable, completed work.", "history", WorkFilters, false),
        new("team-work-summary", "Workforce", "Team work summary",
            "Capacity, planned, actual and reactive work and completed work per team.", "history", WorkFilters, false),
        new("capacity-demand", "Capacity", "Capacity and demand by day",
            "Each day ahead: capacity, confirmed and tentative work, and the unscheduled effort due that day.", "forecast", WorkFilters, false),
        new("future-capacity", "Capacity", "Future capacity by technician",
            "Each person's capacity ahead against confirmed, tentative and estimated unscheduled work, with the gap.", "forecast", WorkFilters, false),
        new("client-workload", "Clients", "Client workload",
            "Actual work per client in a period against the period before, with reactive time and completed work.", "compare", WorkFilters, false),
        new("planned-vs-actual", "Delivery", "Planned against actual by day",
            "Each day: capacity, planned and tentative work, actual work split into planned and reactive, and the variance.", "history", WorkFilters, false),
        new("reactive-work", "Delivery", "Reactive work by week",
            "The last eight weeks: actual work, how much of it was planned and how much reactive, and completed work.", "none", WorkFilters, false),
        new("estimate-variance", "Delivery", "Estimate variance",
            "Planned time against the time recorded on that planned work, by category, client or source.", "compare", [.. WorkFilters, "by"], false),
        new("work-sources", "Delivery", "Work sources",
            "Actual work per source (each PSA, team boards, monitoring) in a period against the period before.", "compare", WorkFilters, false),
        new("operational-quality", "Quality", "Operational quality",
            "The quality signals the records support over completed work, each with what it rests on.", "compare", WorkFilters, false),
        new("integration-health", "Integrations", "Mapping and integration health",
            "Each PSA connection: sync state, how well statuses, priorities, technicians and clients map, and failed records.", "none", [], true),
    ];

    /// <summary>A report as built: every row, before the preview trims it or the export writes it.</summary>
    private sealed record Built(
        AnalyticsPeriodDto? Period, DateTimeOffset GeneratedAt, List<ReportFactDto> Summary, List<ReportColumnDto> Columns, List<IReadOnlyList<object?>> Rows,
        List<string> Notes, IReadOnlyList<SyncFreshnessDto> Sync, string? PersonName = null);

    private static decimal Hours(long minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);
    private static decimal HoursOf(long seconds) => Math.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static ReportColumnDto Text(string key, string label) => new(key, label, "text");
    private static ReportColumnDto Hrs(string key, string label) => new(key, label, "hours");
    private static ReportColumnDto Num(string key, string label) => new(key, label, "count");
    private static ReportColumnDto Share(string key, string label) => new(key, label, "percent");
    private static ReportColumnDto Day(string key, string label) => new(key, label, "date");
    private static ReportFactDto Fact(string label, string value) => new(label, value);
    private static string HoursText(long minutes) => Hours(minutes).ToString("0.##", CultureInfo.InvariantCulture) + " h";
    /// <summary>A signed difference in words. Never a leading + or -: a spreadsheet would read one as the start of a formula.</summary>
    private static string Signed(long minutes, string over, string under) => minutes == 0 ? "0 h" : $"{HoursText(Math.Abs(minutes))} {(minutes > 0 ? over : under)}";

    private static AnalyticsQuery History(InsightsQuery q) => new(q.Period, q.From, q.To, q.TeamId, q.DepartmentId, q.AppUserId, q.ClientId, q.Source, q.Priority);

    public async Task<IReadOnlyList<ReportDefinitionDto>> CatalogueAsync(Guid callerId, CancellationToken ct = default)
    {
        var health = await HoldsAsync(callerId, Permissions.IntegrationHealthView, ct);
        return ReportDefinitions.Where(d => health || !d.NeedsIntegrationHealth).ToList();
    }

    private static ReportDefinitionDto Definition(string key)
        => ReportDefinitions.FirstOrDefault(d => string.Equals(d.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new NotFoundException("Report");

    public async Task<ReportDto> ReportAsync(Guid callerId, string key, InsightsQuery query, CancellationToken ct = default)
    {
        var definition = Definition(key);
        var built = await BuildAsync(callerId, definition, query, ct);
        return new ReportDto(definition, built.Period, built.GeneratedAt, await AppliedAsync(callerId, definition, query, built, ct), built.Summary, built.Columns,
            built.Rows.Take(ReportPreviewRows).ToList(), built.Rows.Count, built.Rows.Count > ReportPreviewRows, built.Notes, built.Sync, await CanExportAsync(callerId, ct));
    }

    public async Task<AnalyticsExportDto> ExportReportAsync(Guid callerId, string key, InsightsQuery query, string format, CancellationToken ct = default)
    {
        // The controller requires the claim; the service checks it again, so no other caller can export without it.
        if (!await CanExportAsync(callerId, ct)) throw new ForbiddenException("Exporting workforce reports needs the workforce.analytics.export permission.");
        var kind = (format ?? "").Trim().ToLowerInvariant();
        if (kind is not ("csv" or "xlsx")) throw new ValidationFailedException("Unknown format. Use csv or xlsx.");
        var definition = Definition(key);
        var built = await BuildAsync(callerId, definition, query, ct);
        if (built.Rows.Count > MaxExportRows) throw new ValidationFailedException($"That is {built.Rows.Count:N0} rows; an export holds at most {MaxExportRows:N0}. Narrow the period or the filters.");
        var applied = await AppliedAsync(callerId, definition, query, built, ct);

        // The file as rows: what it is and what it covers, the summary, then the table. The same for both formats.
        var sheet = new List<IReadOnlyList<object?>> { new object?[] { $"PIO MANAGE: {definition.Title}" } };
        if (built.Period is { } p) sheet.Add(["Period", p.Label, p.TimeZone, Iso(p.From), Iso(p.To)]);
        sheet.Add(["Generated", built.GeneratedAt.ToString("u", CultureInfo.InvariantCulture), "Facts from records; not a performance score."]);
        foreach (var a in applied) sheet.Add(["Filter", a.Label, a.Value]);
        foreach (var s in built.Summary) sheet.Add(["Summary", s.Label, s.Value]);
        sheet.Add([]);
        var head = sheet.Count;
        sheet.Add(built.Columns.Select(c => (object?)(c.Kind == "hours" ? $"{c.Label} (h)" : c.Kind == "percent" ? $"{c.Label} %" : c.Label)).ToList());
        // A figure that does not apply is written as N/A, never as 0: an empty denominator is not a zero.
        sheet.AddRange(built.Rows.Select(r => (IReadOnlyList<object?>)r.Select((cell, i) => cell ?? (built.Columns[i].Kind == "text" ? "" : "N/A")).ToList()));
        if (built.Notes.Count > 0)
        {
            sheet.Add([]);
            sheet.AddRange(built.Notes.Select(n => (IReadOnlyList<object?>)new object?[] { "Note", n }));
        }

        byte[] content;
        string contentType;
        if (kind == "xlsx")
        {
            content = XlsxWriter.Write(definition.Title, sheet, new HashSet<int> { 0, head });
            contentType = XlsxWriter.ContentType;
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var row in sheet) sb.Append(string.Join(',', row.Select(TechnicianReportRenderer.Cell))).Append("\r\n");
            content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            contentType = "text/csv";
        }

        var span = built.Period is { } period ? $"-{Iso(period.From)}-{Iso(period.To)}" : "-" + Iso(DateOnly.FromDateTime(built.GeneratedAt.UtcDateTime));
        // Audited: figures, some about named people, leave the system in a file.
        await audit.WriteAsync("workforce.report.exported", "WorkforceReport", null,
            new { report = definition.Key, format = kind, period = built.Period is { } x ? new { x.Key, x.From, x.To, x.TimeZone } : null, filters = query, rows = built.Rows.Count }, ct);
        return new AnalyticsExportDto($"pio-manage-{definition.Key}{span}.{kind}", contentType, content, built.Rows.Count);
    }

    /// <summary>What the report was asked for, in words: the filter lists the caller is offered, never a name they could not otherwise see.</summary>
    private async Task<List<ReportFactDto>> AppliedAsync(Guid callerId, ReportDefinitionDto definition, InsightsQuery q, Built built, CancellationToken ct)
    {
        var applied = new List<ReportFactDto>();
        if (definition.Filters.Count == 0) return applied;
        if (q.TeamId is not null || q.DepartmentId is not null)
        {
            var groups = await capacity.GroupsAsync(callerId, ct);
            if (q.TeamId is { } team) applied.Add(Fact("Team", groups.Teams.FirstOrDefault(t => t.Id == team)?.Name ?? "A team that is not in your lists"));
            if (q.DepartmentId is { } dept) applied.Add(Fact("Department", groups.Departments.FirstOrDefault(d => d.Id == dept)?.Name ?? "A department that is not in your lists"));
        }
        if (q.AppUserId is not null) applied.Add(Fact("Technician", built.PersonName ?? "One person"));
        if (q.ClientId is { } client)
        {
            var visible = await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct);
            var name = await visible.AnyAsync(t => t.ClientCompanyId == client, ct)
                ? await db.ClientCompanies.AsNoTracking().Where(c => c.Id == client).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
            applied.Add(Fact("Client", name ?? "One client"));
        }
        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            var source = q.Source.Trim().ToLowerInvariant();
            var label = source switch { "client" => "Client work (any PSA)", "internal" => "Team boards", "monitoring" => "Monitoring", _ => null };
            if (label is null && source.StartsWith("psa:", StringComparison.Ordinal) && Guid.TryParse(source[4..], out var connection))
                label = await db.PsaConnections.AsNoTracking().Where(c => c.Id == connection).Select(c => c.Name).FirstOrDefaultAsync(ct);
            applied.Add(Fact("Source", label ?? "One source"));
        }
        if (!string.IsNullOrWhiteSpace(q.Priority)) applied.Add(Fact("Priority", q.Priority.Trim().ToUpperInvariant()));
        if (definition.Filters.Contains("by")) applied.Add(Fact("Grouped by", VarianceBy(q.By)));
        if (applied.Count == 0) applied.Add(Fact("Filters", "None: everyone and all the work you may see"));
        return applied;
    }

    private static string VarianceBy(string? by) => (by ?? "category").Trim().ToLowerInvariant() switch
    {
        "" or "category" => "category",
        "client" => "client",
        "source" => "source",
        _ => throw new ValidationFailedException("Unknown grouping. Use category, client or source."),
    };

    private async Task<Built> BuildAsync(Guid callerId, ReportDefinitionDto definition, InsightsQuery q, CancellationToken ct)
    {
        switch (definition.Key)
        {
            case "workforce-utilization":
            case "technician-work-summary":
            case "team-work-summary":
            case "planned-vs-actual":
                return History(definition.Key, q, await LoadAsync(callerId, History(q), withNow: false, ct));
            case "capacity-demand":
            case "future-capacity":
                return Ahead(definition.Key, await ForecastAsync(callerId, q, ct), q);
            case "integration-health":
                return Health(await HealthAsync(callerId, ct));
            default:
                return Compared(definition.Key, q, await TrendsAsync(callerId, q, ct), await PersonNameAsync(callerId, q, ct));
        }
    }

    private async Task<string?> PersonNameAsync(Guid callerId, InsightsQuery q, CancellationToken ct)
        => q.AppUserId is { } id ? (await access.VisiblePersonAsync(callerId, id, ct)).DisplayName : null;

    // ---- history reports: one Phase 7 load, the Phase 7 tallies ---------------------------------------------------

    private static Built History(string key, InsightsQuery q, Facts f)
    {
        var t = Tallies.Over(f);
        var total = t.Total.ToDto();
        var notes = Notes(f, t);
        var person = q.AppUserId is not null ? f.People.FirstOrDefault()?.Name : null;
        List<ReportFactDto> summary =
        [
            Fact("Capacity", total.CapacityMinutes is { } cap ? HoursText(cap) : "N/A"), Fact("Planned work", HoursText(total.PlannedMinutes)),
            Fact("Actual work", HoursOf(total.ActualSeconds).ToString("0.##", CultureInfo.InvariantCulture) + " h"),
            Fact("Reactive work", HoursOf(total.ReactiveActualSeconds).ToString("0.##", CultureInfo.InvariantCulture) + " h"),
            Fact("Reactive share", Percent(total.ReactiveSharePercent)), Fact("Completed work", total.CompletedWork.ToString(CultureInfo.InvariantCulture)),
        ];
        List<ReportColumnDto> columns;
        List<IReadOnlyList<object?>> rows;
        switch (key)
        {
            case "workforce-utilization":
                columns = [Text("person", "Technician"), Text("teams", "Teams"), Text("offered", "Offered for planned work"), Hrs("capacity", "Capacity"), Hrs("planned", "Planned"),
                    Hrs("tentative", "Tentative"), Hrs("actual", "Actual"), Share("scheduled", "Scheduled utilization"), Share("utilization", "Capacity utilization"), Hrs("over", "Over capacity")];
                rows = f.People.Select(p =>
                {
                    var d = t.Person(p.Id).ToDto();
                    return (IReadOnlyList<object?>)new object?[]
                    {
                        p.Name, string.Join("; ", f.TeamsOf.GetValueOrDefault(p.Id) ?? []), p.IsSchedulable ? "yes" : "no", d.CapacityMinutes is { } c ? Hours(c) : null, Hours(d.PlannedMinutes),
                        Hours(d.TentativeMinutes), HoursOf(d.ActualSeconds), d.ScheduledUtilizationPercent, d.CapacityUtilizationPercent, Hours(d.OverCapacityMinutes),
                    };
                }).ToList();
                break;
            case "technician-work-summary":
                columns = [Text("person", "Technician"), Text("teams", "Teams"), Hrs("actual", "Actual"), Hrs("planned-actual", "Planned actual"), Hrs("reactive", "Reactive"),
                    Share("reactive-share", "Reactive share"), Hrs("client", "Client work"), Hrs("internal", "Internal work"), Hrs("monitoring", "Monitoring work"),
                    Hrs("billable", "Billable"), Num("completed", "Completed work"), Num("items", "Work items")];
                rows = f.People.Select(p =>
                {
                    var d = t.Person(p.Id).ToDto();
                    return (IReadOnlyList<object?>)new object?[]
                    {
                        p.Name, string.Join("; ", f.TeamsOf.GetValueOrDefault(p.Id) ?? []), HoursOf(d.ActualSeconds), HoursOf(d.PlannedActualSeconds), HoursOf(d.ReactiveActualSeconds),
                        d.ReactiveSharePercent, HoursOf(d.ClientSeconds), HoursOf(d.InternalSeconds), HoursOf(d.MonitoringSeconds), HoursOf(d.BillableSeconds), d.CompletedWork, d.WorkItems,
                    };
                }).ToList();
                notes.Insert(0, "Rows are in name order. They describe where time went; they are not a ranking and nothing here scores a person.");
                break;
            case "team-work-summary":
                columns = [Text("team", "Team"), Num("people", "People"), Hrs("capacity", "Capacity"), Hrs("planned", "Planned"), Hrs("actual", "Actual"), Hrs("planned-actual", "Planned actual"),
                    Hrs("reactive", "Reactive"), Share("reactive-share", "Reactive share"), Num("completed", "Completed work"), Num("items", "Work items")];
                rows = TeamGroups(f, t, q.TeamId).Select(g => (IReadOnlyList<object?>)new object?[]
                {
                    g.Name, g.People, g.Figures.CapacityMinutes is { } c ? Hours(c) : null, Hours(g.Figures.PlannedMinutes), HoursOf(g.Figures.ActualSeconds), HoursOf(g.Figures.PlannedActualSeconds),
                    HoursOf(g.Figures.ReactiveActualSeconds), g.Figures.ReactiveSharePercent, g.Figures.CompletedWork, g.Figures.WorkItems,
                }).ToList();
                notes.Insert(0, "A person in more than one team is counted in each, so team rows can add up to more than the summary.");
                break;
            default:
                columns = [Day("date", "Date"), Hrs("capacity", "Capacity"), Hrs("planned", "Planned"), Hrs("tentative", "Tentative"), Hrs("actual", "Actual"), Hrs("planned-actual", "Planned actual"),
                    Hrs("reactive", "Reactive"), Hrs("variance", "Variance"), Num("completed", "Completed work")];
                rows = f.Dates.Select(day =>
                {
                    var d = t.Day(day).ToDto();
                    return (IReadOnlyList<object?>)new object?[]
                    {
                        Iso(day), d.CapacityMinutes is { } c ? Hours(c) : null, Hours(d.PlannedMinutes), Hours(d.TentativeMinutes), HoursOf(d.ActualSeconds), HoursOf(d.PlannedActualSeconds),
                        HoursOf(d.ReactiveActualSeconds), d.VarianceMinutes is { } v ? Hours(v) : null, d.CompletedWork,
                    };
                }).ToList();
                summary.Add(Fact("Variance", total.VarianceMinutes is { } variance ? Signed(variance, "over plan", "under plan") : "N/A"));
                break;
        }
        return new Built(f.Period, f.Now, summary, columns, rows, notes, f.Sync, person);
    }

    // ---- forecast reports ---------------------------------------------------------------------------------------

    private static Built Ahead(string key, ForecastDto fc, InsightsQuery q)
    {
        var x = fc.Totals;
        List<ReportFactDto> summary =
        [
            Fact("Capacity", x.CapacityMinutes is { } c ? HoursText(c) : "N/A"), Fact("Confirmed", HoursText(x.ConfirmedMinutes)), Fact("Tentative", HoursText(x.TentativeMinutes)),
            Fact("Estimated unscheduled", $"{HoursText(x.UnscheduledMinutes)} in {x.UnscheduledItems} work item{(x.UnscheduledItems == 1 ? "" : "s")}"),
            Fact("Unestimated work", $"{x.UnestimatedItems} work item{(x.UnestimatedItems == 1 ? "" : "s")} (in no hours figure)"),
            Fact("Projected demand", HoursText(x.ProjectedMinutes)),
            Fact("Capacity gap", x.GapMinutes is { } g ? Signed(g, "left", "short") : "N/A"), Fact("Projected load", Percent(x.ProjectedPercent)),
        ];
        var notes = fc.Notes.ToList();
        var person = q.AppUserId is not null ? fc.People.FirstOrDefault()?.DisplayName : null;
        if (key == "capacity-demand")
        {
            notes.Insert(0, "Unscheduled effort has no day of its own. It is listed on the day its work is due; the rest is in the summary. It is never spread across days.");
            summary.Add(Fact("Unscheduled, already past its due date", HoursText(fc.UnscheduledOverdueMinutes)));
            summary.Add(Fact("Unscheduled, no due date in this window", HoursText(fc.UnscheduledNoDateMinutes)));
            return new Built(fc.Window, fc.GeneratedAt, summary,
                [Day("date", "Date"), Hrs("capacity", "Capacity"), Hrs("confirmed", "Confirmed"), Hrs("tentative", "Tentative"), Hrs("unscheduled-due", "Unscheduled, due that day"), Hrs("left", "Left after confirmed and tentative")],
                fc.Daily.Select(d => (IReadOnlyList<object?>)new object?[]
                {
                    Iso(d.Date), d.CapacityMinutes is { } cap ? Hours(cap) : null, Hours(d.ConfirmedMinutes), Hours(d.TentativeMinutes), Hours(d.UnscheduledDueMinutes),
                    d.CapacityMinutes is { } left ? Hours(left - d.ConfirmedMinutes - d.TentativeMinutes) : null,
                }).ToList(), notes, [], person);
        }
        notes.Insert(0, "Rows are in name order. A projected load above 100% is a scheduling condition, not a judgement of the person.");
        var rows = fc.People.Select(p => (IReadOnlyList<object?>)new object?[]
        {
            p.DisplayName, string.Join("; ", p.Teams), p.IsSchedulable ? "yes" : "no", p.Figures.CapacityMinutes is { } cap ? Hours(cap) : null, Hours(p.Figures.ConfirmedMinutes), Hours(p.Figures.TentativeMinutes),
            Hours(p.Figures.UnscheduledMinutes), p.Figures.UnestimatedItems, Hours(p.Figures.ProjectedMinutes), p.Figures.GapMinutes is { } gap ? Hours(gap) : null, p.Figures.ProjectedPercent,
        }).ToList();
        if (fc.Unassigned is { } u)
            rows.Add([NotYetAssigned, "", "", null, Hours(u.ConfirmedMinutes), Hours(u.TentativeMinutes), Hours(u.UnscheduledMinutes), u.UnestimatedItems, Hours(u.ProjectedMinutes), null, null]);
        return new Built(fc.Window, fc.GeneratedAt, summary,
            [Text("person", "Technician"), Text("teams", "Teams"), Text("offered", "Offered for planned work"), Hrs("capacity", "Capacity"), Hrs("confirmed", "Confirmed"), Hrs("tentative", "Tentative"),
             Hrs("unscheduled", "Estimated unscheduled"), Num("unestimated", "Unestimated work items"), Hrs("projected", "Projected demand"), Hrs("gap", "Capacity gap"), Share("load", "Projected load")],
            rows, notes, [], person);
    }

    // ---- comparison reports: one trends read ------------------------------------------------------------------

    private static Built Compared(string key, InsightsQuery q, TrendsDto tr, string? person)
    {
        var notes = tr.Notes.ToList();
        string Seconds(double? s) => s is { } v ? HoursOf((long)v).ToString("0.##", CultureInfo.InvariantCulture) + " h" : "N/A";
        ComparisonDto Total(string k) => tr.Totals.First(x => x.Key == k);
        List<ReportFactDto> summary =
        [
            Fact("Compared with", tr.Previous.Label),
            Fact("Actual work", $"{Seconds(Total("actual").Current)} (before: {Seconds(Total("actual").Previous)})"),
            Fact("Reactive share", $"{Percent(Total("reactive-share").Current)} (before: {Percent(Total("reactive-share").Previous)})"),
            Fact("Completed work", $"{Total("completed").Current:0} (before: {Total("completed").Previous:0})"),
        ];
        List<IReadOnlyList<object?>> Groups(IReadOnlyList<GroupComparisonDto> groups) => groups.Select(g => (IReadOnlyList<object?>)new object?[]
        {
            g.Name, HoursOf(g.CurrentSeconds), HoursOf(g.PreviousSeconds), HoursOf(g.ChangeSeconds), g.ChangePercent, HoursOf(g.CurrentReactiveSeconds), g.CurrentCompleted, g.PreviousCompleted, g.CurrentWorkItems,
        }).ToList();
        List<ReportColumnDto> GroupColumns(string first) =>
        [
            Text("name", first), Hrs("actual", "Actual"), Hrs("previous", "Actual before"), Hrs("change", "Change"), Share("change-percent", "Change"), Hrs("reactive", "Reactive"),
            Num("completed", "Completed work"), Num("completed-before", "Completed before"), Num("items", "Work items"),
        ];

        switch (key)
        {
            case "client-workload":
                notes.Insert(0, "More work for a client is a fact about demand, not a problem in itself.");
                return new Built(tr.Current, tr.GeneratedAt, summary, GroupColumns("Client"), Groups(tr.Clients), notes, tr.Sync, person);
            case "work-sources":
                return new Built(tr.Current, tr.GeneratedAt, summary, GroupColumns("Source"), Groups(tr.Sources), notes, tr.Sync, person);
            case "reactive-work":
                var weeks = tr.Weeks;
                var span = new AnalyticsPeriodDto("weeks", weeks[0].From, weeks[^1].To, tr.Current.TimeZone, Describe(weeks[0].From, weeks[^1].To), weeks[^1].To.DayNumber - weeks[0].From.DayNumber + 1, weeks[^1].Partial);
                notes = ["Reactive work is time recorded on work that had no plan for that person that day. Incidents and urgent support are reactive by nature; it is not a fault.",
                    "The week in progress is marked; its figures are not final.", "Time entered directly in a PSA has no portal row and is not in any figure here."];
                return new Built(span, tr.GeneratedAt,
                    [Fact("Actual work", Seconds(weeks.Sum(w => w.ActualSeconds))), Fact("Reactive work", Seconds(weeks.Sum(w => w.ReactiveSeconds))),
                     Fact("Reactive share", weeks.Sum(w => w.ActualSeconds) > 0 ? Percent(Pct(weeks.Sum(w => w.ReactiveSeconds), weeks.Sum(w => w.ActualSeconds))) : "N/A")],
                    [Day("from", "Week from"), Day("to", "To"), Text("state", "State"), Hrs("actual", "Actual"), Hrs("planned-actual", "Planned actual"), Hrs("reactive", "Reactive"),
                     Share("reactive-share", "Reactive share"), Num("completed", "Completed work"), Num("items", "Work items")],
                    weeks.Select(w => (IReadOnlyList<object?>)new object?[]
                    {
                        Iso(w.From), Iso(w.To), w.Partial ? "in progress" : "complete", HoursOf(w.ActualSeconds), HoursOf(w.PlannedActualSeconds), HoursOf(w.ReactiveSeconds), w.ReactiveSharePercent, w.Completed, w.WorkItems,
                    }).ToList(), notes, tr.Sync, person);
            case "estimate-variance":
                var by = VarianceBy(q.By);
                var source = by == "client" ? tr.ByClient : by == "source" ? tr.BySource : tr.ByCategory;
                long planned = source.Sum(r => (long)r.PlannedMinutes), actual = source.Sum(r => (long)r.ActualMinutes), absolute = source.Sum(r => (long)r.AbsoluteVarianceMinutes);
                notes.Insert(0, "Variance describes how estimates compared with the work, per kind of work. It is never attributed to a person.");
                if (by == "category") notes.Insert(1, "Category is the ticket's category as stored (mapped, or the PSA's own label). Monitoring alerts are one group.");
                return new Built(tr.Current, tr.GeneratedAt,
                    [Fact("Planned, on work that had a plan", HoursText(planned)), Fact("Recorded on that planned work", HoursText(actual)),
                     Fact("Variance", Signed(actual - planned, "over plan", "under plan")),
                     Fact("Estimate variance", planned > 0 ? Percent(Pct(absolute, planned)) : "N/A"), Fact("Ticket-days compared", source.Sum(r => r.TicketDays).ToString(CultureInfo.InvariantCulture))],
                    [Text("name", by == "client" ? "Client" : by == "source" ? "Source" : "Category"), Hrs("planned", "Planned"), Hrs("actual", "Recorded on planned work"), Hrs("variance", "Variance"),
                     Share("variance-percent", "Variance"), Hrs("absolute", "Absolute variance"), Share("estimate-variance", "Estimate variance"), Num("ticket-days", "Ticket-days compared")],
                    source.Select(r => (IReadOnlyList<object?>)new object?[]
                    {
                        r.Name, Hours(r.PlannedMinutes), Hours(r.ActualMinutes), Hours(r.VarianceMinutes), r.VariancePercent, Hours(r.AbsoluteVarianceMinutes), r.EstimateVariancePercent, r.TicketDays,
                    }).ToList(), notes, tr.Sync, person);
            default:
                return new Built(tr.Current, tr.GeneratedAt,
                    [Fact("Compared with", tr.Previous.Label), Fact("Completed work", (tr.Quality.FirstOrDefault()?.Population ?? 0).ToString(CultureInfo.InvariantCulture)),
                     Fact("Signals with data", $"{tr.Quality.Count(s => s.Quality != DataQuality.NotAvailable)} of {tr.Quality.Count}")],
                    [Text("signal", "Signal"), Text("quality", "Data quality"), Num("met", "Met"), Num("eligible", "Out of"), Share("percent", "Share"),
                     Num("previous-met", "Met before"), Num("previous-eligible", "Out of before"), Share("previous-percent", "Share before"), Text("definition", "What is counted"), Text("reason", "What it rests on")],
                    tr.Quality.Select(s => (IReadOnlyList<object?>)new object?[]
                    {
                        s.Name, s.Quality switch { DataQuality.High => "High", DataQuality.Partial => "Partial", _ => "Not available" }, s.Met, s.Eligible, s.Percent,
                        s.PreviousMet, s.PreviousEligible, s.PreviousPercent, s.Definition, s.QualityReason,
                    }).ToList(),
                    ["Signals describe completed work and the data behind it. There is no per-technician quality table, by design.",
                     "A signal marked Not available has no reliable source in the records and is not estimated.", .. notes], tr.Sync, person);
        }
    }

    // ---- mapping and integration health ---------------------------------------------------------------------------

    private static Built Health(IntegrationInsightsDto h)
    {
        static string Ratio(MappingCoverageDto m) => $"{m.Mapped} of {m.Total}";
        return new Built(null, h.GeneratedAt,
            [Fact("Connections", h.Connections.Count.ToString(CultureInfo.InvariantCulture)), Fact("Stale or failing", h.Connections.Count(c => c.Stale).ToString(CultureInfo.InvariantCulture)),
             Fact("Tickets in sync error", h.Connections.Sum(c => c.TicketsInSyncError).ToString(CultureInfo.InvariantCulture)),
             Fact("Time entries not in the PSA", h.Connections.Sum(c => c.TimeEntriesFailed).ToString(CultureInfo.InvariantCulture))],
            [Text("connection", "Connection"), Text("provider", "Provider"), Text("state", "State"), Text("enabled", "Enabled"), Text("last-sync", "Last successful sync (UTC)"), Text("stale", "Stale or failing"),
             Num("tickets", "Tickets"), Share("status", "Statuses mapped"), Text("status-unmapped", "Unmapped statuses"), Share("priority", "Priorities mapped"), Text("priority-unmapped", "Unmapped priorities"),
             Text("links", "PSA logins linked"), Share("links-percent", "Logins linked"), Num("placeholder", "Tickets on a placeholder client"), Num("sync-error", "Tickets in sync error"),
             Num("time-failed", "Time entries failed"), Num("time-pending", "Time entries pending")],
            h.Connections.Select(c => (IReadOnlyList<object?>)new object?[]
            {
                c.Name, c.Provider, c.Status, c.IsEnabled ? "yes" : "no", c.LastSuccessfulSyncAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never", c.Stale ? "yes" : "no",
                c.Tickets, c.StatusMapping.Percent, string.Join(", ", c.StatusMapping.Unmapped), c.PriorityMapping.Percent, string.Join(", ", c.PriorityMapping.Unmapped),
                Ratio(c.TechnicianLinks), c.TechnicianLinks.Percent, c.PlaceholderClientTickets, c.TicketsInSyncError, c.TimeEntriesFailed, c.TimeEntriesPending,
            }).ToList(),
            ["Computed from the records as they stand. No credential, address or error text is included; the error text is on the Integration health page.",
             "A status or priority counts as mapped when it is one of the portal's normalized values. A value passed through from the PSA unchanged is listed as unmapped.",
             $"A connection is stale after {InsightThresholds.StaleSyncHours} hours without a successful sync."], []);
    }
}
