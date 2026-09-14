using Desk.Domain.Common;

namespace Desk.Domain.Reporting;

/// <summary>What a staff report contains. Client QBRs build on the same schedule and run records.</summary>
public enum StaffReportKind
{
    /// <summary>Hours, billable share, resolutions and SLA per technician over the period.</summary>
    TechnicianProductivity = 0,

    /// <summary>A quarterly business review for one client.</summary>
    ClientQbr = 1,
}

/// <summary>
/// How often a staff report runs. Every run covers the last COMPLETE period — yesterday, last week,
/// last month, last quarter — because a report of a period still in progress changes after it is sent.
/// </summary>
public enum StaffReportFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
}

/// <summary>
/// A report the MSP schedules for its own people (managers, account leads). Separate from the client
/// Control Panel's <c>ReportSchedule</c>: that one belongs to a client company and reports on their
/// account; this one belongs to the MSP and may span every client.
/// </summary>
public class StaffReportSchedule : TenantEntity
{
    public required string Name { get; set; }
    public StaffReportKind Kind { get; set; }
    public StaffReportFrequency Frequency { get; set; }

    /// <summary>Narrows the report to one client. Required for a QBR; optional for productivity.</summary>
    public Guid? ClientCompanyId { get; set; }

    /// <summary>Email recipients, comma or semicolon separated.</summary>
    public string? Recipients { get; set; }

    public bool IsEnabled { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>When the schedule next falls due (UTC). The worker runs schedules whose time has passed.</summary>
    public DateTimeOffset NextRunAt { get; set; }
}

/// <summary>One generated staff report. CSV and PDF are both stored so history downloads exactly what was sent.</summary>
public class StaffReportRun : TenantEntity
{
    /// <summary>Null when generated on demand rather than by a schedule.</summary>
    public Guid? StaffReportScheduleId { get; set; }

    public StaffReportKind Kind { get; set; }
    public Guid? ClientCompanyId { get; set; }

    /// <summary>First and last calendar day covered, in the organization's time zone.</summary>
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
    public required string Title { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Csv { get; set; } = string.Empty;
    public byte[] Pdf { get; set; } = [];

    public bool Delivered { get; set; }
    public string? DeliveryNote { get; set; }
}
