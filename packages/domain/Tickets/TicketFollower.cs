using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// Somebody who wants to know what happens on a ticket without holding it. A ticket has exactly one
/// assignee — that is what makes it clear who is doing the work — but the person who escalated it,
/// the lead who cares about the customer, and the engineer who fixed it last time all need to see it
/// go by. osTicket calls them collaborators; here they are followers, and the difference from an
/// assignee is that a follower is never accountable for the ticket.
///
/// Staff only. A client following a ticket is a different question (they see their own tickets
/// already), and mixing the two in one table would put client identities in a staff list.
/// </summary>
public class TicketFollower : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid AppUserId { get; set; }

    /// <summary>Who put them on it, which is usually somebody else asking them to look.</summary>
    public Guid? AddedByUserId { get; set; }

    /// <summary>
    /// When the question "do they know they were put on it?" was settled: the message was sent, or
    /// none was owed (they followed it themselves). Null while it is still to be looked at.
    /// </summary>
    public DateTimeOffset? AddedNotifiedAt { get; set; }

    /// <summary>
    /// Everything on the ticket up to this moment has been reported to them. Null means "from when
    /// they started following": what happened before that is not news to a new follower.
    /// </summary>
    public DateTimeOffset? NotifiedThrough { get; set; }
}
