using Desk.Application.Sync;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;

namespace Desk.Worker;

/// <summary>
/// One pass after startup: re-reads the notes of held tickets whose staff notes predate the author
/// id, so each gets the id from the provider's own record rather than from a guess on its name.
///
/// A sync cannot do this. Full or not, it only reads the tickets its import window returns - those
/// active in the last N days - so a thread that went quiet before the id existed would keep its old
/// byline for good. Techpio's 53 notes filed under the integration account were all on such tickets.
///
/// Once per start rather than on a schedule: after one pass the notes left are ones the provider
/// gives no author for, and asking again every few minutes buys nothing. The next deploy re-checks
/// them, which is cheap because by then the set is small.
/// </summary>
public sealed class NoteAuthorBackfillService(
    IServiceProvider services,
    ILogger<NoteAuthorBackfillService> logger) : BackgroundService
{
    /// <summary>After the first poll has run, so the two are not reading the same tickets at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Tickets per batch, and the pause between batches: gentle on the provider's quota.</summary>
    private const int BatchSize = 20;
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            int before;
            Dictionary<Guid, List<string>> pending;
            using (var scope = services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
                before = await NoteAuthorBackfill.CountAsync(db, stoppingToken);
                pending = await NoteAuthorBackfill.PendingAsync(db, stoppingToken);
            }

            // Logged at zero too, so "did it run" is answerable from the log on a quiet deploy.
            if (before == 0)
            {
                logger.LogInformation("Note author backfill: every staff note already has an author id");
                return;
            }
            logger.LogInformation(
                "Note author backfill: {Notes} staff notes without an author id, across {Tickets} tickets",
                before, pending.Sum(p => p.Value.Count));

            foreach (var (connectionId, tickets) in pending)
            {
                foreach (var batch in tickets.Chunk(BatchSize))
                {
                    // A scope per batch, as the poller does per connection: one failure cannot poison
                    // the DbContext the rest of the pass uses.
                    using (var scope = services.CreateScope())
                    {
                        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<IConnectionSyncRunner>()
                                .RefreshNotesAsync(connectionId, batch, stoppingToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Note author backfill: a batch failed for connection {Connection}", connectionId);
                        }
                    }
                    await Task.Delay(BatchPause, stoppingToken);
                }
            }

            using (var scope = services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
                var after = await NoteAuthorBackfill.CountAsync(
                    scope.ServiceProvider.GetRequiredService<DeskDbContext>(), stoppingToken);
                logger.LogInformation(
                    "Note author backfill finished: {Filled} notes given an author id, {Left} still without one",
                    before - after, after);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-pass; the next start picks up whatever is left.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Note author backfill failed");
        }
    }
}
