using Desk.Application.Jobs;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Infrastructure.Jobs;
using Desk.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Taking a background job for one worker and no other.
///
/// A job was taken by reading it, and marked running only in memory until its handler had
/// returned: two workers reading at the same moment both ran it, and a worker that stopped left
/// nothing to say the job had started. Each worker here is a unit of work of its own over the
/// same database, as each is in life.
/// </summary>
public class JobQueueTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class Handler(string type, Action? onHandle = null) : IJobHandler
    {
        public int Ran { get; private set; }
        public string JobType => type;
        public Task HandleAsync(BackgroundJob job, CancellationToken ct = default)
        {
            Ran++;
            onHandle?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed record World(string DbName, TestClock Clock)
    {
        public DeskDbContext Worker() => TestDbContextFactory.ForPlatform(DbName);

        public async Task<BackgroundJob> ReadAsync(Guid id)
        {
            await using var db = Worker();
            return await db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        }
    }

    private static async Task<(World W, Guid JobId)> QueuedAsync(int maxAttempts = 3, DateTimeOffset? notBefore = null)
    {
        var w = new World(Guid.NewGuid().ToString(), new TestClock());
        var job = new BackgroundJob
        {
            MspOrganizationId = Org, JobType = "t", PayloadJson = "{}", Status = BackgroundJobStatus.Queued,
            MaxAttempts = maxAttempts, NextAttemptAt = notBefore,
        };
        await using var db = w.Worker();
        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync();
        return (w, job.Id);
    }

    [Fact]
    public async Task A_job_is_taken_by_one_worker_and_is_then_not_there_for_another()
    {
        var (w, id) = await QueuedAsync();
        await using var first = w.Worker();
        await using var second = w.Worker();

        var taken = await new JobQueue(first, w.Clock).ClaimAsync(id);

        taken.Should().NotBeNull();
        (taken!.Status, taken.Attempts, taken.LeaseExpiresAt).Should().Be((BackgroundJobStatus.Running, 1, w.Clock.GetUtcNow() + JobQueue.DefaultLease));
        var other = new JobQueue(second, w.Clock);
        (await other.DueAsync(20)).Should().BeEmpty("it is running, and its worker still holds it");
        (await other.ClaimAsync(id)).Should().BeNull();
        (await w.ReadAsync(id)).Attempts.Should().Be(1, "it was counted once");
    }

    [Fact]
    public async Task Of_two_workers_that_both_read_a_job_as_due_one_is_refused_it()
    {
        // The moment that mattered: both have read the job as queued, and neither has saved yet.
        var (w, id) = await QueuedAsync();
        await using var first = w.Worker();
        await using var second = w.Worker();
        (await new JobQueue(first, w.Clock).DueAsync(20)).Should().Equal(id);
        (await new JobQueue(second, w.Clock).DueAsync(20)).Should().Equal(id);
        // The second has it in hand as it was, queued.
        (await second.BackgroundJobs.SingleAsync(j => j.Id == id)).Status.Should().Be(BackgroundJobStatus.Queued);

        var one = await new JobQueue(first, w.Clock).ClaimAsync(id);
        var two = await new JobQueue(second, w.Clock).ClaimAsync(id);

        one.Should().NotBeNull();
        two.Should().BeNull("the first worker's claim was saved, and the second's save is refused");
        (await w.ReadAsync(id)).Attempts.Should().Be(1);
        second.ChangeTracker.Entries<BackgroundJob>().Should().BeEmpty("what it could not have, it lets go of");
    }

    [Fact]
    public async Task A_job_that_is_not_due_yet_is_left_alone()
    {
        var clock = new TestClock();
        var (w, id) = await QueuedAsync(notBefore: clock.GetUtcNow().AddMinutes(10));
        await using var db = w.Worker();
        var queue = new JobQueue(db, w.Clock);

        (await queue.DueAsync(20)).Should().BeEmpty();
        (await queue.ClaimAsync(id)).Should().BeNull();

        w.Clock.Advance(TimeSpan.FromMinutes(11));
        (await queue.DueAsync(20)).Should().Equal(id);
        (await queue.ClaimAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_job_whose_worker_stopped_is_taken_again_once_its_lease_has_run_out()
    {
        var (w, id) = await QueuedAsync();
        await using (var died = w.Worker())
            (await new JobQueue(died, w.Clock).ClaimAsync(id)).Should().NotBeNull();
        // That worker is gone: the job is marked running, and nothing will ever finish it.

        await using var next = w.Worker();
        var queue = new JobQueue(next, w.Clock);
        w.Clock.Advance(JobQueue.DefaultLease - TimeSpan.FromSeconds(1));
        (await queue.DueAsync(20)).Should().BeEmpty("it may still be running");

        w.Clock.Advance(TimeSpan.FromSeconds(2));
        (await queue.DueAsync(20)).Should().Equal(id);
        var again = await queue.ClaimAsync(id);
        (again!.Status, again.Attempts).Should().Be((BackgroundJobStatus.Running, 2));
    }

    [Fact]
    public async Task A_job_that_stops_its_worker_every_time_is_set_aside_and_not_run_for_ever()
    {
        var (w, id) = await QueuedAsync(maxAttempts: 2);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var died = w.Worker();
            (await new JobQueue(died, w.Clock).ClaimAsync(id))!.Attempts.Should().Be(attempt);
            w.Clock.Advance(JobQueue.DefaultLease + TimeSpan.FromSeconds(1));
        }

        await using var db = w.Worker();
        (await new JobQueue(db, w.Clock).ClaimAsync(id)).Should().BeNull("it has had every attempt it is allowed");

        var job = await w.ReadAsync(id);
        (job.Status, job.Attempts, job.LeaseExpiresAt).Should().Be((BackgroundJobStatus.DeadLettered, 2, null));
        job.LastError.Should().Contain("worker stopped");
        (await new JobQueue(db, w.Clock).DueAsync(20)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_job_taken_and_run_is_counted_once_and_no_longer_held()
    {
        var (w, id) = await QueuedAsync();
        await using var db = w.Worker();
        var handler = new Handler("t");
        var job = await new JobQueue(db, w.Clock).ClaimAsync(id);

        (await new JobProcessor(db, [handler], w.Clock).ProcessAsync(job!)).Should().Be(BackgroundJobStatus.Succeeded);

        var done = await w.ReadAsync(id);
        (done.Status, done.Attempts, done.LeaseExpiresAt, handler.Ran).Should().Be((BackgroundJobStatus.Succeeded, 1, null, 1));
    }

    [Fact]
    public async Task A_job_that_failed_goes_back_in_the_queue_and_is_taken_again_when_its_time_comes()
    {
        var (w, id) = await QueuedAsync();
        await using var db = w.Worker();
        var queue = new JobQueue(db, w.Clock);
        var failing = new Handler("t", () => throw new InvalidOperationException("nope"));

        (await new JobProcessor(db, [failing], w.Clock).ProcessAsync((await queue.ClaimAsync(id))!)).Should().Be(BackgroundJobStatus.Queued);
        var waiting = await w.ReadAsync(id);
        (waiting.Attempts, waiting.LeaseExpiresAt).Should().Be((1, null));
        (await queue.DueAsync(20)).Should().BeEmpty("it waits out its backoff");

        w.Clock.Advance(TimeSpan.FromHours(1));
        (await queue.DueAsync(20)).Should().Equal(id);
        (await queue.ClaimAsync(id))!.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_worker_that_ran_past_its_lease_does_not_write_over_the_one_that_took_the_job_from_it()
    {
        var (w, id) = await QueuedAsync();
        await using var slow = w.Worker();
        await using var next = w.Worker();
        var mine = await new JobQueue(slow, w.Clock).ClaimAsync(id);

        // The first worker is still going when its lease runs out. Another takes the job and finishes it.
        w.Clock.Advance(JobQueue.DefaultLease + TimeSpan.FromSeconds(1));
        var theirs = await new JobQueue(next, w.Clock).ClaimAsync(id);
        (await new JobProcessor(next, [new Handler("t")], w.Clock).ProcessAsync(theirs!)).Should().Be(BackgroundJobStatus.Succeeded);

        // Then the first comes back, having failed.
        var failing = new Handler("t", () => throw new InvalidOperationException("too late"));
        await new JobProcessor(slow, [failing], w.Clock).ProcessAsync(mine!);

        var job = await w.ReadAsync(id);
        (job.Status, job.Attempts, job.LastError).Should().Be((BackgroundJobStatus.Succeeded, 2, null), "the job was the second worker's to record");
    }
}
