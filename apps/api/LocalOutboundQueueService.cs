using Desk.Infrastructure.Sync;

namespace Desk.Api;

/// <summary>
/// Local mode only. The changes waiting for a PSA are sent by the worker; local mode runs no
/// worker, so the API takes that turn itself every few seconds. Never registered outside
/// Development local mode: in production the worker has this loop and the API has none.
/// </summary>
public sealed class LocalOutboundQueueService(OutboundRunner runner, ILogger<LocalOutboundQueueService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunDueAsync(25, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Local outbound queue turn failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
