namespace Desk.Application.ControlPanel;

/// <summary>Outcome of handing a rendered report to the delivery pipeline.</summary>
public sealed record ReportDeliveryResult(bool Delivered, string? Note);

/// <summary>
/// Delivers a rendered report to its recipients (e.g. email). The default implementation only
/// logs — real email is an environment-gated provider — but the report is always stored for
/// in-portal download, so scheduling is fully functional without email configured.
/// </summary>
public interface IReportDelivery
{
    Task<ReportDeliveryResult> DeliverAsync(Guid organizationId, string? recipients, string subject, string fileName, string csv, CancellationToken ct = default);
}

/// <summary>
/// Generates + delivers a report for every enabled schedule whose next run is due. Called by the
/// worker on a timer under platform scope. Returns the number of schedules processed.
/// </summary>
public interface IScheduledReportRunner
{
    Task<int> RunDueAsync(CancellationToken ct = default);
}
