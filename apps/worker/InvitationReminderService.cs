using Desk.Infrastructure.Identity;

namespace Desk.Worker;

/// <summary>
/// Reminds people whose invitation is waiting (on the third and the sixth day) and tells them
/// once when it has expired. Every thirty minutes is plenty: the days are what matter.
/// </summary>
public sealed class InvitationReminderService(InvitationReminderRunner runner, ILogger<InvitationReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short first wait, so a restart does not fire reminders before the rest of the worker is up.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await runner.RunAllAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Invitation reminders: {Count} mail(s) sent", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Invitation reminder cycle failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
