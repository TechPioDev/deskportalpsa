using Desk.Domain.Reporting;

namespace Desk.Application.Reporting;

public sealed record TechnicianReportRow(
    string Name, decimal Hours, decimal BillableHours, int Resolved, int TicketsTouched, int ActiveDays)
{
    public decimal? HoursPerTicket => TicketsTouched > 0 ? Math.Round(Hours / TicketsTouched, 2) : null;
}

public sealed record TechnicianReportDay(DateOnly Date, decimal Hours, int Resolved);

/// <summary>
/// Everything a technician productivity report says, independent of format, so the CSV, the PDF
/// and the email body cannot drift apart: each renders this, none recomputes it.
/// </summary>
public sealed record TechnicianReport(
    string OrganizationName,
    string PeriodLabel,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? ClientName,
    IReadOnlyList<TechnicianReportRow> Technicians,
    IReadOnlyList<TechnicianReportDay> Days,
    DateTimeOffset GeneratedAt,
    string TimeZone)
{
    public string Title => ClientName is null
        ? $"Technician productivity — {PeriodLabel}"
        : $"Technician productivity — {ClientName} — {PeriodLabel}";

    public decimal TotalHours => Technicians.Sum(t => t.Hours);
    public decimal TotalBillable => Technicians.Sum(t => t.BillableHours);
    public int TotalResolved => Technicians.Sum(t => t.Resolved);
    public int TotalTouched => Technicians.Sum(t => t.TicketsTouched);
    public int BillablePct => TotalHours > 0 ? (int)Math.Round(TotalBillable / TotalHours * 100) : 0;

    public string Summary => $"{TotalHours:0.##}h logged · {TotalResolved} resolved · {Technicians.Count} people";
}

public sealed record StaffReportScheduleDto(
    Guid Id, string Name, StaffReportKind Kind, StaffReportFrequency Frequency, Guid? ClientCompanyId, string? ClientName,
    string? Recipients, bool IsEnabled, DateTimeOffset? LastRunAt, DateTimeOffset NextRunAt);

public sealed record StaffReportScheduleInput(
    Guid? Id, string Name, StaffReportKind Kind, StaffReportFrequency Frequency, Guid? ClientCompanyId, string? Recipients, bool IsEnabled);

public sealed record StaffReportRunDto(
    Guid Id, Guid? ScheduleId, StaffReportKind Kind, string Title, string Summary, DateOnly PeriodStart, DateOnly PeriodEnd,
    DateTimeOffset GeneratedAt, bool Delivered, string? DeliveryNote);

public interface IStaffReportService
{
    /// <summary>The organization's time zone: it decides what "yesterday" means and when reports are sent.</summary>
    Task<string> TimeZoneAsync(CancellationToken ct = default);

    /// <summary>Changes it and moves every schedule's next send to 07:00 in the new zone.</summary>
    Task<string> SetTimeZoneAsync(string timeZoneId, CancellationToken ct = default);

    /// <summary>Clients a report can be narrowed to, by name.</summary>
    Task<IReadOnlyList<(Guid Id, string Name)>> ClientsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StaffReportScheduleDto>> SchedulesAsync(CancellationToken ct = default);
    Task<StaffReportScheduleDto> SaveScheduleAsync(StaffReportScheduleInput input, CancellationToken ct = default);
    Task DeleteScheduleAsync(Guid id, CancellationToken ct = default);

    /// <summary>Generates the schedule's last complete period now and delivers it, without moving its next run.</summary>
    Task<StaffReportRunDto> RunNowAsync(Guid scheduleId, CancellationToken ct = default);

    Task<IReadOnlyList<StaffReportRunDto>> RunsAsync(int take, CancellationToken ct = default);
    Task<(string FileName, string ContentType, byte[] Content)> RunFileAsync(Guid runId, string format, CancellationToken ct = default);

    /// <summary>A business review of one client for a past quarter (year + 1..4), on demand.</summary>
    Task<(string FileName, byte[] Content)> ClientQbrPdfAsync(Guid clientCompanyId, int year, int quarter, CancellationToken ct = default);

    /// <summary>A PDF of an arbitrary range, for the Technician hours page's download button.</summary>
    Task<(string FileName, byte[] Content)> TechnicianPdfAsync(DateTimeOffset from, DateTimeOffset to, Guid? clientCompanyId, string label, CancellationToken ct = default);
}

/// <summary>Runs every due staff schedule across all organizations. Called by the worker.</summary>
public interface IStaffReportRunner
{
    Task<int> RunDueAsync(CancellationToken ct = default);
}
