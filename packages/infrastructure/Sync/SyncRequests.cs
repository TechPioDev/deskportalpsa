using Desk.Application.Common;
using Desk.Application.Sync;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>Which connections have a sync asked for that the worker should run.</summary>
public static class SyncRequests
{
    /// <summary>
    /// Asked for, and able to run: a paused connection reads nothing, and an archived one is put
    /// away. Pausing or archiving withdraws the request, so this is the same rule said twice - on
    /// purpose, for a connection changed between the two.
    /// </summary>
    public static IQueryable<PsaConnection> Pending(IQueryable<PsaConnection> connections)
        => connections.Where(c => c.SyncRequestedAt != null && c.SyncPausedAt == null && c.ArchivedAt == null);
}

public sealed class SyncRequestService(DeskDbContext db, TimeProvider clock) : ISyncRequestService
{
    public async Task<SyncRequestDto> RequestAsync(Guid connectionId, bool full, string? requestedBy, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        // Said in words. A run on a paused connection reads nothing, and a request that sat there
        // unanswered would look like a sync that never started.
        if (connection.ArchivedAt is not null)
            throw new ValidationFailedException("This connection is archived. Restore it to read from the PSA again.");
        if (connection.SyncPausedAt is not null)
            throw new ValidationFailedException("Sync is paused for this connection. Resume it to read from the PSA again.");

        // Asked again while the first is waiting or running: nothing changes, and it runs once.
        // The one exception is asking for everything after asking for what changed. That is a new
        // request, with a new time, so the run already under way does not settle it when it ends.
        if (connection.SyncRequestedAt is null || (full && !connection.SyncRequestedFull))
        {
            connection.SyncRequestedAt = clock.GetUtcNow();
            connection.SyncRequestedFull = full;
            var by = requestedBy?.Trim();
            connection.SyncRequestedBy = string.IsNullOrEmpty(by) ? null : by.Length <= 200 ? by : by[..200];
            await db.SaveChangesAsync(ct);
        }
        return new SyncRequestDto(connection.Id, connection.SyncRequestedAt!.Value, connection.SyncRequestedFull);
    }
}

/// <summary>
/// Runs one requested sync and settles the request. The worker gives each a unit of work of its
/// own, as it does a scheduled run.
/// </summary>
public sealed class RequestedSyncRunner(DeskDbContext db, IConnectionSyncRunner runner)
{
    public async Task<RequestedSyncOutcome> RunAsync(Guid connectionId, CancellationToken ct = default)
    {
        var asked = await SyncRequests.Pending(db.PsaConnections.AsNoTracking())
            .Where(c => c.Id == connectionId)
            .Select(c => new { At = c.SyncRequestedAt!.Value, c.SyncRequestedFull, c.SyncRequestedBy })
            .FirstOrDefaultAsync(ct);
        if (asked is null) return RequestedSyncOutcome.NothingAsked;

        SyncRunResult result;
        try
        {
            result = await runner.RunAsync(connectionId,
                new SyncRunRequest(asked.SyncRequestedFull, Manual: true, RequestedBy: asked.SyncRequestedBy), ct);
        }
        catch (ConflictException)
        {
            // The schedule, or another worker, has the connection. One run at a time: the request
            // stays where it is and is come back to.
            return RequestedSyncOutcome.Busy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run recorded why it failed, on itself and on the connection. Running it again
            // every few seconds would be the same failure every few seconds.
            await SettleAsync(connectionId, asked.At, more: false, CancellationToken.None);
            return RequestedSyncOutcome.Failed;
        }

        // More to read because the run used up its pages: carry straight on. More to read because
        // the PSA stopped answering, or asked to be left alone, is the other case - the run stops
        // early after failures in a row - and coming back every few seconds is exactly what it
        // asked not to be done. That is left to the schedule, at the schedule's pace.
        var carryOn = result.MoreToRead && result.Failed == 0;
        await SettleAsync(connectionId, asked.At, carryOn, ct);
        return carryOn ? RequestedSyncOutcome.More : RequestedSyncOutcome.Done;
    }

    private async Task SettleAsync(Guid connectionId, DateTimeOffset askedAt, bool more, CancellationToken ct)
    {
        // After a run that failed the unit of work may still hold what was refused.
        db.ChangeTracker.Clear();
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        // Asked again, and for more, while this ran: that request is a different one and stands.
        if (connection is null || connection.SyncRequestedAt != askedAt) return;

        if (more)
        {
            // A run reads a bounded number of pages. The request stands until the read is done, so
            // a first import is not left waiting for the schedule between each part of it - but as
            // a continuation: a second full run would start again from nothing.
            connection.SyncRequestedFull = false;
        }
        else
        {
            connection.SyncRequestedAt = null;
            connection.SyncRequestedFull = false;
            connection.SyncRequestedBy = null;
        }
        await db.SaveChangesAsync(ct);
    }
}
