using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// A reply the desk writes often enough to keep: "we have received your request", "please restart
/// and let us know", the steps for a password reset. Inserted into the reply box, where it can still
/// be edited before it is sent — a canned response is a starting point, never a message on its own.
///
/// Placeholders such as {ticket.number} and {requester} are filled from the ticket the reply is being
/// written on, so one response serves every ticket without anyone retyping the details.
/// </summary>
public class CannedResponse : TenantEntity
{
    public required string Name { get; set; }
    public required string Body { get; set; }

    /// <summary>
    /// The board it belongs to, or null for everywhere — including replies on PSA tickets, which is
    /// where the same courtesy text is needed most.
    /// </summary>
    public Guid? BoardId { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public Guid? CreatedByUserId { get; set; }
}
