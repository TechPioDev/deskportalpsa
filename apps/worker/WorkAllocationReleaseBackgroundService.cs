using Desk.Application.Workforce;

namespace Desk.Worker;

/// <summary>
/// Every five minutes, takes finished work out of people's future plans (see
/// <see cref="IWorkAllocationReleaser"/>). The capacity engine already ignores such work at once;
/// this keeps the stored plan honest and audited.
/// </summary>
public sealed class WorkAllocationReleaseBackgroundService(IWorkAllocationReleaseRunner runner, ILogger<WorkAllocationReleaseBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Planned-work release service started; interval {Interval}m", Interval.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Planned-work release cycle failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
