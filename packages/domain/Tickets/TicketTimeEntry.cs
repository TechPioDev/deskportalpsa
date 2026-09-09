using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>Which system a time entry originated in.</summary>
public enum TimeEntrySource
{
    /// <summary>Logged here and pushed out to the PSA.</summary>
    Portal = 1,

    /// <summary>Written directly in the PSA by a technician, and read back from it.</summary>
    Provider = 2,
}

/// <summary>How a portal-logged entry fared on its way to the PSA.</summary>
public enum TimeEntrySyncStatus
{
    Pending = 0,
    Synced = 1,
    Failed = 2,
}

/// <summary>
/// Portal-side record of a time entry logged from here. The PSA remains the system of record for
/// hours — this table exists so the time panel can answer two questions the PSA cannot: which system
/// the entry came from, and whether an entry the customer logged here actually reached the PSA.
///
/// Without it a rejected push vanished entirely: the request 400'd and nothing was kept, so the
/// technician's work was silently lost with no trace to retry from.
/// </summary>
public class TicketTimeEntry : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>The PSA's own entry id. Null while pending, and after a failed push.</summary>
    public string? ExternalEntryId { get; set; }

    /// <summary>
    /// The conversation note this entry was logged WITH, when time came in on a reply. The PSA
    /// keeps the two as unrelated records, so without this link the thread cannot say how long
    /// the work in a reply took — the one question a time-and-materials reader always has.
    /// </summary>
    public Guid? NoteId { get; set; }

    public decimal Hours { get; set; }
    public bool Billable { get; set; }
    public string? Notes { get; set; }

    /// <summary>Provider work-type id and its label at the time of logging, so history stays readable.</summary>
    public string? WorkTypeId { get; set; }
    public string? WorkTypeLabel { get; set; }
    public string? WorkRoleId { get; set; }

    public string? TechnicianExternalId { get; set; }
    public string? TechnicianName { get; set; }

    /// <summary>
    /// The PORTAL user who logged this hour, stamped at creation.
    ///
    /// <see cref="TechnicianExternalId"/> answers "whose timesheet does the PSA put this on", and
    /// for a technician who exists only here the answer is the connection's default resource — the
    /// integration account. That is correct for the provider and useless for us: every hour the
    /// team logs would carry the same identity, and "hours worked per technician" would be one row
    /// with everybody's time in it.
    ///
    /// Null for entries imported FROM the PSA, which were logged by someone who may have no portal
    /// account at all. Null is therefore "not ours to attribute", never "unknown person".
    /// </summary>
    public Guid? AppUserId { get; set; }

    public TimeEntrySource Source { get; set; } = TimeEntrySource.Portal;
    public TimeEntrySyncStatus SyncStatus { get; set; } = TimeEntrySyncStatus.Pending;

    /// <summary>The provider's rejection, kept verbatim so it can be acted on rather than guessed at.</summary>
    public string? SyncError { get; set; }

    public DateTimeOffset EntryDate { get; set; }
}
