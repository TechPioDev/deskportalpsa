using Desk.Application.Marketing;
using Desk.Infrastructure.Tenancy;

namespace Desk.Worker;

/// <summary>
/// Deletes public enquiries once they are past the retention period the privacy policy publishes.
///
/// This job exists because of the words, not the other way round. The policy names a period, and a
/// named period that nothing enforces is a false statement rather than an unimplemented feature —
/// the page was deliberately left saying "kept until we delete them" until this ran.
///
/// Daily, at a low hour. Retention is measured in months, so a pass is either a day early or a day
/// late and neither matters; running it hourly would be twenty-four times the queries to delete the
/// same rows. It sweeps by AGE, not by a watermark, so a missed day repairs itself on the next pass
/// and there is no state to get wrong.
///
/// Enquiries are the only thing purged here. Audit records are append-only on purpose and are a
/// security control, not correspondence; deleting them is a decision for whoever owns that policy,
/// and it should not arrive as a side effect of a job named after enquiries.
/// </summary>
public sealed class EnquiryRetentionBackgroundService(
    IServiceProvider services,
    ILogger<EnquiryRetentionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// A short wait before the first pass, so a restart loop cannot turn a deletion job into a
    /// tight loop of deletions, and so the first thing a fresh container does is not a write.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = services.CreateScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<EnquiryRetentionPolicy>();
            if (!policy.Enabled)
            {
                // Said once, loudly, at startup. An operator who turned this off should be able to
                // confirm it from the log rather than by waiting a day to see nothing happen — and
                // if it is off by accident, this is the line that says so before the policy page
                // starts promising a deletion nobody is performing.
                logger.LogWarning(
                    "Enquiry retention is DISABLED (Enquiries:RetentionMonths = {Months}); enquiries "
                    + "are kept indefinitely. The privacy policy must not state a retention period.",
                    policy.Months);
                return;
            }

            logger.LogInformation(
                "Enquiry retention started; deleting enquiries older than {Months} months, every {Hours}h",
                policy.Months, Interval.TotalHours);
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down before the first pass
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                // Enquiries carry no tenant — one arrives before any tenant relationship exists —
                // but the scope is set anyway so this behaves like every other worker pass and does
                // not become the one place a future tenant-scoped read silently returns nothing.
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();

                var deleted = await scope.ServiceProvider
                    .GetRequiredService<IEnquiryService>()
                    .PurgeExpiredAsync(stoppingToken);

                // Logged at zero too. "Did the purge run" is a question an auditor asks on a quiet
                // month, and it must not be answerable only when something was deleted.
                logger.LogInformation("Enquiry retention: {Deleted} expired enquiries deleted", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The next pass deletes the same rows plus a day's worth, so a transient failure
                // costs nothing but a day. Stopping the loop would quietly end retention forever.
                logger.LogError(ex, "Enquiry retention pass failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
