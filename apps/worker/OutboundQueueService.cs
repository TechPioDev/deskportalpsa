using Desk.Infrastructure.Sync;

namespace Desk.Worker;

/// <summary>
/// Sends the changes that are waiting for a PSA. Every few seconds it asks which are due and tries
/// each: one the PSA takes is synced, one it still cannot be given waits again and longer, one it
/// refuses is failed and kept.
///
/// A loop of its own, so that a reply waiting for one PSA is not held up behind a long sync of
/// another. Each change is taken by a save of its own before it is sent, so a second worker would
/// not send it as well, and inside the scope of the organization it belongs to
/// (<see cref="OutboundRunner"/>).
/// </summary>
public sealed class OutboundQueueService(
    OutboundRunner runner,
    IConfiguration configuration,
    ILogger<OutboundQueueService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Outbound:PollSeconds", 5.0)));
        logger.LogInformation("Outbound queue loop started; every {Seconds:0.#}s", every.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await runner.RunDueAsync(25, stoppingToken);
                if (sent > 0) logger.LogInformation("Outbound queue: {Count} change(s) tried", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbound queue cycle failed");
            }

            try { await Task.Delay(every, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
