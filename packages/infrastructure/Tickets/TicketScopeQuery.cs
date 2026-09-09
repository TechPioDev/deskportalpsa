using System.Linq.Expressions;
using Desk.Application.Authorization;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Translates an <see cref="EffectivePermission"/> into an actual, EF-translatable filter over
/// <see cref="Ticket"/>. Scope and board access are resolved into concrete id/name lists first
/// (real round trips), then composed into a single expression tree built from those already-known
/// values — never a query that calls back into the database mid-predicate, which is what keeps this
/// portable across the Postgres provider used in production and the in-memory one used in tests.
/// </summary>
public sealed class TicketScopeQuery(DeskDbContext db, IEffectivePermissionService permissions) : ITicketScopeQuery
{
    public async Task<IQueryable<Ticket>> VisibleAsync(
        IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct = default)
    {
        var eff = await permissions.ResolveAsync(appUserId, permissionKey, ct);
        if (eff.IsDenied) return source.Where(_ => false);

        var scoped = eff.Scope switch
        {
            PermissionScope.All => source,
            // Board grants are passed in because they change what "assigned to me" should include:
            // a technician who covers a board needs to see its unclaimed queue, or there is nothing
            // for them to pick up and work never starts.
            PermissionScope.Assigned or PermissionScope.Own =>
                await AssignedOnlyAsync(source, appUserId, eff.BoardMode == BoardAccessMode.Selected, ct),
            PermissionScope.Department => await GroupOrUnassignedAsync(source, appUserId, byTeam: false, ct),
            PermissionScope.Team => await GroupOrUnassignedAsync(source, appUserId, byTeam: true, ct),
            // Selected has no meaning for a ticket-visibility scope (it belongs to board access,
            // resolved separately below) and None was already handled above — anything else is a
            // permission this query was never taught, so it fails closed rather than guessing.
            _ => source.Where(_ => false),
        };

        return eff.BoardMode switch
        {
            BoardAccessMode.All => scoped,
            BoardAccessMode.None => scoped.Where(_ => false),
            BoardAccessMode.Selected => scoped.Where(BoardFilter(eff.BoardGrants)),
            _ => scoped.Where(_ => false),
        };
    }

    public async Task<Ticket?> FindAsync(
        IQueryable<Ticket> source, Guid ticketId, Guid appUserId, string permissionKey, CancellationToken ct = default)
    {
        var visible = await VisibleAsync(source, appUserId, permissionKey, ct);
        return await visible.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    /// <summary>
    /// What "assigned to me" means for someone who may exist in the PSA, only in the portal, or both.
    ///
    /// Two identities, OR'd rather than chosen between. A technician linked to a PSA resource keeps
    /// seeing everything the provider assigned them; one who exists only here sees what the portal
    /// assigned them. Picking one identity would have broken the other group, and the whole point is
    /// that a desk can run both at once.
    ///
    /// This used to return NOTHING for an unlinked user, on the reasoning that nothing could ever be
    /// assigned to someone the PSA has never heard of. That was true when assignment lived only in
    /// the PSA. It stopped being true the moment the portal could assign, and until then it meant
    /// forty technicians would have signed in to an empty list.
    ///
    /// <paramref name="includeUnclaimed"/> adds the unassigned queue, and is passed only when the
    /// caller has board grants — the board filter applied after this narrows it to their boards. For
    /// a caller with no grants it stays off, because "every unassigned ticket in the tenant" is not
    /// a queue, it is a firehose.
    /// </summary>
    private async Task<IQueryable<Ticket>> AssignedOnlyAsync(
        IQueryable<Ticket> source, Guid appUserId, bool includeUnclaimed, CancellationToken ct)
    {
        var me = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == appUserId)
            .Select(u => u.ExternalTechnicianId)
            .FirstOrDefaultAsync(ct);
        var linked = !string.IsNullOrEmpty(me);

        // Written out per case rather than composed from a shared helper: a predicate that calls a
        // method cannot be translated to SQL, and one built from captured booleans reads worse than
        // the four sentences it replaces. "Unclaimed" means nobody has it on EITHER side — a ticket
        // the PSA assigned to the integration's own API user carries an external id and so is not
        // unclaimed, which is right: it belongs to whoever the portal gave it to, and showing it in
        // everyone's queue would invite two technicians onto the same work.
        return (linked, includeUnclaimed) switch
        {
            (true, false) => source.Where(t =>
                t.AssignedAppUserId == appUserId || t.AssignedTechnicianExternalId == me),
            (true, true) => source.Where(t =>
                t.AssignedAppUserId == appUserId || t.AssignedTechnicianExternalId == me
                || (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null)),
            (false, false) => source.Where(t => t.AssignedAppUserId == appUserId),
            (false, true) => source.Where(t =>
                t.AssignedAppUserId == appUserId
                || (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null)),
        };
    }

    /// <summary>
    /// Department/Team scope, resolved via the assigned technician's department/team membership —
    /// Ticket carries no department/team of its own (see the Phase 1 design note on why one was
    /// deliberately not added), so this is a join through AppUser.ExternalTechnicianId.
    ///
    /// An unassigned ticket has no technician and so cannot join to anything, but per the explicit
    /// product decision it must stay visible rather than vanish from the unclaimed queue — so the
    /// membership match is OR'd with "unassigned", not AND'd.
    /// </summary>
    private async Task<IQueryable<Ticket>> GroupOrUnassignedAsync(
        IQueryable<Ticket> source, Guid appUserId, bool byTeam, CancellationToken ct)
    {
        List<Guid> groupIds = byTeam
            ? await db.UserTeams.AsNoTracking().Where(ut => ut.AppUserId == appUserId).Select(ut => ut.TeamId).ToListAsync(ct)
            : await db.UserDepartments.AsNoTracking().Where(ud => ud.AppUserId == appUserId).Select(ud => ud.DepartmentId).ToListAsync(ct);

        if (groupIds.Count == 0)
            // Not a member of anything: only the unclaimed queue is visible, nothing "shared".
            return source.Where(t => t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null);

        // Both identities of every member, because a department contains both kinds of technician
        // and a manager who could see only the PSA-linked half would be reading a partial team.
        var memberIds = byTeam
            ? await db.UserTeams.AsNoTracking().Where(ut => groupIds.Contains(ut.TeamId))
                .Select(ut => ut.AppUserId).Distinct().ToListAsync(ct)
            : await db.UserDepartments.AsNoTracking().Where(ud => groupIds.Contains(ud.DepartmentId))
                .Select(ud => ud.AppUserId).Distinct().ToListAsync(ct);

        var technicianIds = await db.AppUsers.AsNoTracking()
            .Where(u => memberIds.Contains(u.Id) && u.ExternalTechnicianId != null)
            .Select(u => u.ExternalTechnicianId!)
            .Distinct().ToListAsync(ct);

        return source.Where(t =>
            (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null)
            || (t.AssignedAppUserId != null && memberIds.Contains(t.AssignedAppUserId.Value))
            || (t.AssignedTechnicianExternalId != null && technicianIds.Contains(t.AssignedTechnicianExternalId!)));
    }

    /// <summary>
    /// One board grant per (connection, board name); ORs across the distinct connections a caller
    /// has grants on. Built as an explicit OR-chain over per-connection clauses — each clause's
    /// "boards.Contains(...)" is a plain local-array Contains, which every EF provider translates to
    /// IN — rather than a single Any() over a nested collection, which most providers cannot.
    /// </summary>
    private static Expression<Func<Ticket, bool>> BoardFilter(IReadOnlyList<BoardGrant> grants)
    {
        if (grants.Count == 0) return _ => false;

        Expression<Func<Ticket, bool>>? combined = null;
        foreach (var group in grants.GroupBy(g => g.PsaConnectionId))
        {
            var connectionId = group.Key;
            var boardNames = group.Select(g => g.BoardName).ToArray();
            Expression<Func<Ticket, bool>> clause = t => t.PsaConnectionId == connectionId && boardNames.Contains(t.QueueOrBoard!);
            combined = combined is null ? clause : Or(combined, clause);
        }
        return combined!;
    }

    private static Expression<Func<Ticket, bool>> Or(Expression<Func<Ticket, bool>> left, Expression<Func<Ticket, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<Ticket, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
