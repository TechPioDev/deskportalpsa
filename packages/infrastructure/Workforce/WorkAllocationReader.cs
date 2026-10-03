using Desk.Application.Tickets;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Planned work, as the capacity engine reads it. Every allocation is confirmed work: there is no
/// pencilled-in state yet. An allocation counts while it is in the plan and the work is still open;
/// work that finished before its planned time releases that time at once, whether or not the worker
/// has got round to cancelling the allocation.
///
/// The work's id goes to the caller only for tickets they may see, so a conflict can name the work
/// to someone who could open it and say only "taken" to anyone else.
/// </summary>
public sealed class WorkAllocationReader(DeskDbContext db, ITicketScopeQuery scope, TimeProvider clock) : IWorkAllocationReader
{
    public async Task<IReadOnlyList<AllocatedSpan>> ForAsync(
        Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (appUserIds.Count == 0) return [];
        var now = clock.GetUtcNow();
        // A piece of work is at most a day long, so one that started more than a day before the
        // range cannot reach into it: the index on (person, start) answers this without a scan.
        var earliest = from.AddDays(-1);
        var rows = await db.WorkAllocations.AsNoTracking()
            .Where(a => appUserIds.Contains(a.AppUserId) && a.Status == WorkAllocationStatus.Planned
                        && a.StartsAt >= earliest && a.StartsAt < to && a.EndsAt > from
                        && (a.StartsAt <= now
                            || (!a.Ticket!.PortalStatus.ToUpper().Contains(TicketStatusRules.ResolvedMarker)
                                && !a.Ticket.PortalStatus.ToUpper().Contains(TicketStatusRules.ClosedMarker))))
            .Select(a => new { a.Id, a.AppUserId, a.TicketId, a.StartsAt, a.EndsAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var visible = (await (await scope.VisibleAsync(db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)), callerId, Permissions.TicketsViewAll, ct))
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();

        return rows.Select(r => new AllocatedSpan(r.AppUserId, new Interval(r.StartsAt, r.EndsAt), Confirmed: true,
            visible.Contains(r.TicketId) ? r.TicketId : null, r.Id)).ToList();
    }
}
