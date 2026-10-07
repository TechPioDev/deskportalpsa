using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Desk.Worker;

/// <summary>
/// Scheduled reconciliation: runs the full inbound sync for every enabled connection on an
/// interval, so provider-side changes reach the portal without anyone pressing a button.
///
/// This service predated the sync engine and used to fetch modified tickets and DISCARD them —
/// while still stamping the connection Healthy. Every capability the engine gained (notes,
/// attachments, time totals, deletion reconciliation, assignee names) therefore only ran on a
/// manual sync. It now delegates to the same <see cref="IConnectionSyncRunner"/> the manual button
/// uses, so the two paths cannot drift: whatever a manual sync does, the schedule does.
/// </summary>
public sealed class PollingSyncService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PollingSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Configurable so an MSP can trade freshness for provider API quota. Autotask meters
        // requests per hour; five minutes is a sane default for both providers.
        var interval = TimeSpan.FromMinutes(
            Math.Max(0.1, configuration.GetValue("Sync:PollIntervalMinutes", 5.0)));
        logger.LogInformation("Polling sync started; interval {Interval:0.#}m", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllConnectionsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Polling cycle failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncAllConnectionsAsync(CancellationToken ct)
    {
        // Connection ids are read in one short-lived scope; each connection then syncs in its own
        // scope so one connection's failure (or a long run) cannot poison another's DbContext.
        List<Guid> connectionIds;
        using (var scope = services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            connectionIds = await db.PsaConnections
                .Where(c => c.IsEnabled && c.Status != ConnectionStatus.Failed)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        foreach (var connectionId in connectionIds)
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            var runner = scope.ServiceProvider.GetRequiredService<IConnectionSyncRunner>();
            try
            {
                var result = await runner.RunAsync(connectionId, full: false, ct);
                if (result.Fetched + result.Notes + result.Attachments + result.AttachmentsRemoved + result.NotesRemoved > 0)
                    logger.LogInformation(
                        "Synced {Connection}: {Fetched} tickets ({Created} new, {Updated} updated), {Notes} notes (+{NotesRemoved} removed), {Files} files (+{FilesRemoved} removed)",
                        connectionId, result.Fetched, result.Created, result.Updated,
                        result.Notes, result.NotesRemoved, result.Attachments, result.AttachmentsRemoved);
            }
            catch (Desk.Application.Common.ConflictException)
            {
                // A run already has this connection - someone pressed "Sync now", or an earlier run
                // of this worker is still going. One at a time; this cycle moves on.
                logger.LogDebug("Scheduled sync skipped for connection {Connection}: a run is already in progress", connectionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The runner already marked the connection Degraded with the reason; this log is
                // for the operator reading worker output, not for state.
                logger.LogWarning(ex, "Scheduled sync failed for connection {Connection}", connectionId);
            }
        }
    }
}
