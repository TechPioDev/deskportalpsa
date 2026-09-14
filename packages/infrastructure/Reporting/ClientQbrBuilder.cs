using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Domain.Reporting;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Reporting;

/// <summary>Computes a <see cref="ClientQbr"/> for one client. Definitions are documented on the model.</summary>
public sealed class ClientQbrBuilder(DeskDbContext db, TechnicianReportBuilder technicians, TimeProvider clock)
{
    private sealed record T(
        string? Ref, string Title, string Priority, string? Category, string Status,
        DateTimeOffset Raised, DateTimeOffset? Done, DateTimeOffset? SlaDue);

    public async Task<ClientQbr> BuildAsync(
        Guid organizationId, Guid clientCompanyId, StaffReportFrequency frequency, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var org = await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => new { o.Name, o.TimeZone }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Organization");
        var client = await db.ClientCompanies.AsNoTracking()
            .Where(c => c.Id == clientCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Client");
        var zone = StaffReportPeriods.Zone(org.TimeZone);

        var (prevStart, prevEnd) = StaffReportPeriods.Previous(frequency, start);
        var (from, to) = StaffReportPeriods.UtcBounds(start, end, zone);
        var (prevFrom, _) = StaffReportPeriods.UtcBounds(prevStart, prevEnd, zone);

        // Everything that could matter to either period: raised before the end, and not finished
        // before the previous period began. Filtered in the database, then classified in memory
        // because "resolved, else closed" and "raised by the PSA, else by the row" do not translate well.
        var tickets = (await db.Tickets.AsNoTracking()
                .Where(t => t.ClientCompanyId == clientCompanyId)
                .Where(t => (t.PsaCreatedAt ?? t.CreatedAt) <= to)
                .Where(t => (t.ResolvedAt ?? t.ClosedAt) == null || (t.ResolvedAt ?? t.ClosedAt) >= prevFrom)
                .Select(t => new { t.ExternalTicketId, t.Title, t.PortalPriority, t.PortalCategory, t.PortalStatus, Raised = t.PsaCreatedAt ?? t.CreatedAt, t.ResolvedAt, t.ClosedAt, t.SlaDueAt })
                .ToListAsync(ct))
            .Select(t => new T(t.ExternalTicketId, t.Title, t.PortalPriority, t.PortalCategory, t.PortalStatus, t.Raised, t.ResolvedAt ?? t.ClosedAt, t.SlaDueAt))
            .ToList();

        var entries = await db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.EntryDate >= prevFrom && e.EntryDate <= to)
            .Where(e => db.Tickets.Any(t => t.Id == e.TicketId && t.ClientCompanyId == clientCompanyId))
            .Select(e => new { e.EntryDate, e.Hours, e.Billable })
            .ToListAsync(ct);

        QbrFigures Figures(DateTimeOffset f, DateTimeOffset t)
        {
            var raised = tickets.Count(x => x.Raised >= f && x.Raised <= t);
            var done = tickets.Where(x => x.Done is { } d && d >= f && d <= t).ToList();
            var open = tickets.Count(x => x.Raised <= t && (x.Done is null || x.Done > t));
            var sla = done.Where(x => x.SlaDue is not null).ToList();
            // Resolution time needs both ends in order; a ticket closed "before" it was raised is a data
            // fault, left out rather than averaged in as zero.
            var durations = done.Where(x => x.Done > x.Raised).Select(x => (x.Done!.Value - x.Raised).TotalHours).Order().ToList();
            var hours = entries.Where(e => e.EntryDate >= f && e.EntryDate <= t).ToList();
            return new QbrFigures(
                raised, done.Count, open, hours.Sum(e => e.Hours), hours.Where(e => e.Billable).Sum(e => e.Hours),
                sla.Count, sla.Count(x => x.Done <= x.SlaDue),
                durations.Count > 0 ? Math.Round(durations.Average(), 1) : null,
                durations.Count > 0 ? Math.Round(durations[durations.Count / 2], 1) : null);
        }

        var current = Figures(from, to);
        var previous = Figures(prevFrom, from.AddTicks(-1));

        // Month by month, only for periods long enough for months to mean something.
        var months = new List<QbrMonth>();
        if (frequency is StaffReportFrequency.Quarterly)
            for (var m = new DateOnly(start.Year, start.Month, 1); m <= end; m = m.AddMonths(1))
            {
                var (mf, mt) = StaffReportPeriods.UtcBounds(m, m.AddMonths(1).AddDays(-1), zone);
                var fig = Figures(mf, mt);
                months.Add(new QbrMonth(m, fig.Raised, fig.Resolved, fig.Hours));
            }

        var inPeriod = tickets.Where(x => x.Raised >= from && x.Raised <= to).ToList();
        // Most severe first, the order anyone reads a priority list in; unknown values after, by count.
        var byPriority = inPeriod.GroupBy(x => x.Priority.ToUpperInvariant())
            .OrderBy(g => Array.IndexOf(Severity, g.Key) is var i && i >= 0 ? i : Severity.Length)
            .ThenByDescending(g => g.Count())
            .Select(g => new QbrCount(Pretty(g.Key), g.Count())).ToList();
        var byCategory = inPeriod.GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Uncategorised" : x.Category!)
            .Select(g => new QbrCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Label).Take(8).ToList();

        // A period still running is measured up to now, not to a last day that has not happened.
        var now = clock.GetUtcNow();
        var asOf = to < now ? to : now;
        var oldest = tickets.Where(x => x.Raised <= asOf && (x.Done is null || x.Done > asOf))
            .OrderBy(x => x.Raised).Take(5)
            .Select(x => new QbrOpenTicket(x.Ref ?? "—", x.Title, Pretty(x.Priority), Pretty(x.Status), (int)Math.Floor((asOf - x.Raised).TotalDays)))
            .ToList();

        var people = await technicians.BuildAsync(organizationId, from, to, start, end, "", clientCompanyId, ct);

        return new ClientQbr(
            org.Name, client, StaffReportPeriods.Describe(frequency, start, end) + (to > now ? " (so far)" : ""), start, end,
            StaffReportPeriods.Describe(frequency, prevStart, prevEnd),
            current, previous, months, byPriority, byCategory, people.Technicians, oldest,
            clock.GetUtcNow(), org.TimeZone);
    }

    private static readonly string[] Severity = ["CRITICAL", "URGENT", "HIGH", "NORMAL", "MEDIUM", "LOW"];

    private static string Pretty(string value)
        => string.IsNullOrWhiteSpace(value) ? "—"
            : string.Join(' ', value.Replace('_', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
