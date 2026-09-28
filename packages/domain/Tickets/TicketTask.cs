using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// One step of the work inside a ticket: "image the laptop", "hand it to the user", "update the
/// asset register". A ticket that is really six jobs used to be six tickets, or one note nobody could
/// tick off; this is the list in between.
///
/// Staff only and portal only. A client never sees the team's working list, and nothing here is sent
/// to a PSA — the provider has its own idea of tasks and would not agree with ours.
///
/// As in osTicket, a ticket cannot be closed from the portal while any of its tasks is still open:
/// the list is only worth keeping if "closed" means it was done.
/// </summary>
public class TicketTask : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public required string Title { get; set; }

    public bool IsDone { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public Guid? DoneByUserId { get; set; }

    /// <summary>Who this step is for, when it is not the ticket's own holder.</summary>
    public Guid? AssignedAppUserId { get; set; }

    public int SortOrder { get; set; }
    public Guid? CreatedByUserId { get; set; }
}
