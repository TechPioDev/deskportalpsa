namespace Desk.Application.Analytics;

/// <summary>
/// Aggregates ticket data into technician/manager dashboard metrics. All queries are tenant-scoped;
/// callers additionally constrain by the supplied filter. Component scores are derived from what the
/// ticket data actually supports today (SLA, resolution, worklog + documentation proxies); untracked
/// signals (CSAT, first-response, reopen) are left unmeasured and excluded from the score.
/// </summary>
public interface ITechnicianMetricsService
{
    Task<TechnicianMetrics> ForTechnicianAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default);
    Task<IReadOnlyList<TeamComparisonRow>> TeamAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default);
    Task<IReadOnlyList<TrendPoint>> TrendAsync(MetricsFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Per technician, per day, over the filter's range: hours logged and tickets resolved. The
    /// series every productivity view is drawn from — a week, a month, a quarter and a custom range
    /// are the same query with different bounds, so there is one of these rather than four.
    /// </summary>
    Task<IReadOnlyList<TechnicianDay>> DailyAsync(MetricsFilter filter, CancellationToken ct = default);
}

/// <summary>
/// Client-level workload: where the desk's capacity is going. Separate from the technician service
/// because it answers a different question for a different reader — an owner asking which customers
/// consume the most, not a manager looking at one person.
/// </summary>
public interface IClientWorkloadService
{
    Task<ClientWorkloadReport> ForClientsAsync(MetricsFilter filter, CancellationToken ct = default);
}

/// <summary>
/// How much of the work the PSA recorded is visible in this portal. The comparative layer — and the
/// one surface where a careless design would invite reading a gap as wasted time.
/// </summary>
public interface IPortalCoverageService
{
    Task<PortalCoverageReport> CoverageAsync(MetricsFilter filter, CancellationToken ct = default);
}
