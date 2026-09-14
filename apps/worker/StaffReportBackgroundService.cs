using Desk.Application.Reporting;

namespace Desk.Worker;

/// <summary>
/// Sends the MSP's own scheduled reports (daily, weekly, monthly, quarterly). Checks every five
/// minutes; each schedule is due once, at 07:00 in the organization's time zone.
/// </summary>
public sealed class StaffReportBackgroundService(IStaffReportRunner runner, ILogger<StaffReportBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Staff report service started; interval {Interval}m", Interval.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Staff report cycle failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
