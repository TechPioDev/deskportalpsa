namespace Desk.Application.Tickets;

/// <summary>One line of a ticket's history: when, who, and what happened in plain words.</summary>
/// <param name="Kind">A stable key the page can style by: created, status, reopened, edited, assigned, team, time, other.</param>
/// <param name="Note">What the person said, where they said something (a handover note).</param>
public sealed record TicketHistoryEntry(DateTimeOffset At, string? Who, string Kind, string Summary, string? Note = null);

public interface ITicketHistoryService
{
    /// <summary>
    /// The ticket's history, newest first. The caller has already found the ticket through the
    /// person's own scope; this reads only that ticket's own records.
    /// </summary>
    Task<IReadOnlyList<TicketHistoryEntry>> ForAsync(Desk.Domain.Tickets.Ticket ticket, CancellationToken ct = default);
}
