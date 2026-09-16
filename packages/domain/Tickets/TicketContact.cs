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
    public static bool IsReachable(Ticket t) => Email(t) is not null || t.RequesterUserId is not null;
}
