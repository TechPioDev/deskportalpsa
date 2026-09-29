using Desk.Application.Notifications;

namespace Desk.Worker;

/// <summary>
/// Push notifications: once a minute, look for tickets newly assigned, newly replied to by a client, or
/// newly inside two hours of their SLA, and tell the technician's phone. A minute is quick enough for
/// "a ticket was assigned to you" and slow enough to cost nothing between events.
/// </summary>
public sealed class PushBackgroundService(IPushRunner runner, ILogger<PushBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Push notification service started; interval {Interval}m", Interval.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Push notification pass failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
