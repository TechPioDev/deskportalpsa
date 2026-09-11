using Desk.Application.Analytics;
using Desk.Domain.Analytics;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Analytics;

/// <summary>
/// How much of the work the PSA recorded is visible in this portal.
///
/// The PSA's time entries are the denominator because they are the billing truth — the one record
/// of work everyone already trusts. For each entry the question is whether ANY portal activity
/// touched that ticket that day. That is a deliberately modest claim: it says the work is visible
/// here, not that the same person did it in both systems, which the data cannot establish.
///
/// What this service refuses to produce is a "PSA hours minus portal hours" figure. Portal active
/// time is not observable — deriving it needs an invented idle timeout, and a technician reading a
/// long thread is indistinguishable from one who walked away — so any such difference would be an
/// assumption presented as a measurement, and read as time wasted.
/// </summary>
public sealed class PortalCoverageService(DeskDbContext db) : IPortalCoverageService
{
    public async Task<PortalCoverageReport> CoverageAsync(MetricsFilter filter, CancellationToken ct = default)
    {
        // When the log began. Everything downstream depends on this: a range that starts earlier is
        // not evidence of low coverage, it is an absence of evidence, and the two must never be
        // shown the same way.
        var recordedSince = await db.ActivityEvents.AsNoTracking()
            .OrderBy(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(ct);

        var entriesQuery = db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.SyncStatus == Domain.Tickets.TimeEntrySyncStatus.Synced);
        if (filter.From is { } from) entriesQuery = entriesQuery.Where(e => e.EntryDate >= from);
        if (filter.To is { } to) entriesQuery = entriesQuery.Where(e => e.EntryDate <= to);
        if (filter.TechnicianExternalId is { } tech)
            entriesQuery = entriesQuery.Where(e => e.TechnicianExternalId == tech);

        var entries = await entriesQuery
            .Select(e => new { e.TicketId, e.EntryDate, e.Hours, e.TechnicianExternalId, Conn = e.Ticket!.PsaConnectionId })
            .ToListAsync(ct);

        // Portal-side activity in the same window, reduced to the pair the corroboration turns on.
        var portalQuery = db.ActivityEvents.AsNoTracking()
            .Where(e => e.Source == ActivitySource.Portal && e.TicketId != null);
        if (filter.From is { } pf) portalQuery = portalQuery.Where(e => e.OccurredAt >= pf);
        if (filter.To is { } pt) portalQuery = portalQuery.Where(e => e.OccurredAt <= pt);

        var portal = await portalQuery
            .Select(e => new { TicketId = e.TicketId!.Value, e.OccurredAt, e.ActorUserId })
            .ToListAsync(ct);

        var touched = portal
            .Select(e => (e.TicketId, Day: e.OccurredAt.UtcDateTime.Date))
            .ToHashSet();

        // Portal events per technician, via the identity mapping. Unmapped people cannot be
        // attributed, and are counted as zero rather than guessed at.
        var identities = await db.UserPsaIdentities.AsNoTracking()
            .Select(i => new { i.AppUserId, i.ExternalTechnicianId })
            .ToListAsync(ct);
        var externalIdByUser = identities
            .GroupBy(i => i.AppUserId)
            .ToDictionary(g => g.Key, g => g.First().ExternalTechnicianId);

        var portalEventsByTech = portal
            .Where(e => e.ActorUserId is not null && externalIdByUser.ContainsKey(e.ActorUserId.Value))
            .GroupBy(e => externalIdByUser[e.ActorUserId!.Value])
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var names = await db.UserPsaIdentities.AsNoTracking()
            .Where(i => i.ExternalTechnicianName != null)
            .Select(i => new { i.ExternalTechnicianId, i.ExternalTechnicianName })
            .ToListAsync(ct);
        var nameById = names
            .GroupBy(n => n.ExternalTechnicianId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ExternalTechnicianName, StringComparer.OrdinalIgnoreCase);

        // No row for the account the portal writes as: it is the whole portal team's time under one
        // login, not a technician whose coverage means anything. Its entries still count in the
        // desk-wide totals below, which are about the work, not about who did it.
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var rows = entries
            .Where(e => !string.IsNullOrEmpty(e.TechnicianExternalId) && !account.IsAccount(e.Conn, e.TechnicianExternalId))
            .GroupBy(e => e.TechnicianExternalId!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var corroborated = g.Count(e => touched.Contains((e.TicketId, e.EntryDate.UtcDateTime.Date)));
                return new PortalCoverageRow(
                    g.Key,
                    nameById.GetValueOrDefault(g.Key),
                    g.Sum(e => e.Hours),
                    g.Count(),
                    corroborated,
                    // Null, not zero, when there is nothing to measure. Zero reads as a finding.
                    g.Any() ? Math.Round(corroborated * 100.0 / g.Count(), 1) : null,
                    portalEventsByTech.GetValueOrDefault(g.Key, 0));
            })
            .OrderByDescending(r => r.PsaHours)
            .ToList();

        var totalEntries = entries.Count;
        var totalCorroborated = entries.Count(e => touched.Contains((e.TicketId, e.EntryDate.UtcDateTime.Date)));

        return new PortalCoverageReport(
            rows,
            entries.Sum(e => e.Hours),
            totalEntries,
            totalCorroborated,
            totalEntries > 0 ? Math.Round(totalCorroborated * 100.0 / totalEntries, 1) : null,
            recordedSince,
            // The flag the surface needs to avoid presenting an absence of evidence as a low score.
            recordedSince is null || (filter.From is { } f && f < recordedSince));
    }
}
