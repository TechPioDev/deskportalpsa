using Desk.Domain.Tickets;

namespace Desk.Application.Tickets;

/// <summary>
/// The opt-in review of resolved work. A board lead approves it, and it closes; or sends it back with
/// a note, and it returns to work. Nobody reviews their own work.
/// </summary>
public interface ITicketReviewService
{
    /// <param name="reviewerId">The lead reviewing. Must not be the person who holds the ticket.</param>
    /// <param name="note">Required when sending back: what needs doing. Optional on approval.</param>
    Task ReviewAsync(Ticket ticket, Guid reviewerId, string reviewerName, bool approve, string? note, CancellationToken ct = default);
}

/// <summary>A link as seen from one ticket: what the other ticket is to this one.</summary>
/// <param name="Relation">From this ticket's side: "Related to", "Duplicate of", "Duplicated by", "Parent of", "Part of", "Blocks", "Blocked by".</param>
public sealed record TicketLinkDto(
    Guid Id, string Relation, Guid OtherTicketId, string? OtherReference, string OtherTitle, string OtherStatus,
    Desk.Domain.Enums.TicketOrigin OtherOrigin);

public interface ITicketLinkService
{
    /// <summary>Links on the ticket, both directions, to tickets the person can see. Others are left out, not named.</summary>
    Task<IReadOnlyList<TicketLinkDto>> ListAsync(Ticket ticket, Guid appUserId, CancellationToken ct = default);

    /// <summary>Links the ticket to another the person can also see. The kind reads from this ticket's side.</summary>
    Task<TicketLinkDto> AddAsync(Ticket ticket, Guid appUserId, Guid otherTicketId, TicketLinkKind kind, CancellationToken ct = default);

    Task RemoveAsync(Ticket ticket, Guid appUserId, Guid linkId, CancellationToken ct = default);
}
