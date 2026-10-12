using Desk.Infrastructure.Tickets;

namespace Desk.Worker;

/// <summary>
/// Tells the followers of a ticket that they were put on it, and what has happened since. Every
/// two minutes: soon enough to feel like being copied in, slow enough that a burst of replies on
/// one ticket becomes one message.
/// </summary>
public sealed class TicketFollowerNotifyService(TicketFollowerNotifyRunner runner, ILogger<TicketFollowerNotifyService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Follower notice service started; interval {Interval}m", Interval.TotalMinutes);
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await runner.RunAllAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Follower notices: {Count} message(s) sent", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Follower notice cycle failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
