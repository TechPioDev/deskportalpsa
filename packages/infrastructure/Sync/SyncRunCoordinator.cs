using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>How the inbound sync paces itself. One place, so a test can shrink every number.</summary>
public sealed record SyncOptions
{
    public static SyncOptions Default { get; } = new();

    public int PageSize { get; init; } = 100;

    /// <summary>Pages one run reads before it stops and leaves the rest to the next run.</summary>
    public int MaxPagesPerRun { get; init; } = 50;

    /// <summary>
    /// How far behind a run's start the next read begins. Covers a clock that differs a little from
    /// the PSA's, and a change stamped just before the run began. Re-reading a minute of tickets is
    /// free: an unchanged one is a no-op.
    /// </summary>
    public TimeSpan Overlap { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The overlap for a connection's first run under cursors. Its old cursor was the moment a run
    /// ENDED, so anything changed while that run was reading may never have been asked for.
    /// </summary>
    public TimeSpan FirstRunOverlap { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long a run holds its connection without reporting progress before it is taken to be dead.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Earlier failures tried again at the start of a run.</summary>
    public int RetriesPerRun { get; init; } = 50;

    /// <summary>
    /// Failures of the same kind in a row before a run stops asking. Five tickets' notes refused one
    /// after another is the PSA saying no, not five unlucky tickets.
    /// </summary>
    public int FailuresInARow { get; init; } = 5;
}

/// <summary>
/// Starts, extends and ends a connection's sync run. While a run is in progress its row is the
/// connection's lock (a unique index allows one per connection), so the scheduled sync and a manual
/// "Sync now" cannot both be working one connection - which they could, each importing the same
/// notes and files.
/// </summary>
public sealed class SyncRunCoordinator(DeskDbContext db, TimeProvider clock, SyncOptions options)
{
    /// <summary>The new run, or null when another one holds the connection.</summary>
    public async Task<SyncRun?> TryStartAsync(
        PsaConnection connection, SyncRunTrigger trigger, string? requestedBy, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var inProgress = await db.SyncRuns
            .Where(r => r.PsaConnectionId == connection.Id && r.Status == SyncRunStatus.Running)
            .ToListAsync(ct);
        foreach (var other in inProgress)
        {
            if (other.LeaseExpiresAt > now) return null;
            // Its lease lapsed with no finish: the process running it stopped. Whatever it had
            // applied is saved and whatever it had not is read again, so taking over loses nothing.
            other.Status = SyncRunStatus.Abandoned;
            other.FinishedAt = now;
            other.Error = "The process running this sync stopped before it finished.";
        }

        var run = new SyncRun
        {
            MspOrganizationId = connection.MspOrganizationId,
            PsaConnectionId = connection.Id,
            Trigger = trigger,
            Status = SyncRunStatus.Running,
            StartedAt = now,
            LeaseExpiresAt = now + options.Lease,
            RequestedBy = requestedBy,
        };
        db.SyncRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another process recorded its run between the read above and this save. It has the
            // connection; this one stands down.
            db.Entry(run).State = EntityState.Detached;
            foreach (var other in inProgress) await db.Entry(other).ReloadAsync(ct);
            return null;
        }
        return run;
    }

    /// <summary>Pushes the lease forward. Saved with whatever else the caller has pending.</summary>
    public void Extend(SyncRun run) => run.LeaseExpiresAt = clock.GetUtcNow() + options.Lease;
}

/// <summary>
/// Keeps the records a run could not read or apply, so that a failure costs one record instead of
/// the run, is tried again later, and can be seen.
/// </summary>
public sealed class SyncFailureStore(DeskDbContext db, TimeProvider clock)
{
    /// <summary>Attempts before a failure stops being retried by itself and waits for a person.</summary>
    public const int MaxAttempts = 6;

    private const int MaxMessageLength = 500;

    /// <summary>
    /// The failures still open for a connection, keyed by record and operation. Loaded once per run:
    /// it is usually empty, and it lets a success clear its own earlier failure without a query per
    /// ticket.
    /// </summary>
    public async Task<Dictionary<(string ExternalId, string Operation), SyncFailure>> OpenAsync(Guid connectionId, CancellationToken ct)
        => (await db.SyncFailures
                .Where(f => f.PsaConnectionId == connectionId
                            && (f.Status == SyncFailureStatus.Pending || f.Status == SyncFailureStatus.NeedsReview))
                .ToListAsync(ct))
            .GroupBy(f => (f.ExternalId, f.Operation))
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Records a failure, or another attempt at one already open. Not saved: the caller saves.</summary>
    public SyncFailure Record(
        PsaConnection connection, Dictionary<(string, string), SyncFailure> open,
        string externalId, string operation, Exception error)
    {
        var now = clock.GetUtcNow();
        if (!open.TryGetValue((externalId, operation), out var failure))
        {
            failure = new SyncFailure
            {
                MspOrganizationId = connection.MspOrganizationId,
                PsaConnectionId = connection.Id,
                Entity = SyncFailure.Ticket,
                ExternalId = externalId,
                Operation = operation,
                Category = "",
                Message = "",
                FirstFailedAt = now,
            };
            db.SyncFailures.Add(failure);
            open[(externalId, operation)] = failure;
        }

        failure.Attempts++;
        failure.LastFailedAt = now;
        failure.Category = Category(error);
        failure.Message = Describe(error);
        // Tried again only where trying again can help. A refusal - no permission, a request the
        // PSA will not accept - is the same refusal next time, and waits for a person instead.
        var worthRetrying = error is not ConnectorException { IsTransient: false } && failure.Attempts < MaxAttempts;
        failure.Status = worthRetrying ? SyncFailureStatus.Pending : SyncFailureStatus.NeedsReview;
        failure.NextAttemptAt = worthRetrying ? now + Delay(failure.Attempts, (error as ConnectorException)?.RetryAfter) : null;
        return failure;
    }

    /// <summary>Marks a record's open failure as dealt with, if it had one. Not saved: the caller saves.</summary>
    public bool Resolve(Dictionary<(string, string), SyncFailure> open, string externalId, string operation)
    {
        if (!open.Remove((externalId, operation), out var failure)) return false;
        failure.Status = SyncFailureStatus.Resolved;
        failure.ResolvedAt = clock.GetUtcNow();
        failure.NextAttemptAt = null;
        return true;
    }

    /// <summary>Five minutes, doubling, to six hours - and never sooner than the PSA asked.</summary>
    public static TimeSpan Delay(int attempts, TimeSpan? retryAfter = null)
    {
        var backoff = TimeSpan.FromMinutes(Math.Min(5 * Math.Pow(2, Math.Max(0, attempts - 1)), 360));
        return retryAfter is { } asked && asked > backoff ? asked : backoff;
    }

    public static string Category(Exception error) => error is ConnectorException c ? c.Kind.ToString() : "Apply";

    /// <summary>
    /// What went wrong, for an administrator. A connector's message is the PSA's own answer about
    /// this tenant's own record; anything else is reduced to its kind, because an unexpected
    /// exception's text is written for a developer and can quote a query.
    /// </summary>
    public static string Describe(Exception error)
    {
        var text = error switch
        {
            ConnectorException c => c.Message,
            DbUpdateException => "The record could not be saved.",
            _ => $"The record could not be applied ({error.GetType().Name}).",
        };
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength];
    }
}
