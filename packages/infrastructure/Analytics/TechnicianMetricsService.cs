using Desk.Application.Analytics;
using Desk.Domain.Tickets;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Analytics;

public sealed class TechnicianMetricsService(DeskDbContext db, IProductivityScorer scorer, TimeProvider clock)
    : ITechnicianMetricsService
{
    /// <summary>Lightweight projection of the ticket fields the metrics need.</summary>
    private sealed record Row(
        Guid Id, string? Tech, Guid? AppUserId, string? TechName, DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt, DateTimeOffset? SlaDueAt,
        decimal Worked, decimal Billable, decimal NonBillable, bool HasNote, Guid? Conn, TicketOrigin Origin,
        DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt, int ReopenCount, int? Rating,
        DateTimeOffset? ReviewedAt, int ReviewSendBacks);

    /// <param name="byResolution">
    /// Window on WHEN THE TICKET WAS RESOLVED instead of when it was raised, for "resolved in this
    /// range" figures. On the raise date a ticket raised last month and resolved yesterday never
    /// counts as a resolution anywhere - so a week of closing out old work reported zero.
    /// </param>
    private async Task<List<Row>> LoadAsync(MetricsFilter f, CancellationToken ct, bool byResolution = false)
    {
        // Date filters and ticket age both run off the PSA's raise date, falling back to the row's
        // own timestamp only where the provider gave none. Using the row timestamp as the primary
        // measured from the day the portal IMPORTED the ticket — so a two-month-old ticket looked
        // hours old, and "average resolution time" quietly reported the rollout, not the service.
        var q = db.Tickets.AsNoTracking().AsQueryable();
        if (byResolution)
        {
            q = q.Where(t => t.ResolvedAt != null);
            if (f.From is { } resolvedFrom) q = q.Where(t => t.ResolvedAt >= resolvedFrom);
            if (f.To is { } resolvedTo) q = q.Where(t => t.ResolvedAt <= resolvedTo);
        }
        else
        {
            if (f.From is { } from) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) >= from);
            if (f.To is { } to) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) <= to);
        }
        // Whose ticket it is, for every figure: whoever resolved it in the portal; else whoever is
        // working it in the portal; else the PSA login it is assigned to, as the portal user that
        // login is linked to. A desk with one PSA login and a team in the portal credits each person
        // with their own work - not the whole team's to the one login.
        if (f.AppUserId is { } me)
        {
            var myTech = f.EitherIdentity ? f.TechnicianExternalId : null;
            q = q.Where(t => (t.ResolvedByAppUserId ?? t.AssignedAppUserId) == me
                || ((t.ResolvedByAppUserId ?? t.AssignedAppUserId) == null && t.AssignedTechnicianExternalId != null
                    && ((myTech != null && t.AssignedTechnicianExternalId == myTech)
                        || db.UserPsaIdentities.Any(i => i.AppUserId == me && i.PsaConnectionId == t.PsaConnectionId
                            && i.ExternalTechnicianId == t.AssignedTechnicianExternalId))
                    // Never the integration account: it holds the whole team's work, not one person's.
                    && !db.PsaConnections.Any(c => c.Id == t.PsaConnectionId && c.DefaultTimeEntryResourceId == t.AssignedTechnicianExternalId)));
        }
        else if (f.TechnicianExternalId is { } tech)
            // A PSA login's own figures: only what nobody here took on or finished.
            q = q.Where(t => t.ResolvedByAppUserId == null && t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == tech);
        if (f.ClientCompanyId is { } company) q = q.Where(t => t.ClientCompanyId == company);
        if (f.PsaOnly) q = q.Where(t => t.Origin == TicketOrigin.Psa);
        if (f.PsaConnectionId is { } conn) q = q.Where(t => t.PsaConnectionId == conn);
        if (f.Priority is { } prio) q = q.Where(t => t.PortalPriority == prio);

        var rows = await q.Select(t => new Row(
            t.Id, t.AssignedTechnicianExternalId, t.ResolvedByAppUserId ?? t.AssignedAppUserId,
            // The provider's own display name, already cached on the ticket by the sync. Without it
            // a PSA-side technician shows as a bare id - "29682889" in a table headed Technician
            // Performance, which nobody can read as a person.
            t.AssignedTechnicianName,
            t.PsaCreatedAt ?? t.CreatedAt, t.ResolvedAt, t.ClosedAt, t.SlaDueAt,
            t.TimeWorkedHours, t.BillableHours, t.NonBillableHours,
            t.Notes.Any(n => n.IsPublic), t.PsaConnectionId, t.Origin,
            t.FirstResponseDueAt, t.FirstRespondedAt, t.ReopenCount,
            db.TicketSatisfactions.Where(x => x.TicketId == t.Id).Select(x => (int?)x.Rating).FirstOrDefault(),
            t.ReviewedAt, t.ReviewSendBacks)).ToListAsync(ct);

        // Nobody here took it on: a PSA login that is linked to a portal user is that person.
        var links = await PsaLinks.LoadAsync(db, ct);
        return rows.Select(r => r.AppUserId is null && links.UserFor(r.Conn, r.Tech) is { } linked ? r with { AppUserId = linked } : r).ToList();
    }

    private Task<Dictionary<Guid, string>> ConnectionNamesAsync(CancellationToken ct)
        => db.PsaConnections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);

    public async Task<TechnicianMetrics> ForTechnicianAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default)
        => Compute(filter.TechnicianExternalId ?? filter.AppUserId?.ToString() ?? "(all)", await LoadAsync(filter, ct), weights,
            await ConnectionNamesAsync(ct));

    public async Task<IReadOnlyList<TeamComparisonRow>> TeamAsync(MetricsFilter filter, ProductivityWeights weights, CancellationToken ct = default)
    {
        var rows = await LoadAsync(filter, ct);

        // Grouped by whoever is actually working the ticket, preferring the PORTAL assignee. On a
        // desk where technicians exist only here, the provider's id is the integration account on
        // every row — grouping by it produced one line called "the API user" containing the whole
        // team's work, and dropped every portal-assigned ticket entirely because its Tech was null.
        var names = await NamesByAppUserAsync(rows, ct);
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var connections = await ConnectionNamesAsync(ct);

        return rows
            // A ticket the PSA shows held by the account the portal writes as is held by nobody, and
            // goes where every unassigned ticket goes - out of a table of people.
            .Where(r => r.AppUserId is not null || (r.Tech is not null && !account.IsAccount(r.Conn, r.Tech)))
            .GroupBy(r => r.AppUserId is { } uid ? "u:" + uid : "x:" + r.Tech)
            .Select(g =>
            {
                var first = g.First();
                var key = first.AppUserId?.ToString() ?? first.Tech!;
                var m = Compute(key, g.ToList(), weights, connections);
                var name = first.AppUserId is { } uid
                    ? names.GetValueOrDefault(uid)
                    // A PSA-side technician: the sync cached the provider's display name on the
                    // ticket, so use it. The id only stands in when even that is missing, because a
                    // stable key still beats a blank cell that reads as missing data.
                    : first.TechName ?? first.Tech;
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
        // Only time that was actually recorded: a push the PSA rejected, or one still waiting, is shown
        // on the ticket for a retry but is not an hour the provider holds, and counting it here made the
        // day's figures disagree with the PSA's. Board time is recorded the moment it is logged.
        entries = entries.Where(e => e.SyncStatus == TimeEntrySyncStatus.Synced);
        // An hour is whoever logged it in the portal. Only time with no portal author - entered in the
        // PSA itself - falls to the PSA login, and to the portal user that login is linked to. Time a
        // portal user logged is pushed under some PSA login too, so matching on the login alone
        // credited a colleague's hours to whoever owns it.
        if (filter.AppUserId is { } who)
        {
            var myTech = filter.EitherIdentity ? filter.TechnicianExternalId : null;
            entries = entries.Where(e => e.AppUserId == who
                || (e.AppUserId == null && e.TechnicianExternalId != null
                    && ((myTech != null && e.TechnicianExternalId == myTech)
                        || db.UserPsaIdentities.Any(i => i.AppUserId == who && i.PsaConnectionId == e.Ticket!.PsaConnectionId
                            && i.ExternalTechnicianId == e.TechnicianExternalId))
                    && !db.PsaConnections.Any(c => c.Id == e.Ticket!.PsaConnectionId && c.DefaultTimeEntryResourceId == e.TechnicianExternalId)));
        }
        else if (filter.TechnicianExternalId is { } tech)
            entries = entries.Where(e => e.AppUserId == null && e.TechnicianExternalId == tech);
        // A time entry has no client of its own — it belongs to a ticket, and the ticket has one.
        // Without this the ticket half of the answer narrowed to one client while the HOURS half
        // stayed organization-wide, so a client's row would have shown the whole desk's time.
        if (filter.ClientCompanyId is { } client)
            entries = entries.Where(e => db.Tickets.Any(t => t.Id == e.TicketId && t.ClientCompanyId == client));
        if (filter.PsaOnly)
            entries = entries.Where(e => db.Tickets.Any(t => t.Id == e.TicketId && t.Origin == TicketOrigin.Psa));

        var links = await PsaLinks.LoadAsync(db, ct);
        var loggedRaw = (await entries
            .Select(e => new
            {
                e.AppUserId, e.TechnicianExternalId, e.EntryDate, e.Hours, e.Billable, e.TicketId,
                Conn = e.Ticket!.PsaConnectionId,
                Origin = e.Ticket!.Origin,
            })
            .ToListAsync(ct))
            // Entered in the PSA under a linked login: that person's hour.
            .Select(e => e.AppUserId is null && links.UserFor(e.Conn, e.TechnicianExternalId) is { } linked ? e with { AppUserId = linked } : e)
            .ToList();

        // Resolution counts come from the tickets themselves, attributed to whoever holds them.
        // Counted on the day the resolution landed, so windowed on that day too: a ticket raised before
        // the range and resolved inside it is a resolution in the range.
        var rows = await LoadAsync(filter, ct, byResolution: true);

        // Work the PSA credits to the account the portal writes as belongs to nobody we can name.
        // It stays in the day's totals - as Unattributed - rather than vanishing: the hours happened,
        // only the person is unknown, and a page whose totals shrank would misreport the desk.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        string? Person(Guid? conn, string? ext) => account.IsAccount(conn, ext) ? null : ext;

        // Provider display names for the PSA-side rows, taken from the tickets themselves.
        var psaNames = rows
            .Where(r => r.Tech is not null && r.TechName is not null)
            .GroupBy(r => r.Tech!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TechName!, StringComparer.OrdinalIgnoreCase);

        var names = await NamesForAsync(
            loggedRaw.Where(e => e.AppUserId is not null).Select(e => e.AppUserId!.Value)
                .Concat(rows.Where(r => r.AppUserId is not null).Select(r => r.AppUserId!.Value))
                .Distinct().ToList(), ct);

        // One key per person across both sources, so a technician who logged hours on Monday and
        // resolved a ticket on Monday produces ONE row for Monday rather than two half-rows.
        var buckets = new Dictionary<(DateOnly Day, string Key), TechnicianDay>();

        // A person's own view is one person, whichever identity each hour was logged under.
        var self = filter.EitherIdentity && filter.AppUserId is not null;
        string KeyOf(Guid? appUserId, string? ext) => self ? "self" : appUserId is { } u ? "u:" + u : "x:" + ext;

        TechnicianDay Seed(DateOnly day, Guid? appUserId, string? ext)
        {
            if (self) { appUserId = filter.AppUserId; ext = null; }
            var key = KeyOf(appUserId, ext);
            if (buckets.TryGetValue((day, key), out var found)) return found;
            var name = appUserId is { } uid
                ? names.GetValueOrDefault(uid, "Unknown user")
                : psaNames.GetValueOrDefault(ext ?? "", ext ?? "Unattributed");
            var seeded = new TechnicianDay(day, appUserId, ext, name, 0m, 0m, 0, 0, 0m, 0);
            buckets[(day, key)] = seeded;
            return seeded;
        }

        foreach (var g in loggedRaw
                     .Where(e => e.AppUserId is not null || e.TechnicianExternalId is not null)
                     .GroupBy(e => (
                         Day: DateOnly.FromDateTime(e.EntryDate.UtcDateTime.Date),
                         e.AppUserId,
                         Ext: e.AppUserId is null ? Person(e.Conn, e.TechnicianExternalId) : null)))
        {
            var current = Seed(g.Key.Day, g.Key.AppUserId, g.Key.Ext);
            buckets[(g.Key.Day, KeyOf(g.Key.AppUserId, g.Key.Ext))] = current with
            {
                Hours = current.Hours + g.Sum(e => e.Hours),
                BillableHours = current.BillableHours + g.Where(e => e.Billable).Sum(e => e.Hours),
                InternalHours = current.InternalHours + g.Where(e => e.Origin == TicketOrigin.Internal).Sum(e => e.Hours),
                MonitoringHours = current.MonitoringHours + g.Where(e => e.Origin == TicketOrigin.Rmm).Sum(e => e.Hours),
                TicketsTouched = current.TicketsTouched + g.Select(e => e.TicketId).Distinct().Count(),
            };
        }

        foreach (var g in rows.GroupBy(r => (
                     Day: DateOnly.FromDateTime(r.ResolvedAt!.Value.UtcDateTime.Date),
                     r.AppUserId,
                     Ext: r.AppUserId is null ? Person(r.Conn, r.Tech) : null)))
        {
            var current = Seed(g.Key.Day, g.Key.AppUserId, g.Key.Ext);
            buckets[(g.Key.Day, KeyOf(g.Key.AppUserId, g.Key.Ext))] =
                current with
                {
                    Resolved = current.Resolved + g.Count(),
                    ResolvedInternal = current.ResolvedInternal + g.Count(r => r.Origin == TicketOrigin.Internal),
                    ResolvedMonitoring = current.ResolvedMonitoring + g.Count(r => r.Origin == TicketOrigin.Rmm),
                };
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
        // Created on the day a ticket was raised; resolved on the day it was resolved - two windows,
        // because "resolved this week" includes tickets raised long before the week began.
        var raised = await LoadAsync(filter, ct);
        var resolvedRows = await LoadAsync(filter, ct, byResolution: true);
        var created = raised.GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());
        var resolved = resolvedRows
            .GroupBy(r => DateOnly.FromDateTime(r.ResolvedAt!.Value.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());

        return created.Keys.Union(resolved.Keys)
            .OrderBy(d => d)
            .Select(d => new TrendPoint(d, created.GetValueOrDefault(d), resolved.GetValueOrDefault(d)))
            .ToList();
    }

    private TechnicianMetrics Compute(string tech, List<Row> rows, ProductivityWeights weights, IReadOnlyDictionary<Guid, string> connections)
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

        // First response: the promise an SLA plan made, kept or not. Measured only where a promise
        // existed; the average reply time is shown with the number of tickets it comes from, because
        // a reply made straight in the PSA is never seen here.
        var promised = rows.Where(r => r.FirstResponseDueAt is not null).ToList();
        var promiseMet = promised.Count(r => r.FirstRespondedAt is { } a && a <= r.FirstResponseDueAt);
        var replied = rows.Where(r => r.FirstRespondedAt is { } a && a >= r.CreatedAt).ToList();
        // Reopens: resolved work that came back. A ticket that keeps returning was not fixed,
        // whatever its resolution time says.
        var reopened = resolvedRows.Count(r => r.ReopenCount > 0);
        double? reopenRate = resolved > 0 ? Math.Round(100.0 * reopened / resolved, 1) : null;
        // Satisfaction: the client's own rating of the work, 4 or 5 out of 5 counting as satisfied.
        var rated = rows.Where(r => r.Rating is not null).ToList();
        var satisfied = rated.Count(r => r.Rating >= TicketSatisfaction.Satisfied);

        // Each component is measured only where there is something to measure; an unmeasured one is
        // left out of the score (weights renormalise) rather than counted as zero.
        var components = new ProductivityComponents
        {
            SlaCompliance = slaEligible > 0 ? slaPct : null,
            ResolutionRate = assigned > 0 ? Math.Round(100.0 * resolved / assigned, 1) : null,
            CustomerSatisfaction = rated.Count > 0 ? Math.Round(100.0 * satisfied / rated.Count, 1) : null,
            FirstResponse = promised.Count > 0 ? Math.Round(100.0 * promiseMet / promised.Count, 1) : null,
            ReopenScore = reopenRate is { } rr ? Math.Round(100.0 - rr, 1) : null,
            WorklogQuality = resolved > 0 ? Math.Round(100.0 * resolvedRows.Count(r => r.Worked > 0) / resolved, 1) : null,
            DocumentationQuality = resolved > 0 ? Math.Round(100.0 * resolvedRows.Count(r => r.HasNote) / resolved, 1) : null,
        };

        var clientRows = rows.Where(r => r.Origin == TicketOrigin.Psa).ToList();
        var internalRows = rows.Where(r => r.Origin == TicketOrigin.Internal).ToList();
        var monitoringRows = rows.Where(r => r.Origin == TicketOrigin.Rmm).ToList();
        var bySource = rows
            .GroupBy(r => r.Origin switch
            {
                TicketOrigin.Internal => "Team boards",
                TicketOrigin.Rmm => "Monitoring",
                _ => r.Conn is { } c && connections.TryGetValue(c, out var name) ? name : "PSA",
            })
            .Select(g => new SourceWork(g.Key, g.Count(), g.Count(r => r.ResolvedAt is not null), g.Sum(r => r.Worked)))
            .OrderByDescending(x => x.Assigned).ToList();

        return new TechnicianMetrics
        {
            TechnicianExternalId = tech,
            Assigned = assigned,
            Resolved = resolved,
            AssignedClient = clientRows.Count,
            AssignedInternal = internalRows.Count,
            ResolvedClient = clientRows.Count(r => r.ResolvedAt is not null),
            ResolvedInternal = internalRows.Count(r => r.ResolvedAt is not null),
            ClientHours = clientRows.Sum(r => r.Worked),
            InternalHours = internalRows.Sum(r => r.Worked),
            AssignedMonitoring = monitoringRows.Count,
            ResolvedMonitoring = monitoringRows.Count(r => r.ResolvedAt is not null),
            MonitoringHours = monitoringRows.Sum(r => r.Worked),
            BySource = bySource,
            FirstResponseEligible = promised.Count,
            FirstResponseMet = promiseMet,
            AvgFirstResponseHours = replied.Count > 0 ? Math.Round(replied.Average(r => (r.FirstRespondedAt!.Value - r.CreatedAt).TotalHours), 1) : null,
            FirstResponseSample = replied.Count,
            Reopened = reopened,
            ReopenRatePct = reopenRate,
            Rated = rated.Count,
            Satisfied = satisfied,
            Reviewed = rows.Count(r => r.ReviewedAt is not null),
            PassedReviewFirstTime = rows.Count(r => r.ReviewedAt is not null && r.ReviewSendBacks == 0),
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
