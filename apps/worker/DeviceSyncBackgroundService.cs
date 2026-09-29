using Desk.Application.ControlPanel;

namespace Desk.Worker;

/// <summary>
/// Brings every client's devices in from the PSA once a day. Devices change slowly - a laptop bought,
/// a server retired - so a daily pass keeps the list honest without adding to the five-minute ticket
/// sync's load. The first pass runs a few minutes after start, so a new deploy fills an empty list the
/// same morning rather than the next.
/// </summary>
public sealed class DeviceSyncBackgroundService(IDeviceSyncRunner runner, ILogger<DeviceSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Device sync service started; first pass in {Minutes}m, then every {Hours}h",
            FirstRun.TotalMinutes, Interval.TotalHours);
        await Task.Delay(FirstRun, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await runner.RunAllAsync(stoppingToken);
                logger.LogInformation("Device sync pass: {Created} new, {Updated} refreshed, {Retired} retired, {Linked} tickets linked",
                    result.Created, result.Updated, result.Retired, result.TicketsLinked);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Device sync pass failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
