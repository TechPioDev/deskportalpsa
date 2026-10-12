using System.Linq.Expressions;
using Desk.Application.Authorization;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
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
        var byKey = await NarrowAsync(source, appUserId, permissionKey, ct);
        if (permissionKey.StartsWith("tickets.view.", StringComparison.Ordinal)
            || !permissionKey.StartsWith("tickets.", StringComparison.Ordinal))
            return byKey;

        // Nobody acts on a ticket they cannot see. The Standard Technician role grants note, time
        // and update at All but sight only of their own tickets; without this, a technician could
        // post on a colleague's ticket they would get a 404 for opening.
        return await NarrowAsync(byKey, appUserId, Permissions.TicketsViewAll, ct);
    }

    public async Task<bool> SeesEveryPsaTicketAsync(Guid appUserId, CancellationToken ct = default)
    {
        // The two things NarrowAsync narrows a PSA ticket by: the scope of the grant, and which
        // provider queues the person has been given. Board membership, the third, lets every PSA
        // ticket through.
        var eff = await ResolveViewAsync(appUserId, Permissions.TicketsViewAll, ct);
        return !eff.IsDenied && eff.Scope == PermissionScope.All && eff.BoardMode == BoardAccessMode.All;
    }

    private async Task<IQueryable<Ticket>> NarrowAsync(
        IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct)
    {
        var eff = await ResolveViewAsync(appUserId, permissionKey, ct);
        if (eff.IsDenied) return source.Where(_ => false);

        Expression<Func<Ticket, bool>>? byScope = eff.Scope switch
        {
            PermissionScope.All => null,
            // Board grants are passed in because they change what "assigned to me" should include:
            // a technician who covers a board needs to see its unclaimed queue, or there is nothing
            // for them to pick up and work never starts.
            PermissionScope.Assigned or PermissionScope.Own =>
                await AssignedOnlyAsync(appUserId, eff.BoardMode == BoardAccessMode.Selected, ct),
            PermissionScope.Department => await GroupOrUnassignedAsync(appUserId, byTeam: false, ct),
            PermissionScope.Team => await GroupOrUnassignedAsync(appUserId, byTeam: true, ct),
            // Selected has no meaning for a ticket-visibility scope (it belongs to board access,
            // resolved separately below) and None was already handled above — anything else is a
            // permission this query was never taught, so it fails closed rather than guessing.
            _ => _ => false,
        };

        // The team's own boards are shared by membership, not by assignment: "everyone on the team
        // can see and take these tickets" is the board's promise, and an unclaimed job nobody can see
        // is one nobody takes. So a narrowed scope still reaches every internal and monitoring
        // ticket, and BoardMembership below cuts that down to the boards this person belongs to.
        var scoped = byScope is null ? source
            : eff.Scope is PermissionScope.Assigned or PermissionScope.Own or PermissionScope.Department or PermissionScope.Team
                ? source.Where(Or(byScope, OnATeamBoard))
                : source.Where(byScope);

        var byBoardAccess = eff.BoardMode switch
        {
            BoardAccessMode.All => scoped,
            BoardAccessMode.None => scoped.Where(_ => false),
            BoardAccessMode.Selected => scoped.Where(BoardFilter(eff.BoardGrants)),
            _ => scoped.Where(_ => false),
        };

        return byBoardAccess.Where(BoardMembership(appUserId));
    }

    /// <summary>
    /// Staff ticket visibility is asked for as <see cref="Permissions.TicketsViewAll"/>, but a
    /// technician holds it as <see cref="Permissions.TicketsViewAssigned"/> - the legacy keys bake the
    /// scope into the name. So a request for the one also weighs the other, and the wider wins: an
    /// admin keeps All, a technician gets Assigned (their tickets, and the unclaimed queue of boards
    /// they are granted), and someone holding neither still sees nothing.
    /// </summary>
    private async Task<EffectivePermission> ResolveViewAsync(Guid appUserId, string permissionKey, CancellationToken ct)
    {
        var eff = await permissions.ResolveAsync(appUserId, permissionKey, ct);
        if (permissionKey != Permissions.TicketsViewAll || eff.Scope == PermissionScope.All) return eff;

        var assigned = await permissions.ResolveAsync(appUserId, Permissions.TicketsViewAssigned, ct);
        return Width(assigned.Scope) > Width(eff.Scope) ? assigned : eff;
    }

    private static int Width(PermissionScope scope) => scope switch
    {
        PermissionScope.All => 4,
        PermissionScope.Department => 3,
        PermissionScope.Team => 2,
        PermissionScope.Assigned or PermissionScope.Own => 1,
        _ => 0,
    };

    /// <summary>
    /// An internal board with no members is open to the whole team, which is what a team that wants
    /// to see each other's work asks for. Naming members narrows it to them — for a board that is
    /// not general reading — and the ticket's own holder always keeps sight of it either way.
    /// A PSA ticket is unaffected: provider queues are governed by board access above.
    /// </summary>
    private Expression<Func<Ticket, bool>> BoardMembership(Guid appUserId) =>
        t => t.Origin == TicketOrigin.Psa
             || t.BoardId == null
             || t.AssignedAppUserId == appUserId
             || t.CreatedByUserId == appUserId
             || !db.BoardMembers.Any(m => m.BoardId == t.BoardId)
             || db.BoardMembers.Any(m => m.BoardId == t.BoardId && m.AppUserId == appUserId);

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
    private static readonly Expression<Func<Ticket, bool>> OnATeamBoard =
        t => t.Origin != TicketOrigin.Psa && t.BoardId != null;

    private static Expression<Func<Ticket, bool>> Where(Expression<Func<Ticket, bool>> predicate) => predicate;

    private async Task<Expression<Func<Ticket, bool>>> AssignedOnlyAsync(
        Guid appUserId, bool includeUnclaimed, CancellationToken ct)
    {
        // A ticket this person raised is theirs too (only internal tickets record a raiser): a
        // technician who logs a job for the team must not watch it vanish the moment it is saved.
        var mine = Where(t => t.AssignedAppUserId == appUserId || t.CreatedByUserId == appUserId);
        if (HeldByLogin(await PsaLoginsAsync([appUserId], ct)) is { } byLogin)
            mine = Or(mine, byLogin);

        // "Unclaimed" means nobody has it on EITHER side - a ticket the PSA assigned to the
        // integration's own API user carries an external id and so is not unclaimed, which is right:
        // it belongs to whoever the portal gave it to, and showing it in everyone's queue would
        // invite two technicians onto the same work.
        return includeUnclaimed
            ? Or(mine, Where(t => t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null))
            : mine;
    }

    /// <summary>
    /// Department/Team scope, resolved via the assigned technician's department/team membership -
    /// Ticket carries no department/team of its own (see the Phase 1 design note on why one was
    /// deliberately not added), so this is a join through each member's portal account and through
    /// the PSA logins they are linked to.
    ///
    /// An unassigned ticket has no technician and so cannot join to anything, but per the explicit
    /// product decision it must stay visible rather than vanish from the unclaimed queue - so the
    /// membership match is OR'd with "unassigned", not AND'd.
    /// </summary>
    private async Task<Expression<Func<Ticket, bool>>> GroupOrUnassignedAsync(
        Guid appUserId, bool byTeam, CancellationToken ct)
    {
        List<Guid> groupIds = byTeam
            ? await db.UserTeams.AsNoTracking().Where(ut => ut.AppUserId == appUserId).Select(ut => ut.TeamId).ToListAsync(ct)
            : await db.UserDepartments.AsNoTracking().Where(ud => ud.AppUserId == appUserId).Select(ud => ud.DepartmentId).ToListAsync(ct);

        if (groupIds.Count == 0)
            // Not a member of anything: only the unclaimed queue is visible, nothing "shared".
            return Where(t => t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null);

        // Both identities of every member, because a department contains both kinds of technician
        // and a manager who could see only the PSA-linked half would be reading a partial team.
        var memberIds = byTeam
            ? await db.UserTeams.AsNoTracking().Where(ut => groupIds.Contains(ut.TeamId))
                .Select(ut => ut.AppUserId).Distinct().ToListAsync(ct)
            : await db.UserDepartments.AsNoTracking().Where(ud => groupIds.Contains(ud.DepartmentId))
                .Select(ud => ud.AppUserId).Distinct().ToListAsync(ct);

        var shared = Where(t =>
            (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null)
            || (t.AssignedAppUserId != null && memberIds.Contains(t.AssignedAppUserId.Value)));
        return HeldByLogin(await PsaLoginsAsync(memberIds, ct)) is { } byLogin ? Or(shared, byLogin) : shared;
    }

    /// <summary>
    /// The PSA logins these people are linked to, each with its connection - never a login alone.
    /// The same id on another PSA account is a different login and usually a different person, so
    /// matching on the id by itself would show one technician another's tickets as soon as a desk
    /// has two accounts. (It used to be read from a single id kept on the person, which no code
    /// ever filled in: a linked technician was shown nothing the PSA had assigned them.)
    ///
    /// A link to the account a connection writes AS is left out, as <see cref="PsaLinks"/> does:
    /// that account holds the whole portal team's work, so treating it as one person's login would
    /// hand them everyone's.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Connection, string Login)>> PsaLoginsAsync(
        IReadOnlyCollection<Guid> appUserIds, CancellationToken ct)
    {
        var rows = await db.UserPsaIdentities.AsNoTracking()
            .Where(i => appUserIds.Contains(i.AppUserId))
            .Select(i => new { i.PsaConnectionId, i.ExternalTechnicianId })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var account = await IntegrationIdentity.LoadAsync(db, ct);
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ExternalTechnicianId)
                        && !account.IsAccount(r.PsaConnectionId, r.ExternalTechnicianId))
            .Select(r => (r.PsaConnectionId, r.ExternalTechnicianId.Trim()))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// "The PSA assigned it to one of these logins": one clause per connection, OR'd, each a plain
    /// local-array Contains. Null when there are no logins, so the caller adds nothing.
    /// </summary>
    private static Expression<Func<Ticket, bool>>? HeldByLogin(IReadOnlyList<(Guid Connection, string Login)> logins)
    {
        Expression<Func<Ticket, bool>>? combined = null;
        foreach (var group in logins.GroupBy(l => l.Connection))
        {
            var connectionId = group.Key;
            var ids = group.Select(l => l.Login).ToArray();
            Expression<Func<Ticket, bool>> clause = t =>
                t.PsaConnectionId == connectionId && ids.Contains(t.AssignedTechnicianExternalId!);
            combined = combined is null ? clause : Or(combined, clause);
        }
        return combined;
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

        // Board grants name PSA queues, which the team's own boards are not. Restricting somebody to
        // certain provider queues says nothing about whether they may see the team's internal work,
        // and silently hiding it would empty the board for exactly the people who work it. Internal
        // boards carry their own membership instead, applied before this.
        Expression<Func<Ticket, bool>> notAProviderQueue = t => t.Origin != TicketOrigin.Psa;
        return Or(combined!, notAProviderQueue);
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
