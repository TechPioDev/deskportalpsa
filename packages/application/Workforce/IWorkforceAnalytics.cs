namespace Desk.Application.Workforce;

/// <summary>Which actual time a figure or a drill-down counts: everything, only time on work planned that day, or only reactive time.</summary>
public enum ActualKindFilter { All = 0, Planned = 1, Reactive = 2 }

/// <summary>What a drill-down lists: the records behind one card.</summary>
public enum AnalyticsWorkKind
{
    /// <summary>Every time entry and live clock in the figure.</summary>
    Actual = 1,
    /// <summary>Time on work the person had planned that day.</summary>
    PlannedActual = 2,
    /// <summary>Time on work with nothing planned that day.</summary>
    Reactive = 3,
    /// <summary>Confirmed allocations starting in the period.</summary>
    Planned = 4,
    /// <summary>Tentative allocations starting in the period.</summary>
    Tentative = 5,
    /// <summary>Unique work items finished in the period, credited to the people in scope.</summary>
    Completed = 6,
    /// <summary>Open work held by the people in scope, right now.</summary>
    Open = 7,
    Unscheduled = 8,
    Overdue = 9,
}

public enum AnalyticsExportReport { Technicians = 1, Teams = 2, Clients = 3, Sources = 4, Daily = 5, Work = 6 }

/// <summary>
/// What the dashboard is asked for. The period is a preset key (today, yesterday, this-week,
/// last-week, this-month, last-month, 7d, 30d) resolved on the server in the organization's zone,
/// or "custom" with both dates. Every id is validated against the tenant and the caller's scope.
/// </summary>
/// <param name="Source">"psa:{connectionId}" (one PSA connection), "client" (any PSA ticket), "internal" (the team's boards) or "monitoring".</param>
/// <param name="Priority">The ticket's normalized priority (CRITICAL, HIGH, NORMAL, LOW, as stored).</param>
/// <param name="Kind">Narrows actual time to planned or reactive work; planned minutes and capacity are unchanged.</param>
public sealed record AnalyticsQuery(
    string? Period = null, DateOnly? From = null, DateOnly? To = null,
    Guid? TeamId = null, Guid? DepartmentId = null, Guid? AppUserId = null,
    Guid? ClientId = null, string? Source = null, string? Priority = null,
    ActualKindFilter Kind = ActualKindFilter.All);

/// <summary>The resolved period: whole dates in the organization's zone, and how to say it.</summary>
public sealed record AnalyticsPeriodDto(string Key, DateOnly From, DateOnly To, string TimeZone, string Label, int Days, bool EndsInFuture);

/// <summary>
/// The figures, for any grouping (everyone, a person, a team, a client, a day). Capacity and planned
/// time are whole minutes; actual time is whole seconds (the screen rounds). A ratio with a zero
/// denominator is null: N/A, never 0 %. Nothing here is a score.
/// </summary>
/// <param name="CapacityMinutes">Null where capacity does not apply (a client, a source, a person not offered for planned work).</param>
/// <param name="PlannedToDateMinutes">Confirmed planned minutes on days up to and including today: what variance compares with, so work planned for later in a running period is not yet "behind".</param>
/// <param name="LiveSeconds">How much of <paramref name="ActualSeconds"/> is clocks still running.</param>
/// <param name="BillableSeconds">Entries marked billable when logged; live clocks are not yet marked.</param>
/// <param name="VarianceMinutes">All recorded time − planned to date, only when something was planned by today.</param>
/// <param name="AbsoluteVarianceMinutes">Σ |actual − planned| over person-ticket-days with a plan: estimate variance, not a quality score.</param>
/// <param name="OverCapacityMinutes">Confirmed planned time beyond a person-day's capacity (an override put it there).</param>
public sealed record AnalyticsFiguresDto(
    int? CapacityMinutes, int PlannedMinutes, int PlannedToDateMinutes, int TentativeMinutes,
    long ActualSeconds, long PlannedActualSeconds, long ReactiveActualSeconds, long LiveSeconds, long BillableSeconds,
    long ClientSeconds, long InternalSeconds, long MonitoringSeconds,
    double? ScheduledUtilizationPercent, double? CapacityUtilizationPercent, double? ReactiveSharePercent,
    int? VarianceMinutes, double? VariancePercent,
    int AbsoluteVarianceMinutes, double? EstimateVariancePercent, int PlannedItemsCompared,
    int OverCapacityMinutes, int OverCapacityPersonDays,
    int CompletedWork, int WorkItems, int ReactiveWorkItems);

public sealed record AnalyticsPersonDto(
    Guid AppUserId, string DisplayName, IReadOnlyList<string> Teams, bool IsSchedulable, bool HasSchedule, string TimeZone,
    AnalyticsFiguresDto Figures);

/// <summary>One row of a breakdown. <paramref name="Key"/> is an id, a source key, a priority or a label; "none" / "hidden" for the catch-all rows.</summary>
public sealed record AnalyticsGroupDto(string Key, string Name, int People, AnalyticsFiguresDto Figures);

public sealed record AnalyticsDayDto(DateOnly Date, AnalyticsFiguresDto Figures);

/// <summary>Open work held by the people in scope as of <paramref name="AsOf"/>: not period-bound. The due figures leave out work whose SLA clock is paused, as the boards do.</summary>
public sealed record WorkNowDto(DateTimeOffset AsOf, int Open, int Unscheduled, int Overdue, int DueToday, int DueSoon, int UnscheduledDue);

/// <summary>Capacity against demand for the period; confirmed and tentative are never merged.</summary>
public sealed record CapacityDemandDto(
    int AvailableMinutes, int ConfirmedMinutes, int TentativeMinutes, int ProjectedMinutes, int ShortageMinutes, int RemainingConfirmedMinutes);

public sealed record HeatmapCellDto(int? CapacityMinutes, int PlannedMinutes, int ActualSeconds);
public sealed record HeatmapRowDto(Guid AppUserId, string DisplayName, IReadOnlyList<HeatmapCellDto> Cells);
/// <param name="Unavailable">Why there is no heatmap (too many days or people), or null when <paramref name="Rows"/> is the heatmap.</param>
public sealed record HeatmapDto(IReadOnlyList<DateOnly> Dates, IReadOnlyList<HeatmapRowDto> Rows, int PeopleTotal, bool Truncated, string? Unavailable);

/// <summary>How old the PSA side of the figures is: each connection's newest successful sync.</summary>
public sealed record SyncFreshnessDto(string Connection, DateTimeOffset? LastSuccessfulSyncAt);

public sealed record AnalyticsOverviewDto(
    AnalyticsPeriodDto Period, DateTimeOffset GeneratedAt,
    AnalyticsFiguresDto Totals, WorkNowDto Now, CapacityDemandDto Demand,
    IReadOnlyList<AnalyticsPersonDto> People,
    IReadOnlyList<AnalyticsGroupDto> Teams, IReadOnlyList<AnalyticsGroupDto> Clients, IReadOnlyList<AnalyticsGroupDto> Sources,
    IReadOnlyList<AnalyticsGroupDto> Priorities, IReadOnlyList<AnalyticsGroupDto> WorkTypes,
    IReadOnlyList<AnalyticsDayDto> Daily, HeatmapDto Heatmap,
    IReadOnlyList<SyncFreshnessDto> Sync, IReadOnlyList<string> Notes,
    bool SeesOthers, bool CanExport);

/// <summary>One piece of work in a person's period: planned against actual on that ticket.</summary>
public sealed record AnalyticsWorkItemDto(
    Guid TicketId, string Reference, string? Title, string? ClientName, string Source, string? Priority, bool TicketVisible, bool Finished, DateTimeOffset? FinishedAt,
    int PlannedMinutes, int TentativeMinutes, int ActualSeconds, int ReactiveActualSeconds, int? VarianceMinutes, double? VariancePercent, int Entries, int Days);

public sealed record TechnicianAnalyticsDto(
    AnalyticsPeriodDto Period, DateTimeOffset GeneratedAt, AnalyticsPersonDto Person,
    IReadOnlyList<AnalyticsGroupDto> Clients, IReadOnlyList<AnalyticsGroupDto> Sources, IReadOnlyList<AnalyticsGroupDto> WorkTypes, IReadOnlyList<AnalyticsGroupDto> Priorities,
    IReadOnlyList<AnalyticsDayDto> Daily, IReadOnlyList<AnalyticsWorkItemDto> Items, bool ItemsTruncated,
    IReadOnlyList<string> Notes);

/// <summary>
/// One record behind a figure. <paramref name="Kind"/> is "entry", "live", "allocation" or "ticket";
/// the nullable fields are set as that kind has them. Titles, references and clients are given only
/// for tickets the caller may open ("Work you cannot open" otherwise).
/// </summary>
public sealed record AnalyticsWorkRowDto(
    string Kind, Guid? Id, DateOnly Date, DateTimeOffset? At, Guid? AppUserId, string? PersonName,
    Guid TicketId, string Reference, string? Title, string? ClientName, string Source, string? Priority, bool TicketVisible,
    int? Minutes, int? Seconds, bool? Billable, bool? PlannedWork, string? Status, DateTimeOffset? DueAt, DateTimeOffset? FinishedAt);

/// <summary>A page of records with the totals of the WHOLE set, so the list reconciles with the card it opened from.</summary>
public sealed record AnalyticsWorkPageDto(
    AnalyticsWorkKind Kind, AnalyticsPeriodDto Period, int Total, long TotalSeconds, int TotalMinutes, int Skip, int Take,
    IReadOnlyList<AnalyticsWorkRowDto> Rows);

public sealed record AnalyticsOptionDto(string Key, string Name);

/// <summary>The filter lists, limited to what the caller may see.</summary>
public sealed record AnalyticsFilterOptionsDto(
    IReadOnlyList<WorkforceGroupDto> Teams, IReadOnlyList<WorkforceGroupDto> Departments, IReadOnlyList<AnalyticsOptionDto> People,
    IReadOnlyList<AnalyticsOptionDto> Clients, IReadOnlyList<AnalyticsOptionDto> Sources, IReadOnlyList<string> Priorities,
    bool SeesOthers, bool CanExport, string TimeZone);

public sealed record AnalyticsExportDto(string FileName, string ContentType, byte[] Content, int Rows);

/// <summary>
/// Workforce analytics (Phase 7): how work is distributed, planned, executed and completed, as
/// facts over a period, for the people the caller's <c>schedule.view</c> scope reaches. Reads only.
/// Definitions: docs/workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md.
/// </summary>
public interface IWorkforceAnalyticsService
{
    Task<AnalyticsFilterOptionsDto> FiltersAsync(Guid callerId, CancellationToken ct = default);
    Task<AnalyticsOverviewDto> OverviewAsync(Guid callerId, AnalyticsQuery query, CancellationToken ct = default);
    Task<TechnicianAnalyticsDto> TechnicianAsync(Guid callerId, Guid appUserId, AnalyticsQuery query, CancellationToken ct = default);
    Task<AnalyticsWorkPageDto> WorkAsync(Guid callerId, AnalyticsQuery query, AnalyticsWorkKind kind, int skip, int take, CancellationToken ct = default);
    Task<AnalyticsExportDto> ExportAsync(Guid callerId, AnalyticsQuery query, AnalyticsExportReport report, CancellationToken ct = default);
}
