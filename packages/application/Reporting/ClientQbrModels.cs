namespace Desk.Application.Reporting;

public sealed record QbrFigures(
    int Raised, int Resolved, int OpenAtEnd, decimal Hours, decimal BillableHours,
    int SlaEligible, int WithinSla, double? AvgResolutionHours, double? MedianResolutionHours)
{
    /// <summary>Null when no resolved ticket carried an SLA target — "no data", never 0% or 100%.</summary>
    public double? SlaPct => SlaEligible > 0 ? Math.Round(100.0 * WithinSla / SlaEligible, 1) : null;
}

public sealed record QbrCount(string Label, int Count);

public sealed record QbrMonth(DateOnly Month, int Raised, int Resolved, decimal Hours);

public sealed record QbrOpenTicket(string Reference, string Title, string Priority, string Status, int AgeDays);

/// <summary>
/// A business review of one client over one period (normally a quarter), with the previous period
/// alongside so every figure answers "compared to what". Rendered once to PDF and CSV.
///
/// Definitions, stated here because a QBR is read by the client and argued over:
/// raised = the PSA's raise date falls in the period; resolved = resolution (or, without one, closure)
/// falls in the period, whenever the ticket was raised; open at end = raised by the last day and not
/// resolved by it; SLA = resolved in the period with an SLA target, met when resolved by that target.
/// </summary>
public sealed record ClientQbr(
    string OrganizationName,
    string ClientName,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string PreviousLabel,
    QbrFigures Current,
    QbrFigures Previous,
    IReadOnlyList<QbrMonth> Months,
    IReadOnlyList<QbrCount> ByPriority,
    IReadOnlyList<QbrCount> ByCategory,
    IReadOnlyList<TechnicianReportRow> Technicians,
    IReadOnlyList<QbrOpenTicket> OldestOpen,
    DateTimeOffset GeneratedAt,
    string TimeZone)
{
    public string Title => $"Business review — {ClientName} — {PeriodLabel}";

    public string Summary =>
        $"{Current.Raised} raised · {Current.Resolved} resolved · {Current.Hours:0.##}h" +
        (Current.SlaPct is { } sla ? $" · {sla:0.#}% SLA" : "");
}
