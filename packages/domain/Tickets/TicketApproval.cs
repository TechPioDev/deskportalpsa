using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

public enum ApprovalState
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>The technician withdrew the question - the work changed, or it was asked of the wrong person.</summary>
    Cancelled = 3,
}

/// <summary>How the answer reached the desk. Only <see cref="Portal"/> is the approver's own click;
/// every other value is a technician writing down what they were told, and says so.</summary>
public enum ApprovalChannel
{
    Portal = 0,
    Phone = 1,
    Email = 2,
    InPerson = 3,
}

/// <summary>
/// A technician asking one of the client's named approvers to agree to something before the work
/// goes ahead - a licence, a purchase, an out-of-hours reboot.
///
/// The approver is chosen from the client's own list (Control Panel → Approvers), and their name and
/// email are copied onto the request: the client can edit or remove an approver afterwards, and the
/// record of who was asked must not change with it.
///
/// The approver answers in the portal when their sign-in email matches, or the technician records an
/// answer given by phone or in person. <see cref="Channel"/> keeps the two apart, because "approved in
/// the portal by Rahul" and "Anika says Rahul approved on the phone" are different evidence.
/// </summary>
public class TicketApproval : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public Guid ClientCompanyId { get; set; }

    /// <summary>The approver as listed at the time; null once the client removes them from the list.</summary>
    public Guid? ApproverId { get; set; }
    public required string ApproverName { get; set; }
    public string? ApproverEmail { get; set; }

    /// <summary>What is being approved, in the technician's words: "Adobe Acrobat licence for Priya, ₹18,000".</summary>
    public required string Request { get; set; }

    public Guid RequestedByAppUserId { get; set; }
    public required string RequestedByName { get; set; }
    public DateTimeOffset RequestedAt { get; set; }

    public ApprovalState State { get; set; } = ApprovalState.Pending;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }
    public ApprovalChannel? Channel { get; set; }

    /// <summary>The client portal user who clicked, when the answer came through the portal.</summary>
    public Guid? DecidedByClientUserId { get; set; }

    /// <summary>The technician who wrote the answer down, or withdrew the request.</summary>
    public Guid? RecordedByAppUserId { get; set; }
    public string? RecordedByName { get; set; }

    public const int MaxRequestLength = 1000;
    public const int MaxCommentLength = 1000;
}
