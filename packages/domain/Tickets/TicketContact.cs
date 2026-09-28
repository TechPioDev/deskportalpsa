namespace Desk.Domain.Tickets;

/// <summary>
/// The customer contact a ticket is for, as far as anyone can reach them. Tickets imported before
/// contacts were captured, or whose PSA names nobody, hold placeholders ("Unknown" /
/// "unknown@unknown"); those mean no contact, not a contact called Unknown.
/// </summary>
public static class TicketContact
{
    public const string PlaceholderName = "Unknown";
    public const string PlaceholderEmail = "unknown@unknown";

    public static string? Email(Ticket t)
        => string.IsNullOrWhiteSpace(t.RequesterEmail) || t.RequesterEmail == PlaceholderEmail || !t.RequesterEmail.Contains('@')
            ? null : t.RequesterEmail;

    public static string? Name(Ticket t)
        => string.IsNullOrWhiteSpace(t.RequesterName) || t.RequesterName == PlaceholderName ? null : t.RequesterName;

    /// <summary>
    /// Whether a public reply reaches anyone: the ticket has a contact with an address in the PSA, or
    /// a client portal user raised it and reads the thread here.
    /// </summary>
    /// <remarks>
    /// Never on a ticket that belongs to no PSA. Such a ticket's requester is the member of staff
    /// who raised it, whose address is perfectly valid and is exactly the wrong person to "reply" to
    /// — there is no client thread on the team's own board, and offering one would invite a
    /// technician to write to a customer who was never part of the conversation.
    /// </remarks>
    public static bool IsReachable(Ticket t) =>
        t.Origin == Desk.Domain.Enums.TicketOrigin.Psa
        && (Email(t) is not null || t.RequesterUserId is not null);
}
