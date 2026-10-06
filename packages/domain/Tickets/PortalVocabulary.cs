namespace Desk.Domain.Tickets;

/// <summary>
/// The portal's own words for a ticket's status and priority: what a PSA's values are mapped TO.
/// Listed once, here. The mapping screen and the mapping health check both read it, so a status the
/// portal can be set to and a status the health check knows about cannot be two different lists.
/// </summary>
public static class PortalVocabulary
{
    public static readonly IReadOnlyList<string> Statuses = ["NEW", "IN_PROGRESS", "WAITING_CUSTOMER", "ON_HOLD", "RESOLVED", "CLOSED"];
    public static readonly IReadOnlyList<string> Priorities = ["CRITICAL", "HIGH", "NORMAL", "LOW"];
}
