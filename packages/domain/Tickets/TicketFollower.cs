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
}
