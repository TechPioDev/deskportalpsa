using Desk.Application.Admin;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Who is watching a ticket. A follower is not an assignee: they are not accountable for the work,
/// which is why putting somebody on a ticket is not a change of assignment and never touches who
/// holds it.
///
/// Every method resolves the ticket through the caller's own TicketsViewAll scope first. Following
/// is otherwise a way to read a ticket list you were never meant to see — "add me to ticket X" tells
/// you X exists, and the followed-tickets view would then show it.
/// </summary>
public sealed class TicketFollowerService(
    DeskDbContext db, ITicketScopeQuery scopeQuery, ICurrentUser user, IAuditWriter audit) : ITicketFollowerService
{
    public async Task<IReadOnlyList<TicketFollowerDto>> ListAsync(Guid ticketId, CancellationToken ct = default)
    {
        await LoadAsync(ticketId, Permissions.TicketsViewAll, ct);
        return await ReadAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<TicketFollowerDto>> AddAsync(Guid ticketId, Guid appUserId, CancellationToken ct = default)
    {
        // Changing who watches a ticket is a change to the ticket, so it takes the permission that
        // changing a ticket takes — not merely the one for reading it.
        var ticket = await LoadAsync(ticketId, Permissions.TicketsUpdate, ct);

        var follower = await db.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == appUserId && u.IsActive, ct)
            ?? throw new ValidationFailedException("That user does not exist, or is not active.");

        // Following twice is the same as following once. Returning the list either way keeps the
        // caller's copy correct without making them handle a conflict that means nothing.
        var already = await db.TicketFollowers.AnyAsync(f => f.TicketId == ticketId && f.AppUserId == appUserId, ct);
        if (!already)
        {
            db.TicketFollowers.Add(new TicketFollower
            {
                MspOrganizationId = ticket.MspOrganizationId,
                TicketId = ticketId,
                AppUserId = appUserId,
                AddedByUserId = user.UserId,
            });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("ticket.follower.added", "Ticket", ticketId.ToString(),
                new { appUserId, follower.DisplayName }, ct);
        }
        return await ReadAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<TicketFollowerDto>> RemoveAsync(Guid ticketId, Guid appUserId, CancellationToken ct = default)
    {
        await LoadAsync(ticketId, Permissions.TicketsUpdate, ct);
        var row = await db.TicketFollowers.FirstOrDefaultAsync(f => f.TicketId == ticketId && f.AppUserId == appUserId, ct);
        if (row is not null)
        {
            db.TicketFollowers.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("ticket.follower.removed", "Ticket", ticketId.ToString(), new { appUserId }, ct);
        }
        return await ReadAsync(ticketId, ct);
    }

    /// <summary>
    /// The tickets the caller follows. Not filtered by the view scope: a follow row only exists
    /// because somebody with the scope to see the ticket put it there, and the list page resolves
    /// each id through its own scoped query anyway.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> FollowedTicketIdsAsync(CancellationToken ct = default)
        => user.UserId is { } uid
            ? await db.TicketFollowers.AsNoTracking()
                .Where(f => f.AppUserId == uid)
                .Select(f => f.TicketId)
                .ToListAsync(ct)
            : [];

    private async Task<Ticket> LoadAsync(Guid ticketId, string permission, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        return await scopeQuery.FindAsync(db.Tickets, ticketId, uid, permission, ct)
            ?? throw new NotFoundException("Ticket");
    }

    private async Task<IReadOnlyList<TicketFollowerDto>> ReadAsync(Guid ticketId, CancellationToken ct)
        => await db.TicketFollowers.AsNoTracking()
            .Where(f => f.TicketId == ticketId)
            .Join(db.AppUsers.AsNoTracking(), f => f.AppUserId, u => u.Id, (f, u) => new { f.CreatedAt, u.Id, u.DisplayName, u.Email })
                // Ordered on the column, not on the DTO: ordering by a property of a constructed
                // record does not translate to SQL, and only the in-memory test provider accepts it.
                .OrderBy(x => x.DisplayName)
                .Select(x => new TicketFollowerDto(x.Id, x.DisplayName, x.Email, x.Id == user.UserId, x.CreatedAt))
            .ToListAsync(ct);
}
