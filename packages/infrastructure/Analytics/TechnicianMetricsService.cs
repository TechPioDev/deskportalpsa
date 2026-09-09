using Desk.Application.Analytics;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Analytics;

public sealed class TechnicianMetricsService(DeskDbContext db, IProductivityScorer scorer, TimeProvider clock)
    : ITechnicianMetricsService
{
    /// <summary>Lightweight projection of the ticket fields the metrics need.</summary>
    private sealed record Row(
        Guid Id, string? Tech, Guid? AppUserId, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt,
        DateTimeOffset? ClosedAt, DateTimeOffset? SlaDueAt, decimal Worked, decimal Billable,
        decimal NonBillable, bool HasNote);

    private async Task<List<Row>> LoadAsync(MetricsFilter f, CancellationToken ct)
    {
        // Date filters and ticket age both run off the PSA's raise date, falling back to the row's
        // own timestamp only where the provider gave none. Using the row timestamp as the primary
        // measured from the day the portal IMPORTED the ticket — so a two-month-old ticket looked
        // hours old, and "average resolution time" quietly reported the rollout, not the service.
        var q = db.Tickets.AsNoTracking().AsQueryable();
        if (f.From is { } from) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) >= from);
        if (f.To is { } to) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) <= to);
        if (f.TechnicianExternalId is { } tech) q = q.Where(t => t.AssignedTechnicianExternalId == tech);
        if (f.AppUserId is { } assignee) q = q.Where(t => t.AssignedAppUserId == assignee);
        if (f.ClientCompanyId is { } company) q = q.Where(t => t.ClientCompanyId == company);
        if (f.PsaConnectionId is { } conn) q = q.Where(t => t.PsaConnectionId == conn);
        if (f.Priority is { } prio) q = q.Where(t => t.PortalPriority == prio);

        return await q.Select(t => new Row(
            t.Id, t.AssignedTechnicianExternalId, t.AssignedAppUserId,
            t.PsaCreatedAt ?? t.CreatedAt, t.ResolvedAt, t.ClosedAt, t.SlaDueAt,
            t.TimeWorkedHours, t.BillableHours, t.NonBillableHours,
            t.Notes.Any(n => n.IsPublic))).ToListAsync(ct);
    }

    public async Task<TechnicianMetrics> ForTechnicianAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default)
        => Compute(filter.TechnicianExternalId ?? "(all)", await LoadAsync(filter, ct), weights);

    public async Task<IReadOnlyList<TeamComparisonRow>> TeamAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default)
    {
        var rows = await LoadAsync(filter, ct);

        // Grouped by whoever is actually working the ticket, preferring the PORTAL assignee. On a
        // desk where technicians exist only here, the provider's id is the integration account on
        // every row — grouping by it produced one line called "the API user" containing the whole
        // team's work, and dropped every portal-assigned ticket entirely because its Tech was null.
        var names = await NamesByAppUserAsync(rows, ct);

        return rows
            .Where(r => r.AppUserId is not null || r.Tech is not null)
            .GroupBy(r => r.AppUserId is { } uid ? "u:" + uid : "x:" + r.Tech)
            .Select(g =>
            {
                var first = g.First();
                var key = first.AppUserId?.ToString() ?? first.Tech!;
                var m = Compute(key, g.ToList(), weights);
                var name = first.AppUserId is { } uid
                    ? names.GetValueOrDefault(uid)
                    // A PSA-side technician's name is not on the ticket row, so the id stands in.
                    // Better a stable key than a blank cell that looks like missing data.
                    : first.Tech;
                return new TeamComparisonRow(
                    key, m.Resolved, m.SlaCompliancePct, m.Score?.Overall, name, first.AppUserId);
            })
            .OrderByDescending(r => r.Score ?? -1)
            .ToList();
    }

    public async Task<IReadOnlyList<TechnicianDay>> DailyAsync(MetricsFilter filter, CancellationToken ct = default)
    {
        var from = filter.From;
        var to = filter.To;

        // HOURS come from time entries, not from the ticket's worked total. A ticket total is the
        // sum of everyone who touched it, so attributing it to the current assignee would credit
        // one person with a colleague's afternoon - and on reassignment it would move that
        // afternoon to somebody new.
        var entries = db.TicketTimeEntries.AsNoTracking().AsQueryable();
        if (from is { } f) entries = entries.Where(e => e.EntryDate >= f);
        if (to is { } t) entries = entries.Where(e => e.EntryDate <= t);
        if (filter.AppUserId is { } who) entries = entries.Where(e => e.AppUserId == who);
        if (filter.TechnicianExternalId is { } tech) entries = entries.Where(e => e.TechnicianExternalId == tech);

        var loggedRaw = await entries
            .Select(e => new { e.AppUserId, e.TechnicianExternalId, e.EntryDate, e.Hours, e.Billable, e.TicketId })
            .ToListAsync(ct);

        // Resolution counts come from the tickets themselves, attributed to whoever holds them.
        var rows = (await LoadAsync(filter, ct)).Where(r => r.ResolvedAt is not null).ToList();

        var names = await NamesForAsync(
            loggedRaw.Where(e => e.AppUserId is not null).Select(e => e.AppUserId!.Value)
                .Concat(rows.Where(r => r.AppUserId is not null).Select(r => r.AppUserId!.Value))
                .Distinct().ToList(), ct);

        // One key per person across both sources, so a technician who logged hours on Monday and
        // resolved a ticket on Monday produces ONE row for Monday rather than two half-rows.
        var buckets = new Dictionary<(DateOnly Day, string Key), TechnicianDay>();

        TechnicianDay Seed(DateOnly day, Guid? appUserId, string? ext)
        {
            var key = appUserId is { } u ? "u:" + u : "x:" + ext;
            if (buckets.TryGetValue((day, key), out var found)) return found;
            var name = appUserId is { } uid
                ? names.GetValueOrDefault(uid, "Unknown user")
                : ext ?? "Unattributed";
            var seeded = new TechnicianDay(day, appUserId, ext, name, 0m, 0m, 0, 0);
            buckets[(day, key)] = seeded;
            return seeded;
        }

        foreach (var g in loggedRaw
                     .Where(e => e.AppUserId is not null || e.TechnicianExternalId is not null)
                     .GroupBy(e => (
                         Day: DateOnly.FromDateTime(e.EntryDate.UtcDateTime.Date),
                         e.AppUserId,
                         Ext: e.AppUserId is null ? e.TechnicianExternalId : null)))
        {
            var current = Seed(g.Key.Day, g.Key.AppUserId, g.Key.Ext);
            buckets[(g.Key.Day, g.Key.AppUserId is { } u ? "u:" + u : "x:" + g.Key.Ext)] = current with
            {
                Hours = current.Hours + g.Sum(e => e.Hours),
                BillableHours = current.BillableHours + g.Where(e => e.Billable).Sum(e => e.Hours),
                TicketsTouched = current.TicketsTouched + g.Select(e => e.TicketId).Distinct().Count(),
            };
        }

        foreach (var g in rows.GroupBy(r => (
                     Day: DateOnly.FromDateTime(r.ResolvedAt!.Value.UtcDateTime.Date),
                     r.AppUserId,
                     Ext: r.AppUserId is null ? r.Tech : null)))
        {
            var current = Seed(g.Key.Day, g.Key.AppUserId, g.Key.Ext);
            buckets[(g.Key.Day, g.Key.AppUserId is { } u ? "u:" + u : "x:" + g.Key.Ext)] =
                current with { Resolved = current.Resolved + g.Count() };
        }

        return buckets.Values.OrderBy(d => d.Date).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Display names for the portal assignees in this result set, in one round trip.</summary>
    private Task<Dictionary<Guid, string>> NamesByAppUserAsync(List<Row> rows, CancellationToken ct)
        => NamesForAsync(rows.Where(r => r.AppUserId is not null).Select(r => r.AppUserId!.Value).Distinct().ToList(), ct);

    private async Task<Dictionary<Guid, string>> NamesForAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        return await db.AppUsers.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<IReadOnlyList<TrendPoint>> TrendAsync(MetricsFilter filter, CancellationToken ct = default)
    {
        var rows = await LoadAsync(filter, ct);
        var created = rows.GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());
        var resolved = rows.Where(r => r.ResolvedAt is not null)
            .GroupBy(r => DateOnly.FromDateTime(r.ResolvedAt!.Value.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());

        return created.Keys.Union(resolved.Keys)
            .OrderBy(d => d)
            .Select(d => new TrendPoint(d, created.GetValueOrDefault(d), resolved.GetValueOrDefault(d)))
            .ToList();
    }

    private TechnicianMetrics Compute(string tech, List<Row> rows, ProductivityWeights weights)
    {
        var now = clock.GetUtcNow();
        var assigned = rows.Count;
        var resolvedRows = rows.Where(r => r.ResolvedAt is not null).ToList();
        var resolved = resolvedRows.Count;
        var open = rows.Count(r => r.ResolvedAt is null && r.ClosedAt is null);
        var overdue = rows.Count(r => r.ResolvedAt is null && r.SlaDueAt is { } d && d < now);

        var slaEligible = resolvedRows.Count(r => r.SlaDueAt is not null);
        var withinSla = resolvedRows.Count(r => r.SlaDueAt is { } d && r.ResolvedAt <= d);
        var slaPct = slaEligible > 0 ? Math.Round(100.0 * withinSla / slaEligible, 1) : 0;

        var avgResolutionHours = resolved > 0
            ? Math.Round(resolvedRows.Average(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalHours), 1)
            : 0;

        // Proxy component scores from measurable ticket data. CSAT / first-response / reopen are not
        // tracked yet and remain unmeasured (excluded from the weighted score via renormalization).
        var components = new ProductivityComponents
        {
            SlaCompliance = slaEligible > 0 ? slaPct : null,
            ResolutionRate = assigned > 0 ? Math.Round(100.0 * resolved / assigned, 1) : null,
            WorklogQuality = resolved > 0 ? Math.Round(100.0 * resolvedRows.Count(r => r.Worked > 0) / resolved, 1) : null,
            DocumentationQuality = resolved > 0 ? Math.Round(100.0 * resolvedRows.Count(r => r.HasNote) / resolved, 1) : null,
        };

        return new TechnicianMetrics
        {
            TechnicianExternalId = tech,
            Assigned = assigned,
            Resolved = resolved,
            Open = open,
            Overdue = overdue,
            WithinSla = withinSla,
            SlaEligible = slaEligible,
            SlaCompliancePct = slaPct,
            AvgResolutionHours = avgResolutionHours,
            TimeWorkedHours = rows.Sum(r => r.Worked),
            BillableHours = rows.Sum(r => r.Billable),
            NonBillableHours = rows.Sum(r => r.NonBillable),
            Components = components,
            Score = scorer.Calculate(components, weights),
        };
    }
}
