using Desk.Application.Boards;

namespace Desk.Worker;

/// <summary>
/// Raises recurring tickets when they fall due. Checks every two minutes: a schedule set for 09:00
/// arrives by 09:02, which is well inside what anybody means by "at nine".
/// </summary>
public sealed class RecurringTicketBackgroundService(IRecurringTicketRunner runner, ILogger<RecurringTicketBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Recurring ticket service started; interval {Interval}m", Interval.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recurring ticket cycle failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
