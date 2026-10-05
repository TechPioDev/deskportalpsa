namespace Desk.Application.Workforce;

/// <summary>How much a statement in the attention list asks of the reader. Set by fixed thresholds, never by judgement.</summary>
public enum InsightSeverity { Info = 1, Watch = 2, Attention = 3, Critical = 4 }

/// <summary>How far an optional figure can be trusted, from what the records actually hold. Travels with the number.</summary>
public enum DataQuality { NotAvailable = 0, Partial = 1, High = 2 }

/// <summary>What a forecast drill-down lists: the records behind one forecast figure.</summary>
public enum ForecastWorkKind { Confirmed = 1, Tentative = 2, Unscheduled = 3, Unestimated = 4, AtRisk = 5, Overdue = 6, Skill = 7 }

/// <summary>
/// What management insights and reports are asked for. <paramref name="Window"/> is a forecast
/// window (next-7, next-14, next-30, this-week, next-week, this-month, custom with both dates);
/// <paramref name="Period"/> is a history period as Phase 7 names them; <paramref name="Compare"/>
/// is a period set against the one before it (last-7, last-30, last-week, last-month). Every id is
/// validated against the tenant and the caller's scope.
/// </summary>
/// <param name="By">Estimate variance report only: category, client or source.</param>
/// <param name="SkillId">The skill a skill drill-down lists the work of.</param>
public sealed record InsightsQuery(
    string? Window = null, DateOnly? From = null, DateOnly? To = null,
    Guid? TeamId = null, Guid? DepartmentId = null, Guid? AppUserId = null,
    Guid? ClientId = null, string? Source = null, string? Priority = null,
    string? Compare = null, string? Period = null, string? By = null, Guid? SkillId = null);

/// <summary>
/// The forecast for any grouping (everyone, a team, a person, a client). Every component of the
/// projection is its own figure, so a projected number can always be taken apart:
/// projected = confirmed + tentative + estimated unscheduled; gap = capacity − projected.
/// Unestimated work is a count and is never turned into hours.
/// </summary>
/// <param name="CapacityMinutes">Null where capacity does not apply (a client, work nobody holds, a person not offered for planned work).</param>
/// <param name="GapMinutes">Capacity − projected. Negative: a potential shortage. Positive: potential remaining capacity, never a judgement.</param>
public sealed record ForecastFiguresDto(
    int? CapacityMinutes, int ConfirmedMinutes, int TentativeMinutes,
    int UnscheduledMinutes, int UnscheduledItems, int UnestimatedItems,
    int ProjectedMinutes, int? ConfirmedRemainingMinutes, int? GapMinutes,
    double? ConfirmedPercent, double? ProjectedPercent);

/// <param name="UnscheduledDueMinutes">Remaining unallocated effort of work that is due on this day. Unscheduled effort has no date of its own and is never spread by guesswork.</param>
public sealed record ForecastDayDto(DateOnly Date, int? CapacityMinutes, int ConfirmedMinutes, int TentativeMinutes, int UnscheduledDueMinutes);

public sealed record ForecastPersonDto(Guid AppUserId, string DisplayName, IReadOnlyList<string> Teams, bool IsSchedulable, bool HasSchedule, ForecastFiguresDto Figures);

public sealed record ForecastGroupDto(string Key, string Name, int People, ForecastFiguresDto Figures);

/// <summary>How much of the estimated open work has time allocated to it. N/A when nothing is estimated.</summary>
public sealed record ScheduleCoverageDto(
    int OpenItems, int EstimatedItems, int UnestimatedOpenItems, int EstimatedMinutes, int ScheduledMinutes, int UnscheduledMinutes, double? CoveragePercent);

/// <summary>Only for skills some open estimated work requires. A person holding two skills is counted under both, so rows do not add up.</summary>
public sealed record SkillCapacityDto(Guid SkillId, string Name, int DemandMinutes, int DemandItems, int SkilledPeople, int CapacityMinutes, int GapMinutes, IReadOnlyList<string> People);

/// <summary>Recurring tickets scheduled to be raised in the window. Occurrences carry no estimate, and one is skipped while the previous is still open.</summary>
public sealed record RecurringDemandDto(int Definitions, int Occurrences, IReadOnlyList<RecurringItemDto> Items);
public sealed record RecurringItemDto(Guid Id, string Title, string Schedule, string? AssigneeName, int Occurrences, DateTimeOffset? Next);

/// <summary>Facts about the planning data itself, so a reader knows what the forecast rests on.</summary>
/// <param name="OpenWithoutHolder">Open work the caller may see that nobody holds in the portal; null for someone who sees only their own.</param>
public sealed record PlanningDataQualityDto(
    int People, int PeopleWithoutSchedule, int PeopleNotOffered, int OpenHeldItems, int? OpenWithoutHolder,
    int EstimatedWithSkill, int FinishedWithoutDate);

public sealed record WorkAtRiskDto(int Overdue, int CapacityShortfall, int DueInWindow);

public sealed record InsightFactDto(string Label, string Value);

/// <summary>
/// One statement in the attention list: produced by one fixed rule, with the numbers that produced
/// it and where to look. <paramref name="List"/> names a drill-down; <paramref name="TargetKind"/>
/// and <paramref name="TargetId"/> name a team, person or skill to filter to.
/// </summary>
public sealed record InsightDto(
    string Key, InsightSeverity Severity, string Title, string Rule, IReadOnlyList<InsightFactDto> Facts,
    string? List = null, string? TargetKind = null, string? TargetId = null);

public sealed record ForecastDto(
    AnalyticsPeriodDto Window, DateTimeOffset GeneratedAt, ForecastFiguresDto Totals,
    int UnscheduledOverdueMinutes, int UnscheduledNoDateMinutes,
    ScheduleCoverageDto Coverage, PlanningDataQualityDto DataQuality, WorkAtRiskDto AtRisk,
    IReadOnlyList<ForecastDayDto> Daily, IReadOnlyList<ForecastGroupDto> Teams, IReadOnlyList<ForecastPersonDto> People,
    ForecastFiguresDto? Unassigned, IReadOnlyList<ForecastGroupDto> Clients, IReadOnlyList<ForecastGroupDto> Sources,
    IReadOnlyList<SkillCapacityDto> Skills, RecurringDemandDto? Recurring,
    IReadOnlyList<InsightDto> Attention, IReadOnlyList<string> Notes,
    bool SeesOthers, bool CanExport, bool CanSeeHealth);

/// <summary>
/// One record behind a forecast figure. <paramref name="Kind"/> is "allocation" or "ticket". Titles,
/// references, clients, priority and due date only for tickets the caller may open.
/// </summary>
public sealed record ForecastWorkRowDto(
    string Kind, Guid? Id, Guid TicketId, string Reference, string? Title, string? ClientName, string Source, string? Priority, bool TicketVisible,
    Guid? AppUserId, string? PersonName, string? TeamName, DateOnly? Date, DateTimeOffset? At,
    int? Minutes, int? RequiredMinutes, int? AllocatedMinutes, int? RemainingMinutes,
    DateTimeOffset? DueAt, int? FreeBeforeDueMinutes, string? Risk, string? SkillName, string? Status);

public sealed record ForecastWorkPageDto(ForecastWorkKind Kind, AnalyticsPeriodDto Window, int Total, int TotalMinutes, int Skip, int Take, IReadOnlyList<ForecastWorkRowDto> Rows);

/// <summary>
/// A figure in a period against the same figure in the period before. Both values are always given,
/// so a percentage can never hide its denominator; the percentage is null when the previous value is 0.
/// </summary>
/// <param name="Unit">seconds, minutes, count or percent (for percent, <paramref name="Change"/> is in percentage points).</param>
public sealed record ComparisonDto(string Key, string Label, string Unit, double? Current, double? Previous, double? Change, double? ChangePercent);

public sealed record WeekTrendDto(
    DateOnly From, DateOnly To, bool Partial, long ActualSeconds, long PlannedActualSeconds, long ReactiveSeconds, double? ReactiveSharePercent, int Completed, int WorkItems);

public sealed record GroupComparisonDto(
    string Key, string Name, long CurrentSeconds, long PreviousSeconds, long ChangeSeconds, double? ChangePercent,
    long CurrentReactiveSeconds, int CurrentCompleted, int PreviousCompleted, int CurrentWorkItems);

/// <summary>Planned time against the time recorded on that same planned work. A fact about estimates; never attributed to a person.</summary>
public sealed record EstimateVarianceRowDto(
    string Key, string Name, int PlannedMinutes, int ActualMinutes, int VarianceMinutes, double? VariancePercent,
    int AbsoluteVarianceMinutes, double? EstimateVariancePercent, int TicketDays);

/// <summary>
/// A quality signal with what it was computed from. <paramref name="Met"/> ÷ <paramref name="Eligible"/>
/// out of a <paramref name="Population"/> of completed work, with a label for how complete the
/// underlying data is and the reason. Null figures: the signal is not available.
/// </summary>
public sealed record QualitySignalDto(
    string Key, string Name, string Definition, DataQuality Quality, string QualityReason,
    int? Met, int? Eligible, double? Percent, int Population,
    int? PreviousMet, int? PreviousEligible, double? PreviousPercent);

public sealed record TrendsDto(
    AnalyticsPeriodDto Current, AnalyticsPeriodDto Previous, DateTimeOffset GeneratedAt,
    IReadOnlyList<ComparisonDto> Totals, IReadOnlyList<WeekTrendDto> Weeks,
    IReadOnlyList<GroupComparisonDto> Clients, IReadOnlyList<GroupComparisonDto> Sources,
    IReadOnlyList<EstimateVarianceRowDto> ByCategory, IReadOnlyList<EstimateVarianceRowDto> ByClient, IReadOnlyList<EstimateVarianceRowDto> BySource,
    IReadOnlyList<QualitySignalDto> Quality, IReadOnlyList<InsightDto> Attention,
    IReadOnlyList<SyncFreshnessDto> Sync, IReadOnlyList<string> Notes);

/// <summary>A share of a connection's records that are in a recognised state, with examples of what is not.</summary>
public sealed record MappingCoverageDto(int Mapped, int Total, double? Percent, IReadOnlyList<string> Unmapped);

/// <summary>One PSA connection: its sync state and how well its records map. No credential, endpoint or error text.</summary>
public sealed record ConnectionInsightDto(
    Guid ConnectionId, string Name, string Provider, string Status, bool IsEnabled,
    DateTimeOffset? LastSuccessfulSyncAt, DateTimeOffset? LastHealthCheckAt, bool HasError, bool Stale, int Tickets,
    MappingCoverageDto StatusMapping, MappingCoverageDto PriorityMapping, MappingCoverageDto TechnicianLinks,
    int PlaceholderClientTickets, int TicketsInSyncError, int TimeEntriesFailed, int TimeEntriesPending);

public sealed record IntegrationInsightsDto(DateTimeOffset GeneratedAt, IReadOnlyList<ConnectionInsightDto> Connections, IReadOnlyList<InsightDto> Attention);

// ---- reports ------------------------------------------------------------------------------------------

/// <param name="PeriodKind">"history" (a Phase 7 period), "forecast" (a window), "compare" (a period against the one before) or "none".</param>
/// <param name="Filters">Which filters the report takes: team, department, person, client, source, priority, by.</param>
public sealed record ReportDefinitionDto(string Key, string Category, string Title, string Description, string PeriodKind, IReadOnlyList<string> Filters, bool NeedsIntegrationHealth);

public sealed record ReportFactDto(string Label, string Value);

/// <param name="Kind">text, hours, count, percent or date: how a cell is shown and typed in a spreadsheet.</param>
public sealed record ReportColumnDto(string Key, string Label, string Kind);

/// <summary>
/// A report as previewed: what was asked, when, what the data rests on, a summary and the table.
/// The export is built by the same builder with the same query, so its totals are the preview's.
/// A cell is a string, a number or null (not applicable).
/// </summary>
public sealed record ReportDto(
    ReportDefinitionDto Definition, AnalyticsPeriodDto? Period, DateTimeOffset GeneratedAt,
    IReadOnlyList<ReportFactDto> Applied, IReadOnlyList<ReportFactDto> Summary,
    IReadOnlyList<ReportColumnDto> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, int TotalRows, bool Truncated,
    IReadOnlyList<string> Notes, IReadOnlyList<SyncFreshnessDto> Sync, bool CanExport);

/// <summary>
/// Management insights (Phase 8): what is ahead (capacity against committed, tentative and
/// unscheduled demand), what changed (a period against the one before), what the optional quality
/// data supports, and what needs attention. Deterministic sums over records; every statement names
/// its rule. Reads only. Design: docs/workforce-scheduling/PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md.
/// </summary>
public interface IWorkforceInsightsService
{
    Task<ForecastDto> ForecastAsync(Guid callerId, InsightsQuery query, CancellationToken ct = default);
    Task<ForecastWorkPageDto> ForecastWorkAsync(Guid callerId, InsightsQuery query, ForecastWorkKind kind, int skip, int take, CancellationToken ct = default);
    Task<TrendsDto> TrendsAsync(Guid callerId, InsightsQuery query, CancellationToken ct = default);
    /// <summary>Mapping and integration health; needs <c>integration.health.view</c>.</summary>
    Task<IntegrationInsightsDto> HealthAsync(Guid callerId, CancellationToken ct = default);
}

/// <summary>The report center: definitions in code, one builder per report, preview and export from the same rows.</summary>
public interface IWorkforceReportService
{
    Task<IReadOnlyList<ReportDefinitionDto>> CatalogueAsync(Guid callerId, CancellationToken ct = default);
    Task<ReportDto> ReportAsync(Guid callerId, string key, InsightsQuery query, CancellationToken ct = default);
    /// <param name="format">csv or xlsx. Needs <c>workforce.analytics.export</c>; audited.</param>
    Task<AnalyticsExportDto> ExportReportAsync(Guid callerId, string key, InsightsQuery query, string format, CancellationToken ct = default);
}
