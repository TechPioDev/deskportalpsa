using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// TicketScopeQueryTests runs against the in-memory provider, which is more forgiving than a real
/// SQL backend — it silently falls back to client-side evaluation for expressions a real provider
/// would reject outright. These tests build the SAME expression shapes against the actual Npgsql
/// provider and ask EF to compile them to SQL (never executing — no connection is opened), so a
/// predicate that only "works" because the in-memory provider is lenient fails here loudly instead
/// of failing in production. The board-access OR-chain (built via manual Expression.OrElse) is the
/// one genuinely novel piece of expression-tree surgery in this phase, so it gets the most scrutiny.
/// </summary>
public class TicketScopeQueryPostgresTranslationTests
{
    private static DeskDbContext NpgsqlDb()
    {
        var tenant = new TenantContext();
        tenant.SetPlatformScope();
        var options = new DbContextOptionsBuilder<DeskDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only;Username=x;Password=x")
            .Options;
        return new DeskDbContext(options, tenant, TimeProvider.System);
    }

    [Fact]
    public void Board_fence_OrElse_chain_compiles_to_sql()
    {
        using var db = NpgsqlDb();
        var connA = Guid.NewGuid();
        var connB = Guid.NewGuid();
        var boardsA = new[] { "Help Desk", "NOC" };
        var boardsB = new[] { "Projects" };

        // Reproduces TicketScopeQuery.BoardFilter's exact shape: two per-connection clauses
        // combined by rebinding parameters and Expression.OrElse, not Expression.Invoke.
        var left = (System.Linq.Expressions.Expression<Func<Desk.Domain.Tickets.Ticket, bool>>)
            (t => t.PsaConnectionId == connA && boardsA.Contains(t.QueueOrBoard!));
        var right = (System.Linq.Expressions.Expression<Func<Desk.Domain.Tickets.Ticket, bool>>)
            (t => t.PsaConnectionId == connB && boardsB.Contains(t.QueueOrBoard!));

        var param = left.Parameters[0];
        var rightBody = new ParamSwap(right.Parameters[0], param).Visit(right.Body)!;
        var combined = System.Linq.Expressions.Expression.Lambda<Func<Desk.Domain.Tickets.Ticket, bool>>(
            System.Linq.Expressions.Expression.OrElse(left.Body, rightBody), param);

        var act = () => db.Tickets.Where(combined).ToQueryString();

        act.Should().NotThrow("a predicate this shape has to survive real SQL translation, not just the lenient in-memory provider");
    }

    [Fact]
    public void Assigned_scope_filter_compiles_to_sql()
    {
        using var db = NpgsqlDb();
        const string me = "tech-123";

        var act = () => db.Tickets.Where(t => t.AssignedTechnicianExternalId == me).ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dual_identity_assigned_scope_compiles_to_sql()
    {
        // "Assigned to me" now means either identity, plus the unclaimed queue for a board holder.
        // The first draft of this expressed "unclaimed" as a static helper method called inside the
        // predicate — which the in-memory provider evaluates client-side without complaint and
        // Npgsql cannot translate at all. This is the test that would have caught it.
        using var db = NpgsqlDb();
        var appUserId = Guid.NewGuid();
        const string me = "tech-123";

        var act = () => db.Tickets
            .Where(t => t.AssignedAppUserId == appUserId
                        || t.AssignedTechnicianExternalId == me
                        || (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null))
            .ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void Department_scope_across_both_identities_compiles_to_sql()
    {
        using var db = NpgsqlDb();
        var memberIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var technicianIds = new List<string> { "tech-1", "tech-2" };

        // Guid list Contains over a NULLABLE column is the part worth pinning: it needs the .Value
        // access to compile in C#, and that is exactly the shape a provider can refuse.
        var act = () => db.Tickets
            .Where(t => (t.AssignedAppUserId == null && t.AssignedTechnicianExternalId == null)
                        || (t.AssignedAppUserId != null && memberIds.Contains(t.AssignedAppUserId.Value))
                        || (t.AssignedTechnicianExternalId != null && technicianIds.Contains(t.AssignedTechnicianExternalId!)))
            .ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void Department_scope_unassigned_or_in_technician_list_compiles_to_sql()
    {
        using var db = NpgsqlDb();
        var technicianIds = new List<string> { "tech-1", "tech-2" };

        var act = () => db.Tickets
            .Where(t => t.AssignedTechnicianExternalId == null || technicianIds.Contains(t.AssignedTechnicianExternalId!))
            .ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void Board_actions_bitwise_flag_check_compiles_to_sql()
    {
        using var db = NpgsqlDb();
        const BoardAction required = BoardAction.Edit;

        // Mirrors EffectivePermissionService.ResolveBoardAccessAsync's flag-containment check.
        var act = () => db.UserBoardGrants
            .Where(g => (g.Actions & required) == required)
            .ToQueryString();

        act.Should().NotThrow();
    }

    [Fact]
    public void Board_options_join_and_order_compiles_to_sql()
    {
        // Reproduces UserAdminService.BoardsAsync's exact shape. This is the query that actually
        // shipped broken: ordering by a property read off a positional record built in the Join's
        // result selector doesn't translate — EF can't see through the constructor to know which
        // argument maps to the join's c.Name. The in-memory provider evaluated it client-side and
        // never complained; this is the test that would have caught it before it reached prod.
        using var db = NpgsqlDb();

        var act = () => db.Tickets
            .Where(t => t.QueueOrBoard != null)
            .Select(t => new { t.PsaConnectionId, t.QueueOrBoard })
            .Distinct()
            .Join(db.PsaConnections, t => t.PsaConnectionId, c => c.Id,
                (t, c) => new { t.PsaConnectionId, ConnectionName = c.Name, BoardName = t.QueueOrBoard! })
            .OrderBy(b => b.ConnectionName).ThenBy(b => b.BoardName)
            .Select(b => new Desk.Application.Admin.BoardOptionDto(b.PsaConnectionId, b.ConnectionName, b.BoardName))
            .ToQueryString();

        act.Should().NotThrow("boards are a live per-tenant derivation hit on every Users-page load — this must survive real SQL translation");
    }

    private sealed class ParamSwap(
        System.Linq.Expressions.ParameterExpression from, System.Linq.Expressions.ParameterExpression to)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(System.Linq.Expressions.ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
