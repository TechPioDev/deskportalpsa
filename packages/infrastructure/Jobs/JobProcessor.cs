using Desk.Application.Jobs;
using Desk.Application.Resilience;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Infrastructure.Persistence;

namespace Desk.Infrastructure.Jobs;

/// <summary>
/// Runs a single background job and applies the retry / backoff / dead-letter policy:
/// on failure the job is re-queued with an exponential-backoff next-attempt time until
/// <see cref="BackgroundJob.MaxAttempts"/> is reached, after which it is dead-lettered for
/// manual reprocessing. A missing handler dead-letters immediately (no point retrying).
/// </summary>
public sealed class JobProcessor(
    DeskDbContext db,
    IEnumerable<IJobHandler> handlers,
    TimeProvider clock,
    RetryPolicy? policy = null)
{
    private readonly RetryPolicy _policy = policy ?? new RetryPolicy();
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(h => h.JobType, StringComparer.Ordinal);

    public async Task<BackgroundJobStatus> ProcessAsync(BackgroundJob job, CancellationToken ct = default)
    {
        // Taken through the queue, it is already marked running and counted as an attempt.
        // Handed straight here, it is marked now.
        if (job.Status != BackgroundJobStatus.Running)
        {
            job.Status = BackgroundJobStatus.Running;
            job.Attempts++;
        }

        try
        {
            if (!_handlers.TryGetValue(job.JobType, out var handler))
            {
                job.Status = BackgroundJobStatus.DeadLettered;
                job.LastError = $"No handler registered for job type '{job.JobType}'.";
                return await SaveAndReturn(job, ct);
            }

            await handler.HandleAsync(job, ct);
            job.Status = BackgroundJobStatus.Succeeded;
            job.LastError = null;
        }
        catch (Exception ex)
        {
            job.LastError = ex.Message;
            if (job.Attempts >= job.MaxAttempts)
            {
                job.Status = BackgroundJobStatus.DeadLettered;
            }
            else
            {
                job.Status = BackgroundJobStatus.Queued;
                job.NextAttemptAt = clock.GetUtcNow() + _policy.ComputeDelay(job.Attempts);
            }
        }

        return await SaveAndReturn(job, ct);
    }

    private async Task<BackgroundJobStatus> SaveAndReturn(BackgroundJob job, CancellationToken ct)
    {
        job.LeaseExpiresAt = null;
        job.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // The handler ran past its lease and another worker took the job. It is theirs to
            // finish and to record; what this one would have written is let go of.
            db.Entry(job).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
        return job.Status;
    }
}
