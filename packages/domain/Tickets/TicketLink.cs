using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>How two tickets relate. Read from the FROM ticket's side; the other side reads the inverse.</summary>
public enum TicketLinkKind
{
    /// <summary>About the same thing. Symmetric.</summary>
    Related = 0,
    /// <summary>From is a duplicate of To: the same request raised twice. The work happens on To.</summary>
    Duplicate = 1,
    /// <summary>From is the parent of To: To is one part of the larger job From describes.</summary>
    Parent = 2,
    /// <summary>From blocks To: To cannot finish until From does.</summary>
    Blocks = 3,
}

/// <summary>
/// Two tickets tied together - an investigation and the Autotask ticket it started from, a job and
/// its parts, a request raised twice. A link is information for whoever works either ticket; it
/// never merges, closes or moves anything, and it changes no count.
/// </summary>
public class TicketLink : TenantEntity
{
    public Guid FromTicketId { get; set; }
    public Ticket? FromTicket { get; set; }
    public Guid ToTicketId { get; set; }
    public Ticket? ToTicket { get; set; }
    public TicketLinkKind Kind { get; set; }
    public Guid? CreatedByUserId { get; set; }
}

/// <summary>Where a ticket stands in an opt-in review, on a board or topic that asks for one.</summary>
public enum TicketReviewState
{
    /// <summary>No review asked for, or not resolved yet.</summary>
    None = 0,
    /// <summary>Resolved, waiting for a board lead to approve it before it can be closed.</summary>
    Pending = 1,
    /// <summary>Approved: the work was checked, and the ticket may close.</summary>
    Approved = 2,
}
