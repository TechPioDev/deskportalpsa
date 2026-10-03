using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Workforce;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Work that finished - resolved or closed, in the portal or in the PSA - comes out of people's
/// FUTURE plans, so nobody's day shows an hour reserved for a ticket that is already done. Planned
/// time that has started or passed is left as it was: it is history, and the planned-versus-actual
/// figures of a later phase want it. The capacity engine ignores such allocations the moment the
/// ticket finishes; this makes the record say so too, and leaves an audit entry.
///
/// A deleted ticket takes its allocations with it (the foreign key cascades); nothing to do here.
/// </summary>
public sealed class WorkAllocationReleaser(DeskDbContext db, IAuditWriter audit, TimeProvider clock) : IWorkAllocationReleaser
{
    public async Task<int> ReleaseFinishedAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rows = await db.WorkAllocations.Include(a => a.Ticket)
            .Where(a => a.Status == WorkAllocationStatus.Planned && a.StartsAt > now
                        && (a.Ticket!.PortalStatus.ToUpper().Contains(TicketStatusRules.ResolvedMarker)
                            || a.Ticket.PortalStatus.ToUpper().Contains(TicketStatusRules.ClosedMarker)))
            .OrderBy(a => a.StartsAt).Take(500).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        foreach (var row in rows)
        {
            row.Status = WorkAllocationStatus.Cancelled;
            row.CancelledAt = now;
            row.CancelReason = "The work finished before its planned time.";
            row.Version++;
        }
        // One save and one audit entry for the pass: a batch is one event, not five hundred round trips.
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.allocation.released", "WorkAllocation", null, new
        {
            count = rows.Count,
            released = rows.Select(row => new
            {
                id = row.Id, ticketId = row.TicketId, reference = WorkPlanService.Reference(row.Ticket!), status = row.Ticket!.PortalStatus,
                appUserId = row.AppUserId, was = $"{row.StartsAt:u} to {row.EndsAt:u}",
            }).ToList(),
        }, ct);
        return rows.Count;
    }
}

/// <summary>The worker's loop: every organization that has any planned work in the future.</summary>
public sealed class WorkAllocationReleaseRunner(IServiceScopeFactory scopes, ILogger<WorkAllocationReleaseRunner> logger) : IWorkAllocationReleaseRunner
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        List<Guid> orgs;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            orgs = await db.WorkAllocations.AsNoTracking()
                .Where(a => a.Status == WorkAllocationStatus.Planned && a.StartsAt > now)
                .Select(a => a.MspOrganizationId).Distinct().ToListAsync(ct);
        }
        foreach (var org in orgs)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
            try
            {
                var released = await scope.ServiceProvider.GetRequiredService<IWorkAllocationReleaser>().ReleaseFinishedAsync(ct);
                if (released > 0) logger.LogInformation("Planned work released for finished tickets: {Count}", released);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Releasing finished planned work failed for an organization");
            }
        }
    }
}
