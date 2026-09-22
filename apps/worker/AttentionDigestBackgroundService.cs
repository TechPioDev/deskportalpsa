using Desk.Application.Admin;

namespace Desk.Worker;

/// <summary>
/// Emails each organization's "needs attention" digest once a day (07:30 in its time zone), only
/// when something is on the list. Checks every ten minutes.
/// </summary>
public sealed class AttentionDigestBackgroundService(IAttentionDigestRunner runner, ILogger<AttentionDigestBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Attention digest service started; interval {Interval}m", Interval.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Attention digest cycle failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
