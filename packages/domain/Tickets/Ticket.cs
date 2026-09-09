using Desk.Domain.Common;
using Desk.Domain.Enums;

namespace Desk.Domain.Tickets;

/// <summary>
/// PSA-neutral, normalized ticket. The PSA remains the system of record; this row is the
/// portal's performance/search/reporting projection plus the sync-control metadata that
/// prevents echo loops (correlation id + update hash + version).
/// </summary>
public class Ticket : TenantEntity
{
    // Identity across systems
    public Guid PsaConnectionId { get; set; }
    public ProviderType Provider { get; set; }
    public string? ExternalTicketId { get; set; }
    public Guid ClientCompanyId { get; set; }

    // Requester
    public Guid? RequesterUserId { get; set; }
    public required string RequesterName { get; set; }
    public required string RequesterEmail { get; set; }

    // Content
    public required string Title { get; set; }
    public string? Description { get; set; }

    // Dual status/priority/category: portal-normalized value + raw PSA value
    public string PortalStatus { get; set; } = "NEW";
    public string? PsaStatus { get; set; }
    public string PortalPriority { get; set; } = "NORMAL";
    public string? PsaPriority { get; set; }
    public string? PortalCategory { get; set; }
    public string? PsaCategory { get; set; }
    public string? PortalSubcategory { get; set; }
    public string? PsaSubcategory { get; set; }

    public string? QueueOrBoard { get; set; }
    public string? AssignedTechnicianExternalId { get; set; }

    /// <summary>Display name for the assignee, resolved on sync so reads never call the provider.</summary>
    public string? AssignedTechnicianName { get; set; }

    /// <summary>
    /// The PORTAL user this ticket sits with, which is a different question from
    /// <see cref="AssignedTechnicianExternalId"/> and deliberately not the same field.
    ///
    /// A technician who exists only here has no PSA resource to be assigned to. Their work still
    /// reaches the PSA — under the connection's API identity, because that is the only identity the
    /// PSA has — so the provider's assignee says "the integration" and can never say who actually
    /// did it. Without somewhere local to record that, the answer is lost: every ticket looks
    /// unassigned to the portal, "my tickets" is empty for everyone, and no metric can be attributed
    /// to a person.
    ///
    /// Both may be set at once, and that is the normal case rather than a conflict: the PSA holds
    /// whatever it holds, and this holds who is really working it.
    /// </summary>
    public Guid? AssignedAppUserId { get; set; }

    /// <summary>
    /// When the PSA says the ticket was RAISED — distinct from <see cref="BaseEntity.CreatedAt"/>,
    /// which is when this row was first written and therefore when the portal happened to import it.
    /// Every metric about ticket age or resolution time must use this one: measuring from the import
    /// date silently reports the portal's own rollout as the customer's wait.
    /// Null for a ticket whose provider did not supply a creation date.
    /// </summary>
    public DateTimeOffset? PsaCreatedAt { get; set; }

    // SLA & time
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal TimeWorkedHours { get; set; }
    public decimal BillableHours { get; set; }
    public decimal NonBillableHours { get; set; }

    // Sync control
    public TicketSyncStatus SyncStatus { get; set; } = TicketSyncStatus.PendingCreate;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? SyncError { get; set; }

    /// <summary>Stable id threaded through every event this ticket originates, to detect echoes.</summary>
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    /// <summary>Hash of the last-applied normalized state; unchanged hash ⇒ skip write (idempotency).</summary>
    public string? UpdateHash { get; set; }

    /// <summary>Optimistic-concurrency version.</summary>
    public int Version { get; set; }

    public ICollection<TicketNote> Notes { get; set; } = new List<TicketNote>();
    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
}
