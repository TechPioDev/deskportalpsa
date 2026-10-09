using Desk.Domain.Tickets;

namespace Desk.Application.Tickets;

/// <summary>
/// How a ticket's due date stands after somebody moved it: the date it is due now, the date it
/// was due before the first extension, how many times it was moved, and the last move's reason
/// and author. Staff only; a client is never sent the reason.
/// </summary>
public sealed record TicketDueDateDto(
    DateTimeOffset? DueAt,
    DateTimeOffset? OriginalDueAt,
    int Extensions,
    DateTimeOffset? LastExtendedAt,
    string? LastExtendedByName,
    string? LastReason);

public sealed record ExtendDueDateInput(DateTimeOffset DueAt, string Reason);

/// <summary>
/// Moving a ticket's due date later, with a reason, by somebody who may change the ticket.
///
/// A PSA ticket's due date is the PSA's: the new date is written there first and kept here only
/// once the PSA has accepted it, and the next sync reads it back as the PSA holds it. A board
/// ticket's is the portal's own. Either way the date it was first due is kept, so the figures
/// can tell an extended ticket from one that was simply on time, and every move is audited
/// with its reason, which is what the ticket's history shows.
/// </summary>
public interface ITicketDueDateService
{
    Task<TicketDueDateDto> ExtendAsync(Ticket ticket, ExtendDueDateInput input, CancellationToken ct = default);
}
