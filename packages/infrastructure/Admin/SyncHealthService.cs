
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Sync;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Reads a connection's sync runs, cursor and failed records, under the caller's tenant: another
/// organization's connection is simply not found. Every query names the connection, and the
/// connection is looked up first, so a failure id from elsewhere finds nothing.
/// </summary>
public sealed class SyncHealthService(DeskDbContext db, IAuditWriter audit, TimeProvider clock) : ISyncHealthService
{
    public async Task<SyncStateDto> StateAsync(Guid connectionId, int runs = 20, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(connectionId, ct);
        runs = Math.Clamp(runs, 1, 100);

        var cursor = await db.SyncCursors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PsaConnectionId == connectionId && c.Entity == SyncCursor.Tickets, ct);
        var recent = await db.SyncRuns.AsNoTracking()
            .Where(r => r.PsaConnectionId == connectionId)
            .OrderByDescending(r => r.StartedAt)
            .Take(runs)
            .ToListAsync(ct);
        var open = await db.SyncFailures.AsNoTracking()
            .Where(f => f.PsaConnectionId == connectionId
                        && (f.Status == SyncFailureStatus.Pending || f.Status == SyncFailureStatus.NeedsReview))
            .Select(f => f.Status)
            .ToListAsync(ct);
        var now = clock.GetUtcNow();

        return new SyncStateDto(
            connectionId,
            cursor?.Watermark,
            ReadInProgress: cursor?.Continuation is not null,
            PagesReadSoFar: cursor?.ContinuationPages ?? 0,
            // A run whose lease has lapsed is not running, whatever its row still says.
            Running: recent.Any(r => r.Status == SyncRunStatus.Running && r.LeaseExpiresAt > now),
            OpenFailures: open.Count,
            NeedsReview: open.Count(s => s == SyncFailureStatus.NeedsReview),
            recent.Select(Dto).ToList());
    }

    public async Task<IReadOnlyList<SyncFailureDto>> FailuresAsync(Guid connectionId, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(connectionId, ct);
        var rows = await db.SyncFailures.AsNoTracking()
            .Where(f => f.PsaConnectionId == connectionId
                        && (f.Status == SyncFailureStatus.Pending || f.Status == SyncFailureStatus.NeedsReview))
            .OrderByDescending(f => f.LastFailedAt)
            .Take(500)
            .ToListAsync(ct);
        return rows.Select(Dto).ToList();
    }

    public async Task RetryAsync(Guid connectionId, Guid failureId, CancellationToken ct = default)
    {
        var failure = await OpenFailureAsync(connectionId, failureId, ct);
        failure.Status = SyncFailureStatus.Pending;
        failure.NextAttemptAt = clock.GetUtcNow();
        // A person asking is one more try, not a fresh run of six: if it fails again it comes
        // straight back for review rather than quietly retrying for another day.
        failure.Attempts = Math.Min(failure.Attempts, Sync.SyncFailureStore.MaxAttempts - 1);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("sync.failure.retried", "SyncFailure", failureId.ToString(),
            new { connectionId, failure.Entity, failure.ExternalId, failure.Operation }, ct);
    }

    public async Task DismissAsync(Guid connectionId, Guid failureId, CancellationToken ct = default)
    {
        var failure = await OpenFailureAsync(connectionId, failureId, ct);
        failure.Status = SyncFailureStatus.Dismissed;
        failure.NextAttemptAt = null;
        failure.ResolvedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("sync.failure.dismissed", "SyncFailure", failureId.ToString(),
            new { connectionId, failure.Entity, failure.ExternalId, failure.Operation, failure.Category }, ct);
    }

    private async Task EnsureConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        if (!await db.PsaConnections.AsNoTracking().AnyAsync(c => c.Id == connectionId, ct))
            throw new NotFoundException("PSA connection");
    }

    private async Task<SyncFailure> OpenFailureAsync(Guid connectionId, Guid failureId, CancellationToken ct)
    {
        await EnsureConnectionAsync(connectionId, ct);
        return await db.SyncFailures.FirstOrDefaultAsync(f =>
                f.Id == failureId && f.PsaConnectionId == connectionId
                && (f.Status == SyncFailureStatus.Pending || f.Status == SyncFailureStatus.NeedsReview), ct)
            ?? throw new NotFoundException("Sync failure");
    }

    private static SyncRunDto Dto(SyncRun r) => new(
        r.Id, r.Trigger.ToString(), r.Status.ToString(), r.StartedAt, r.FinishedAt,
        r.Fetched, r.Created, r.Updated, r.Skipped, r.Pages, r.Notes, r.Attachments,
        r.FailedRecords, r.Retried, r.Recovered, r.Error, r.Notice, r.RequestedBy);

    private static SyncFailureDto Dto(SyncFailure f) => new(
        f.Id, f.Entity, f.ExternalId, f.Operation, f.Category, f.Message,
        f.Attempts, f.FirstFailedAt, f.LastFailedAt, f.NextAttemptAt, f.Status.ToString());
}
