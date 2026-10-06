using Desk.Application.Jobs;
using Desk.Domain.Enums;
using Desk.Infrastructure.Jobs;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Desk.Worker;

/// <summary>
/// Takes due background jobs through <see cref="JobQueue"/>, one at a time and each for this
/// worker alone, and runs each through <see cref="JobProcessor"/>, which applies the
/// retry / backoff / dead-letter policy. Runs under platform scope to discover jobs across all
/// tenants; each handler narrows to its job's tenant before touching tenant-scoped data.
/// </summary>
public sealed class BackgroundJobPollingService(
    IServiceProvider services,
    ILogger<BackgroundJobPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background job poller started; interval {Interval}s", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background poll cycle failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();

        var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>();
        var processor = new JobProcessor(db, handlers, clock);

        var queue = new JobQueue(db, clock);
        var due = await queue.DueAsync(20, ct);

        if (due.Count == 0) return;
        logger.LogInformation("Processing {Count} due background job(s)", due.Count);

        foreach (var id in due)
        {
            // Taken before it is run, and saved as taken: another worker reading the same list
            // is refused this one and goes on to the next.
            var job = await queue.ClaimAsync(id, ct);
            if (job is null) continue;
            var result = await processor.ProcessAsync(job, ct);
            if (result == BackgroundJobStatus.DeadLettered)
                logger.LogWarning("Job {JobId} ({Type}) dead-lettered: {Error}", job.Id, job.JobType, job.LastError);
        }
    }
}
