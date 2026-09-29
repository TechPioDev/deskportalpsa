using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// How the client felt about a ticket once it was done: one rating, 1 to 5, and an optional comment.
/// One row per ticket — a client who changes their mind replaces their answer rather than adding a
/// second vote — and changeable only for a while after the ticket was finished, so a rating says
/// something about the work and not about something that happened a quarter later.
///
/// Who held the ticket is copied onto the rating when it is given. Reassigning the ticket afterwards
/// must not move the credit, or the blame, to someone who never worked it.
///
/// CSAT, as the reports use it, is the share of ratings that are 4 or 5.
/// </summary>
public class TicketSatisfaction : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>The client company, copied so per-client figures do not need the ticket.</summary>
    public Guid? ClientCompanyId { get; set; }

    /// <summary>The client portal user who answered.</summary>
    public Guid ClientUserId { get; set; }

    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset RatedAt { get; set; }

    // Who held the ticket when it was rated: a portal user, or a PSA resource, or nobody.
    public Guid? TechnicianAppUserId { get; set; }
    public string? TechnicianExternalId { get; set; }
    public string? TechnicianName { get; set; }

    public const int Satisfied = 4;
}
