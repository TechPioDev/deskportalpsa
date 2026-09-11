using Desk.Application.Analytics;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Analytics;

/// <summary>
/// Where the desk's capacity goes, by client.
///
/// Every figure comes from what the PSA reported and the portal stored — no estimate, no
/// apportionment, no filling of gaps. Where a figure cannot be computed for a ticket, that ticket
/// is excluded from that figure and counted in the report's coverage fields instead, so the surface
/// can say what it measured rather than quietly averaging over whatever happened to be present.
/// </summary>
public sealed class ClientWorkloadService(DeskDbContext db) : IClientWorkloadService
{
    /// <summary>One person's share of one client's work, accumulated from both sources.</summary>
    private sealed class Tally(Guid? appUserId, string? externalId)
    {
        public Guid? AppUserId { get; } = appUserId;
        public string? ExternalId { get; } = externalId;
        public int Assigned { get; set; }
        public decimal Hours { get; set; }
    }

    public async Task<ClientWorkloadReport> ForClientsAsync(MetricsFilter filter, CancellationToken ct = default)
    {
        var q = db.Tickets.AsNoTracking().AsQueryable();
        // Ticket age and every date filter run off the PSA's raise date, falling back to the row
        // timestamp only where the provider gave none — the row timestamp is when the portal
        // imported the ticket, which is a fact about the rollout, not about the work.
        if (filter.From is { } from) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) >= from);
        if (filter.To is { } to) q = q.Where(t => (t.PsaCreatedAt ?? t.CreatedAt) <= to);
        if (filter.ClientCompanyId is { } company) q = q.Where(t => t.ClientCompanyId == company);
        if (filter.PsaConnectionId is { } conn) q = q.Where(t => t.PsaConnectionId == conn);

        var rows = await q
            .Select(t => new
            {
                t.ClientCompanyId,
                t.PsaCreatedAt,
                RowCreatedAt = t.CreatedAt,
                t.ClosedAt,
                t.ResolvedAt,
                t.SlaDueAt,
                t.AssignedTechnicianExternalId,
                t.AssignedTechnicianName,
                t.AssignedAppUserId,
                t.TimeWorkedHours,
                t.BillableHours,
            })
            .ToListAsync(ct);

        var names = await db.ClientCompanies.AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // Who actually worked the ticket, from the TIME ENTRIES as well as the assignee: hours belong
        // to whoever logged them, and a ticket is commonly worked by someone other than the person it
        // is assigned to — or by several people.
        //
        // Joined to the SAME windowed tickets as every other figure in the row. This used to join all
        // of a client's tickets, so on "Last 30 days" a client with one ticket in range still counted
        // everyone who had ever logged time for it - a People figure from a different period than the
        // Tickets figure beside it.
        var entries = await db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.AppUserId != null || (e.TechnicianExternalId != null && e.TechnicianExternalId != ""))
            .Join(q, e => e.TicketId, t => t.Id,
                (e, t) => new { t.ClientCompanyId, e.AppUserId, e.TechnicianExternalId, e.TechnicianName, e.Hours })
            .ToListAsync(ct);
        var entriesByClient = entries.ToLookup(e => e.ClientCompanyId);

        var userNames = await UserNamesAsync(
            rows.Where(r => r.AssignedAppUserId is not null).Select(r => r.AssignedAppUserId!.Value)
                .Concat(entries.Where(e => e.AppUserId is not null).Select(e => e.AppUserId!.Value))
                .Distinct().ToList(), ct);

        // The provider's display names, as the sync cached them on tickets and entries. The id only
        // stands in where neither carried one.
        var psaNames = rows
            .Where(r => !string.IsNullOrEmpty(r.AssignedTechnicianExternalId) && r.AssignedTechnicianName is not null)
            .Select(r => (Id: r.AssignedTechnicianExternalId!, Name: r.AssignedTechnicianName!))
            .Concat(entries
                .Where(e => !string.IsNullOrEmpty(e.TechnicianExternalId) && e.TechnicianName is not null)
                .Select(e => (Id: e.TechnicianExternalId!, Name: e.TechnicianName!)))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        var clients = rows
            .GroupBy(t => t.ClientCompanyId)
            .Select(g =>
            {
                // Resolution time needs BOTH ends. A ticket missing either is excluded and
                // reported, never treated as instant.
                var resolved = g
                    .Where(t => t.ClosedAt is not null && (t.PsaCreatedAt ?? t.RowCreatedAt) is var raised && t.ClosedAt > raised)
                    .Select(t => (t.ClosedAt!.Value - (t.PsaCreatedAt ?? t.RowCreatedAt)).TotalHours)
                    .ToList();

                var slaEligible = g.Where(t => t.SlaDueAt is not null && t.ClosedAt is not null).ToList();
                var withinSla = slaEligible.Count(t => t.ClosedAt <= t.SlaDueAt);

                // Assignees and time-loggers together: either alone under-counts who touched it.
                // One identity per person, preferring the PORTAL user - the rule the technician pages
                // use. A portal-only technician's work reaches the PSA under the integration account,
                // so keyed on the provider's id every one of them collapsed into "the API user" and the
                // people actually doing the work were never counted at all.
                var people = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
                Tally For(Guid? appUserId, string? externalId)
                {
                    var key = appUserId is { } u ? "u:" + u : "x:" + externalId;
                    if (!people.TryGetValue(key, out var tally))
                        people[key] = tally = new Tally(appUserId, appUserId is null ? externalId : null);
                    return tally;
                }

                foreach (var t in g)
                {
                    if (t.AssignedAppUserId is { } uid) For(uid, null).Assigned++;
                    else if (!string.IsNullOrEmpty(t.AssignedTechnicianExternalId)) For(null, t.AssignedTechnicianExternalId).Assigned++;
                }
                foreach (var e in entriesByClient[g.Key])
                    For(e.AppUserId, e.TechnicianExternalId).Hours += e.Hours;

                // The count IS the length of this list, so the figure and the list it opens cannot
                // disagree - they are one computation, not two that happen to match today.
                var involved = people.Values
                    .Select(p => new ClientWorkloadPerson(
                        p.AppUserId,
                        p.ExternalId,
                        p.AppUserId is { } u
                            ? userNames.GetValueOrDefault(u, "Unknown user")
                            : psaNames.GetValueOrDefault(p.ExternalId!, p.ExternalId!),
                        p.Assigned,
                        p.Hours))
                    .OrderByDescending(p => p.HoursLogged)
                    .ThenByDescending(p => p.AssignedTickets)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ClientWorkloadRow(
                    g.Key,
                    names.GetValueOrDefault(g.Key, "Unknown client"),
                    g.Count(),
                    g.Count(t => t.ClosedAt is null),
                    g.Count(t => t.ClosedAt is not null),
                    g.Sum(t => t.TimeWorkedHours),
                    g.Sum(t => t.BillableHours),
                    involved.Count,
                    resolved.Count > 0 ? Math.Round(resolved.Average(), 1) : null,
                    resolved.Count,
                    slaEligible.Count > 0 ? Math.Round(withinSla * 100.0 / slaEligible.Count, 1) : null,
                    slaEligible.Count,
                    involved);
            })
            // Hours first: the question this answers is where capacity goes, and hours are capacity.
            .OrderByDescending(c => c.HoursWorked)
            .ThenByDescending(c => c.TotalTickets)
            .ToList();

        var windows = await db.PsaConnections.AsNoTracking()
            .Select(c => new ImportWindowNote(
                c.Name, c.ImportClosedTickets, c.FilterActiveWithinDays,
                db.Tickets.Count(t => t.PsaConnectionId == c.Id)))
            .ToListAsync(ct);

        return new ClientWorkloadReport(
            clients,
            rows.Count(t => t.PsaCreatedAt is null),
            rows.Count(t => t.ClosedAt is null),
            windows);
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        return await db.AppUsers.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}
