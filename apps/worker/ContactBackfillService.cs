using Desk.Application.Connectors;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;

namespace Desk.Worker;

/// <summary>
/// One pass after startup: fills in the customer contact on tickets imported before contacts were
/// kept, by reading each one from its PSA. Without it a public reply on an older ticket has nobody
/// to go to, and the composer offers internal notes only.
///
/// Once per start, like the author backfill: what remains after a pass is tickets the PSA names no
/// contact for, and the count is logged so that is answerable from the log.
/// </summary>
public sealed class ContactBackfillService(IServiceProvider services, ILogger<ContactBackfillService> logger) : BackgroundService
{
    /// <summary>After the first poll and the author backfill have started, so they do not read the same tickets at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);
    private const int BatchSize = 20;
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            Dictionary<Guid, List<(Guid Id, string ExternalId)>> pending;
            using (var scope = services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                pending = await ContactBackfill.PendingAsync(scope.ServiceProvider.GetRequiredService<DeskDbContext>(), stoppingToken);
            }

            var total = pending.Sum(p => p.Value.Count);
            if (total == 0)
            {
                logger.LogInformation("Contact backfill: every PSA ticket already has a reachable contact");
                return;
            }
            logger.LogInformation("Contact backfill: {Tickets} tickets without a contact across {Connections} connection(s)", total, pending.Count);

            foreach (var (connectionId, tickets) in pending)
            {
                int found = 0, none = 0, failed = 0, nameOnly = 0;
                foreach (var batch in tickets.Chunk(BatchSize))
                {
                    using var scope = services.CreateScope();
                    scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                    try
                    {
                        var connector = await scope.ServiceProvider.GetRequiredService<IConnectorResolver>().ResolveAsync(connectionId, stoppingToken);
                        var r = await ContactBackfill.ApplyAsync(scope.ServiceProvider.GetRequiredService<DeskDbContext>(), connector, batch, stoppingToken);
                        found += r.Found; none += r.NoContact; failed += r.Failed; nameOnly += r.NameOnly;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The connection itself could not be reached; its sync already reports why.
                        failed += batch.Length;
                        logger.LogWarning(ex, "Contact backfill: connection {Connection} could not be read", connectionId);
                        break;
                    }
                    await Task.Delay(BatchPause, stoppingToken);
                }
                logger.LogInformation(
                    "Contact backfill for {Connection}: {Found} contacts found, {NameOnly} named without an email, {None} tickets with no contact in the PSA, {Failed} could not be read",
                    connectionId, found, nameOnly, none, failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Contact backfill failed");
        }
    }
}
