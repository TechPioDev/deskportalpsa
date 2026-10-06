using Desk.Application.Sync;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Desk.Worker;

/// <summary>
/// Runs the syncs people ask for with "Sync now" and "Re-sync all". The API only notes the request
/// on the connection; this reads those notes every few seconds and runs each through the same
/// runner the schedule uses, so the two cannot drift.
///
/// A loop of its own, beside the scheduled one: a request does not wait for the schedule to work
/// through every other connection first. For the same connection it does wait - one run at a time
/// is the run's own rule - and is come back to on the next turn.
/// </summary>
public sealed class RequestedSyncService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<RequestedSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Sync:RequestPollSeconds", 5.0)));
        logger.LogInformation("Requested-sync loop started; every {Seconds:0.#}s", every.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Requested-sync cycle failed");
            }

            try { await Task.Delay(every, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunPendingAsync(CancellationToken ct)
    {
        List<Guid> connectionIds;
        using (var scope = services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            // The one asked for longest ago first.
            connectionIds = await SyncRequests.Pending(db.PsaConnections.AsNoTracking())
                .OrderBy(c => c.SyncRequestedAt)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        foreach (var connectionId in connectionIds)
        {
            // A unit of work for each, so one connection's failure cannot poison the next one's.
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            var runner = scope.ServiceProvider.GetRequiredService<RequestedSyncRunner>();
            var outcome = await runner.RunAsync(connectionId, ct);
            switch (outcome)
            {
                case RequestedSyncOutcome.Done:
                    logger.LogInformation("Requested sync of connection {Connection} finished", connectionId);
                    break;
                case RequestedSyncOutcome.More:
                    logger.LogInformation("Requested sync of connection {Connection} has more to read; it carries on", connectionId);
                    break;
                case RequestedSyncOutcome.Failed:
                    // The runner marked the connection Degraded with the reason; this is for the operator.
                    logger.LogWarning("Requested sync of connection {Connection} failed; the reason is on the connection", connectionId);
                    break;
                case RequestedSyncOutcome.Busy:
                    logger.LogDebug("Requested sync of connection {Connection} waits: a run is already in progress", connectionId);
                    break;
            }
        }
    }
}
