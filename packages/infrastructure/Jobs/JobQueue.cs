using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Jobs;

/// <summary>
/// Which background jobs are due, and taking one of them for this worker and no other.
///
/// A job used to be taken by reading it: the worker marked it running in memory and saved that
/// only after the handler had returned. Two workers reading at the same moment both ran it, and a
/// worker that stopped mid-job left nothing to say it had ever started. One worker runs today,
/// which is the only reason neither had happened.
///
/// Taking a job is now a save of its own, before the handler runs. The job's version is checked
/// by that save, so of two workers taking the same job one is refused and moves on. The job is
/// held for a lease: a worker that stops leaves a running job whose lease runs out, and the next
/// cycle takes it again. A handler must therefore finish inside the lease, and must be safe to
/// run twice - which a job that is retried on failure had to be already.
/// </summary>
public sealed class JobQueue(DeskDbContext db, TimeProvider clock, TimeSpan? lease = null)
{
    /// <summary>How long a worker holds a job it has taken.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _lease = lease ?? DefaultLease;

    /// <summary>
    /// The jobs to try now, oldest first: queued ones whose time has come, and ones a worker took
    /// and never finished. Only their ids - each is read again, and checked again, when it is taken.
    /// </summary>
    public async Task<List<Guid>> DueAsync(int take, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return await db.BackgroundJobs.AsNoTracking()
            .Where(j => (j.Status == BackgroundJobStatus.Queued && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
                        || (j.Status == BackgroundJobStatus.Running && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now)))
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .Take(take)
            .Select(j => j.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Takes the job for this worker: marked running, counted as an attempt, and held for the
    /// lease. Null when it is no longer there to take - another worker has it, or it is done.
    /// </summary>
    public async Task<BackgroundJob?> ClaimAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.BackgroundJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;
        var now = clock.GetUtcNow();

        var abandoned = job.Status == BackgroundJobStatus.Running && (job.LeaseExpiresAt is null || job.LeaseExpiresAt < now);
        var queued = job.Status == BackgroundJobStatus.Queued && (job.NextAttemptAt is null || job.NextAttemptAt <= now);
        if (!abandoned && !queued)
        {
            db.Entry(job).State = EntityState.Detached;
            return null;
        }

        if (abandoned && job.Attempts >= job.MaxAttempts)
        {
            // Every attempt it had ended with the worker stopping. Running it again is how a job
            // that brings its worker down goes on doing so for ever.
            job.Status = BackgroundJobStatus.DeadLettered;
            job.LeaseExpiresAt = null;
            job.LastError = "The worker stopped before this job finished, each time it was tried.";
        }
        else
        {
            job.Status = BackgroundJobStatus.Running;
            job.Attempts++;
            job.LeaseExpiresAt = now + _lease;
        }
        job.Version++;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker saved its claim first. The job is theirs.
            db.Entry(job).State = EntityState.Detached;
            return null;
        }
        return job.Status == BackgroundJobStatus.Running ? job : null;
    }
}
