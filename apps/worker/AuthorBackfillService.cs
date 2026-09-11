using Desk.Application.Sync;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;

namespace Desk.Worker;

/// <summary>
/// One pass after startup: fills in the author id on notes and attachments imported before it was
/// kept, from the provider's own record rather than from a guess on the name.
///
/// A sync cannot do this for notes. Full or not, it only reads the tickets its import window returns -
/// those active in the last N days - so a thread that went quiet before the id existed would keep its
/// old byline for good. Techpio's 53 notes filed under the integration account were all on such
/// tickets. Attachments are read by an undated tenant-wide sweep instead, which has no window, so one
/// sweep per connection covers them.
///
/// Once per start rather than on a schedule: after one pass what is left is what the provider gives no
/// author for, and asking again every few minutes buys nothing. The next deploy re-checks it, which is
/// cheap because by then the set is small or empty.
/// </summary>
public sealed class AuthorBackfillService(
    IServiceProvider services,
    ILogger<AuthorBackfillService> logger) : BackgroundService
{
    /// <summary>After the first poll has run, so the two are not reading the same tickets at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Tickets per batch of note reads, and the pause between batches: gentle on the provider's quota.</summary>
    private const int BatchSize = 20;
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await NotesAsync(stoppingToken);
            await AttachmentsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-pass; the next start picks up whatever is left.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Author id backfill failed");
        }
    }

    /// <summary>
    /// Each step in its own scope, as the poller runs each connection: one failure cannot poison the
    /// DbContext the rest of the pass uses.
    /// </summary>
    private async Task<T> ScopedAsync<T>(Func<IServiceProvider, Task<T>> use)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
        return await use(scope.ServiceProvider);
    }

    private async Task NotesAsync(CancellationToken ct)
    {
        var before = await ScopedAsync(sp => AuthorBackfill.NotesCountAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        // Logged at zero too, so "did it run" is answerable from the log on a quiet deploy.
        if (before == 0)
        {
            logger.LogInformation("Author id backfill: every staff note already has an author id");
            return;
        }

        var pending = await ScopedAsync(sp => AuthorBackfill.NotesPendingAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        logger.LogInformation(
            "Author id backfill: {Notes} staff notes without an author id, across {Tickets} tickets",
            before, pending.Sum(p => p.Value.Count));

        foreach (var (connectionId, tickets) in pending)
        {
            foreach (var batch in tickets.Chunk(BatchSize))
            {
                try
                {
                    await ScopedAsync(sp => sp.GetRequiredService<IConnectionSyncRunner>()
                        .RefreshNotesAsync(connectionId, batch, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Author id backfill: a batch of notes failed for connection {Connection}", connectionId);
                }
                await Task.Delay(BatchPause, ct);
            }
        }

        var after = await ScopedAsync(sp => AuthorBackfill.NotesCountAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        logger.LogInformation(
            "Author id backfill, notes: {Filled} given an author id, {Left} still without one", before - after, after);
    }

    private async Task AttachmentsAsync(CancellationToken ct)
    {
        var before = await ScopedAsync(sp => AuthorBackfill.AttachmentsCountAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        if (before == 0)
        {
            logger.LogInformation("Author id backfill: every imported attachment already has an author id");
            return;
        }

        var connections = await ScopedAsync(sp => AuthorBackfill.AttachmentConnectionsPendingAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        logger.LogInformation(
            "Author id backfill: {Files} imported attachments without an author id, on {Connections} connection(s)",
            before, connections.Count);

        foreach (var connectionId in connections)
        {
            try
            {
                var (added, removed) = await ScopedAsync(sp => sp.GetRequiredService<IConnectionSyncRunner>()
                    .RefreshAttachmentsAsync(connectionId, ct));
                // The sweep is the one a full sync runs, so it can also bring in a file that never
                // arrived or drop one deleted in the PSA. Logged so a changed file count is explained.
                if (added + removed > 0)
                    logger.LogInformation(
                        "Author id backfill: the attachment sweep also added {Added} and removed {Removed} files on connection {Connection}",
                        added, removed, connectionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Author id backfill: the attachment sweep failed for connection {Connection}", connectionId);
            }
        }

        var after = await ScopedAsync(sp => AuthorBackfill.AttachmentsCountAsync(sp.GetRequiredService<DeskDbContext>(), ct));
        logger.LogInformation(
            "Author id backfill, attachments: {Filled} given an author id, {Left} still without one", before - after, after);
    }
}
