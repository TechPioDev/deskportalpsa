using System.Globalization;
using System.Text;
using Desk.Application.ControlPanel;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.ControlPanel;

/// <summary>
/// Builds the account report and renders it to CSV. Shared by the client-facing service (export /
/// run-now) and the worker's scheduled runner. Queries filter by company id explicitly, so it works
/// under either a client tenant scope or the worker's platform scope.
/// </summary>
internal static class ReportComposer
{
    private static readonly string[] OpenStatuses = ["NEW", "IN_PROGRESS", "WAITING_CUSTOMER", "ON_HOLD"];

    public static async Task<AccountReportDto> BuildAsync(DeskDbContext db, Guid companyId, CancellationToken ct)
    {
        // PSA tickets only: an internal board's work is the team's record, not the client's.
        var tickets = db.Tickets.AsNoTracking()
            .Where(t => t.ClientCompanyId == companyId && t.Origin == TicketOrigin.Psa);

        var byStatus = await tickets
            .GroupBy(t => t.PortalStatus)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var total = byStatus.Sum(s => s.Count);
        var open = byStatus.Where(s => OpenStatuses.Contains(s.Status.ToUpper())).Sum(s => s.Count);
        var hours = await tickets.SumAsync(t => (decimal?)t.TimeWorkedHours, ct) ?? 0m;
        var billable = await tickets.SumAsync(t => (decimal?)t.BillableHours, ct) ?? 0m;

        var recent = await tickets
            .OrderByDescending(t => t.CreatedAt).Take(25)
            .Select(t => new ReportTicket(t.Id, t.ExternalTicketId, t.Title, t.PortalStatus, t.CreatedAt))
            .ToListAsync(ct);

        return new AccountReportDto(total, open,
            byStatus.OrderByDescending(s => s.Count).ToList(),
            Math.Round(hours, 2), Math.Round(billable, 2), recent);
    }

    public static string Summary(AccountReportDto r)
        => $"{r.TotalTickets} tickets · {r.OpenTickets} open · {r.HoursLogged:0.00}h";

    public static string ToCsv(AccountReportDto r, string companyName, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        void Line(params string[] cells) => sb.Append(string.Join(',', cells.Select(Csv))).Append("\r\n");

        Line("PioManage — Account Report");
        Line("Account", companyName);
        Line("Generated", generatedAt.ToString("u", CultureInfo.InvariantCulture));
        Line("");
        Line("Summary");
        Line("Total tickets", r.TotalTickets.ToString(CultureInfo.InvariantCulture));
        Line("Open tickets", r.OpenTickets.ToString(CultureInfo.InvariantCulture));
        Line("Hours logged", r.HoursLogged.ToString("0.00", CultureInfo.InvariantCulture));
        Line("Billable hours", r.BillableHours.ToString("0.00", CultureInfo.InvariantCulture));
        Line("");
        Line("Tickets by status");
        Line("Status", "Count");
        foreach (var s in r.ByStatus) Line(s.Status, s.Count.ToString(CultureInfo.InvariantCulture));
        Line("");
        Line("Recent tickets");
        Line("Reference", "Title", "Status", "Created");
        foreach (var t in r.Recent)
            Line(t.ExternalTicketId ?? "", t.Title, t.PortalStatus, t.CreatedAt.ToString("u", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    public static DateTimeOffset Advance(DateTimeOffset from, ReportFrequency f) => f switch
    {
        ReportFrequency.Daily => from.AddDays(1),
        ReportFrequency.Monthly => from.AddMonths(1),
        _ => from.AddDays(7),
    };

    // Minimal RFC-4180 quoting: wrap in quotes when the field contains a comma, quote or newline.
    private static string Csv(string? field)
    {
        var s = field ?? "";
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
