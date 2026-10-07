using Desk.Domain.Tickets;

namespace Desk.Application.Tickets;

/// <summary>
/// The one place a staff-side ticket query gets narrowed to what the caller may actually see.
/// Every server path that lists or loads a ticket for a staff user — not a client-portal user,
/// which already has its own company-scoped predicate — must go through this rather than querying
/// <c>Ticket</c> directly, or a caller correctly hidden from the list can still reach the same row
/// by id.
/// </summary>
public interface ITicketScopeQuery
{
    /// <summary>
    /// Narrows <paramref name="source"/> to the tickets the given user may reach under
    /// <paramref name="permissionKey"/>. Returns an empty (never-matching) query when the caller has
    /// no grant at all — never throws, so a caller can compose this into a query and still get a
    /// normal empty result rather than an exception mid-request.
    /// </summary>
    Task<IQueryable<Ticket>> VisibleAsync(
        IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct = default);

    /// <summary>Convenience for the common "may this user touch this one ticket" check, used by
    /// every mutation endpoint that currently loads a ticket by id with no scope predicate at all.</summary>
    /// <summary>
    /// True when nothing narrows this user's sight of the tickets that came from a PSA: they may
    /// see every one of them. (The team's own boards are still a matter of membership.) A read
    /// that only wants to know which PSA logins appear on visible tickets can then be answered
    /// from an index, with no ticket visited to ask whether it may be seen. False whenever that is
    /// not certain, and by default: the caller then checks each ticket, as it always did.
    /// </summary>
    Task<bool> SeesEveryPsaTicketAsync(Guid appUserId, CancellationToken ct = default) => Task.FromResult(false);

    Task<Ticket?> FindAsync(
        IQueryable<Ticket> source, Guid ticketId, Guid appUserId, string permissionKey, CancellationToken ct = default);
}
