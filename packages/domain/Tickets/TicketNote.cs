using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// A ticket note. Only PUBLIC notes are ever mirrored into the portal and shown to clients;
/// internal PSA notes must never reach this table (spec §7/§9: internal notes stay hidden).
/// </summary>
public class TicketNote : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public string? ExternalNoteId { get; set; }
    public required string AuthorName { get; set; }

    /// <summary>
    /// The provider's id for whoever wrote it (Autotask resource, ConnectWise member); null for a
    /// portal reply, a contact's note or a system note. <see cref="AuthorName"/> is only what that
    /// person is CALLED - and the account the portal writes as is called after a real person, so
    /// only the id can tell its notes from theirs.
    /// </summary>
    public string? AuthorExternalId { get; set; }
    public bool AuthoredByClient { get; set; }
    public required string Body { get; set; }

    /// <summary>Invariant: always true for persisted notes. Private notes are filtered before persistence.</summary>
    public bool IsPublic { get; set; } = true;

    public DateTimeOffset NoteCreatedAt { get; set; }

    /// <summary>Set when this note originated from the portal, so the inbound sync can skip its own echo.</summary>
    public Guid? OriginCorrelationId { get; set; }

    /// <summary>
    /// True when the inbound sync wrote this row from the provider's thread. This — not the author's
    /// side — is what deletion reconciliation keys on: a client CONTACT can author a note in the PSA
    /// (imported, reconcilable), and the portal's own replies carry a provider id after the push
    /// (portal-origin, never reconciled away). AuthoredByClient answers "who wrote it";
    /// this answers "whose copy is authoritative".
    /// </summary>
    public bool ImportedFromProvider { get; set; }

    /// <summary>
    /// Where sending this note to the PSA stands, for a note written while the PSA could not be
    /// reached. Null for every other note: one the PSA took at once, one read from the PSA, one on
    /// a ticket with no PSA. Pending and failed notes have no id from the PSA yet. A reply of the
    /// desk's that is not synced is not shown to the client: the PSA has not sent it to them.
    /// </summary>
    public Desk.Domain.Sync.OutboundState? SyncState { get; set; }
}
