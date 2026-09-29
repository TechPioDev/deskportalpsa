using Desk.Domain.Common;
using Desk.Domain.Tenancy;

namespace Desk.Domain.ControlPanel;

/// <summary>
/// Per-account configuration a client maintains in the control panel (CP-2). Every entity is scoped
/// to a single <see cref="ClientCompany"/> (the account) and tenant-isolated like everything else.
/// These describe how the MSP should handle the account; the PSA remains the system of record for
/// tickets, so nothing here is written back to the PSA — it is guidance the technicians read.
/// </summary>

/// <summary>Per-account config that belongs to exactly one client company (enables generic scoping).</summary>
public interface IAccountScoped
{
    Guid ClientCompanyId { get; set; }
}

/// <summary>A person the client authorizes to approve requests (e.g. new users, purchases).</summary>
public class Approver : TenantEntity, IAccountScoped
{
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>What this person is allowed to approve (free text, e.g. "New users, hardware under $500").</summary>
    public string? Scope { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>One tier in the account's escalation path (who to contact, and when).</summary>
public class EscalationLevel : TenantEntity, IAccountScoped
{
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    /// <summary>1-based tier order.</summary>
    public int Level { get; set; }

    public required string Name { get; set; }
    public string? Contact { get; set; }

    /// <summary>The trigger for escalating to this tier (e.g. "No response in 30 min" / "P1 outage").</summary>
    public string? Condition { get; set; }
}

/// <summary>A day the account is closed / on reduced coverage.</summary>
public class Holiday : TenantEntity, IAccountScoped
{
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    /// <summary>ISO date (yyyy-MM-dd). Stored as text for provider/DB portability.</summary>
    public required string Date { get; set; }
    public required string Name { get; set; }
}

/// <summary>A device / asset the client records for the account (client-maintained; not a PSA sync).</summary>
public class Device : TenantEntity, IAccountScoped
{
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    public required string Name { get; set; }
    public string? Type { get; set; }

    /// <summary>Serial or asset tag.</summary>
    public string? Identifier { get; set; }
    public string? Notes { get; set; }

    // ---- From the PSA ----
    // A device the daily sync brought in carries the PSA's own id, and its name, type, serial and
    // warranty are the PSA's to change: edited here, they would be overwritten on the next sync.
    // A device added by hand has none of these, and stays entirely the client's.

    /// <summary>The connection it came from; null for a device added by hand.</summary>
    public Guid? PsaConnectionId { get; set; }

    /// <summary>Autotask configuration item or ConnectWise configuration id.</summary>
    public string? ExternalId { get; set; }

    /// <summary>False once the PSA marks it inactive or stops listing it. Kept rather than deleted,
    /// because its tickets still point at it.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? WarrantyExpiresAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    public bool FromPsa => ExternalId is not null;
}

/// <summary>
/// The account's business hours — one row per account. The weekly schedule is stored as JSON
/// (seven days, each open/closed with start/end) so the shape can evolve without a migration.
/// </summary>
public class BusinessHours : TenantEntity, IAccountScoped
{
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    /// <summary>IANA/Windows time-zone label the client operates in (free text).</summary>
    public string? TimeZone { get; set; }

    /// <summary>JSON array of 7 day entries: [{ "day": "Mon", "open": true, "start": "09:00", "end": "17:00" }, …].</summary>
    public string ScheduleJson { get; set; } = "[]";

    public string? Notes { get; set; }
}
