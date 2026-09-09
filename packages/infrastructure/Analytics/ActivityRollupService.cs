using Desk.Application.Analytics;
using Desk.Domain.Analytics;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Analytics;

/// <summary>
/// Rolls the activity log into daily facts and expires the raw rows behind them.
///
/// Runs under PLATFORM scope: one pass covers every tenant, and the facts it writes carry the
/// tenant of the events they came from, so the global filter still isolates them on read.
/// </summary>
public sealed class ActivityRollupService(DeskDbContext db, TimeProvider clock) : IActivityRollupService
{
    /// <summary>
    /// How far back each pass rebuilds unconditionally. PSA data arrives late — a closure observed
    /// today can carry a timestamp from days ago — so recent history is rebuilt rather than appended
    /// to. Seven days is comfortably longer than any sync lag seen here and still cheap.
    ///
    /// It is NOT the limit of what a pass will build. A rolling window alone can only ever describe
    /// the recent past: an event already older than the window the first time the rollup sees it
    /// never becomes a fact at all. For PSA data that is the normal case rather than an edge one — a
    /// widened import brings in months of closures at once, every one of them dated when it
    /// happened. Days holding events but no facts are rebuilt too, however old.
    /// </summary>
    private const int RecomputeDays = 7;

    /// <summary>
    /// How long raw events are kept. Thirteen months so a year-on-year comparison still has the
    /// detail behind it; the facts themselves are kept indefinitely, because they are small.
    /// </summary>
    private const int RawRetentionDays = 396;

    /// <summary>Rows deleted per statement, and the ceiling on one pass. A large backlog drains
    /// across successive runs rather than holding the table for the length of one.</summary>
    private const int ExpiryBatchSize = 500;
    private const int MaxExpiryBatches = 40;

    public async Task<ActivityRollupResult> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var firstDay = today.AddDays(-RecomputeDays);
        var retentionFloor = now.AddDays(-RawRetentionDays);

        // Which days to rebuild: the rolling window, plus every day holding raw events that have no
        // facts to show for them. That second half is what makes a backfill visible — without it an
        // import recovering months of history leaves the dashboards exactly as they were.
        //
        // Both queries are deliberately dull. The tests run on the in-memory provider, which happily
        // executes LINQ that Npgsql cannot translate, so anything clever here would pass every test
        // and throw in production.
        var eventTimes = await db.ActivityEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OccurredAt >= retentionFloor)
            .Select(e => e.OccurredAt)
            .ToListAsync(ct);
        var eventDays = eventTimes.Select(t => DateOnly.FromDateTime(t.UtcDateTime.Date)).ToHashSet();

        var factDays = (await db.ActivityDailyFacts.IgnoreQueryFilters().AsNoTracking()
            .Select(f => f.Day)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var targetDays = new HashSet<DateOnly>(eventDays.Where(d => !factDays.Contains(d)));
        for (var d = firstDay; d <= today; d = d.AddDays(1)) targetDays.Add(d);

        // One bounded range for the reads, then the exact day set in memory. A day inside that range
        // which is NOT being rebuilt has to keep its facts, so the filter is exact on the way out as
        // well as the way in.
        var rangeFirst = targetDays.Min();
        var rangeLast = targetDays.Max();
        var rangeStart = rangeFirst.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rangeEndExclusive = rangeLast.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // Portal events name a portal user; the facts are keyed on the PSA identity so both halves
        // aggregate on the same axis. Someone unmapped resolves to null rather than a guess.
        var externalIdByUser = await db.UserPsaIdentities.IgnoreQueryFilters().AsNoTracking()
            .Select(i => new { i.AppUserId, i.ExternalTechnicianId })
            .ToListAsync(ct);
        var actorLookup = externalIdByUser
            .GroupBy(i => i.AppUserId)
            .ToDictionary(g => g.Key, g => g.First().ExternalTechnicianId);
        // The reverse direction too, so PSA-observed work also lands on a portal person where we
        // know who they are. Without it a linked technician's synced activity and portal activity
        // would aggregate on different axes and never add up to one number.
        var userByExternalId = externalIdByUser
            .GroupBy(i => i.ExternalTechnicianId)
            .ToDictionary(g => g.Key, g => g.First().AppUserId);

        var events = (await db.ActivityEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OccurredAt >= rangeStart && e.OccurredAt < rangeEndExclusive)
            .Select(e => new
            {
                e.MspOrganizationId, e.OccurredAt, e.Source, e.ActorUserId, e.ActorExternalId,
                e.ClientCompanyId, e.DurationSeconds,
            })
            .ToListAsync(ct))
            .Where(e => targetDays.Contains(DateOnly.FromDateTime(e.OccurredAt.UtcDateTime.Date)))
            .ToList();

        // Delete-then-insert for the window. The alternative — a unique index and upserts — looks
        // tidier and is a trap here: three of the grain's columns are nullable, and Postgres treats
        // NULLs as distinct, so the index would not actually prevent the duplicates it appears to.
        var stale = (await db.ActivityDailyFacts.IgnoreQueryFilters()
                .Where(f => f.Day >= rangeFirst && f.Day <= rangeLast)
                .ToListAsync(ct))
            .Where(f => targetDays.Contains(f.Day))
            .ToList();
        db.ActivityDailyFacts.RemoveRange(stale);

        var facts = events
            .GroupBy(e => new
            {
                e.MspOrganizationId,
                Day = DateOnly.FromDateTime(e.OccurredAt.UtcDateTime.Date),
                e.Source,
                Actor = e.ActorExternalId
                    ?? (e.ActorUserId is { } uid ? actorLookup.GetValueOrDefault(uid) : null),
                // Whoever acted, as a portal person: the event's own user if it had one, else the
                // portal account behind the PSA id it carried.
                ActorUser = e.ActorUserId
                    ?? (e.ActorExternalId is { } ext ? userByExternalId.GetValueOrDefault(ext) : null),
                e.ClientCompanyId,
            })
            .Select(g => new ActivityDailyFact
            {
                MspOrganizationId = g.Key.MspOrganizationId,
                Day = g.Key.Day,
                Source = g.Key.Source,
                ActorExternalId = g.Key.Actor,
                ActorAppUserId = g.Key.ActorUser,
                ClientCompanyId = g.Key.ClientCompanyId,
                EventCount = g.Count(),
                DurationSeconds = g.Sum(e => e.DurationSeconds ?? 0),
            })
            .ToList();

        db.ActivityDailyFacts.AddRange(facts);
        await db.SaveChangesAsync(ct);

        return new ActivityRollupResult(targetDays.Count, facts.Count, await ExpireAsync(now, ct));
    }

    /// <summary>
    /// Drops raw events past the retention horizon, in batches.
    ///
    /// Always AFTER the rollup, never before: dropping events that had not been aggregated would
    /// lose exactly the history the facts exist to preserve.
    ///
    /// Batched rather than one set-based statement. A single ExecuteDelete would be faster but is
    /// unsupported by the in-memory provider the tests run on — and a path that DELETES DATA is the
    /// last one to leave unverified. Batching also bounds how long one pass holds locks on a table
    /// the dashboards read.
    /// </summary>
    private async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(-RawRetentionDays);
        var expired = 0;
        for (var batch = 0; batch < MaxExpiryBatches; batch++)
        {
            var doomed = await db.ActivityEvents.IgnoreQueryFilters()
                .Where(e => e.OccurredAt < horizon)
                .Take(ExpiryBatchSize)
                .ToListAsync(ct);
            if (doomed.Count == 0) break;

            db.ActivityEvents.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            expired += doomed.Count;
        }

        return expired;
    }
}
