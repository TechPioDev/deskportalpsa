namespace Desk.Domain.Tickets;

/// <summary>
/// The portal's own words for a ticket's status and priority: what a PSA's values are mapped TO.
/// Listed once on the server, here, and read by the mapping health check. The mapping screen in
/// the browser still carries its own copy of the same two lists; it reads this one when that
/// screen is reworked.
/// </summary>
public static class PortalVocabulary
{
    public static readonly IReadOnlyList<string> Statuses = ["NEW", "IN_PROGRESS", "WAITING_CUSTOMER", "ON_HOLD", "RESOLVED", "CLOSED"];
    public static readonly IReadOnlyList<string> Priorities = ["CRITICAL", "HIGH", "NORMAL", "LOW"];
}
