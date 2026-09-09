using Desk.Api.Controllers;
using Desk.Application.Analytics;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The technician dashboard took its technician filter straight from the query string, so anyone
/// holding the own-productivity permission could read a named colleague's figures by passing their
/// id — or the whole organization's by passing none, since an absent filter means "no restriction"
/// downstream. A Technician holds exactly that permission, so this was reachable by every
/// technician, with no id guessing: just an edited URL.
///
/// These tests pin the clamp that fixes it, by capturing the filter the controller actually hands
/// to the metrics service.
/// </summary>
public class ProductivityScopeTests
{
    private sealed class CapturingMetrics : ITechnicianMetricsService
    {
        public MetricsFilter? Captured { get; private set; }

        public Task<TechnicianMetrics> ForTechnicianAsync(MetricsFilter f, ProductivityWeights w, CancellationToken ct = default)
        {
            Captured = f;
            return Task.FromResult(new TechnicianMetrics { TechnicianExternalId = f.TechnicianExternalId ?? "(all)" });
        }

        public Task<IReadOnlyList<TeamComparisonRow>> TeamAsync(MetricsFilter f, ProductivityWeights w, CancellationToken ct = default)
        {
            Captured = f;
            return Task.FromResult<IReadOnlyList<TeamComparisonRow>>([]);
        }

        public Task<IReadOnlyList<TechnicianDay>> DailyAsync(MetricsFilter f, CancellationToken ct = default)
        {
            Captured = f;
            return Task.FromResult<IReadOnlyList<TechnicianDay>>([]);
        }

        public Task<IReadOnlyList<TrendPoint>> TrendAsync(MetricsFilter f, CancellationToken ct = default)
        {
            Captured = f;
            return Task.FromResult<IReadOnlyList<TrendPoint>>([]);
        }
    }

    // Typed on the interface, not one stub: two identity shapes are exercised here — an account
    // with a portal user and one with no identity at all — and they are different classes.
    private static DashboardController Controller(
        CapturingMetrics metrics, Desk.Application.Abstractions.ICurrentUser user)
        => new(metrics, new UnusedClientWorkload(), new UnusedCoverage(), user);

    /// <summary>These tests are about technician scoping; the coverage surface is not exercised.</summary>
    private sealed class UnusedCoverage : Desk.Application.Analytics.IPortalCoverageService
    {
        public Task<Desk.Application.Analytics.PortalCoverageReport> CoverageAsync(
            Desk.Application.Analytics.MetricsFilter filter, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>These tests are about technician scoping; the client surface is not exercised.</summary>
    private sealed class UnusedClientWorkload : Desk.Application.Analytics.IClientWorkloadService
    {
        public Task<Desk.Application.Analytics.ClientWorkloadReport> ForClientsAsync(
            Desk.Application.Analytics.MetricsFilter filter, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Minimal ICurrentUser we can point at a specific permission set + technician id.</summary>
    private sealed class ICurrentUserStub(string? tech, params string[] perms) : Desk.Application.Abstractions.ICurrentUser
    {
        // Stable, not a fresh Guid per access. UserId is now what an unlinked technician is pinned
        // to, so a stub handing out a different id each time it is read would make any assertion
        // about that pinning impossible to write - and would quietly pass a broken implementation.
        private readonly Guid _org = Guid.NewGuid();
        public Guid UserIdValue { get; } = Guid.NewGuid();

        public bool IsAuthenticated => true;
        public string? Subject => "sub";
        public string? Email => "t@test";
        public string? DisplayName => "Tech";
        public Guid? OrganizationId => _org;
        public Guid? UserId => UserIdValue;
        public string? TechnicianExternalId => tech;
        public IReadOnlySet<string> Permissions => perms.ToHashSet();
        public bool HasPermission(string permissionKey) => perms.Contains(permissionKey);
    }

    [Fact]
    public async Task A_technician_asking_for_a_colleague_is_pinned_to_themselves()
    {
        var metrics = new CapturingMetrics();
        var user = new ICurrentUserStub("tech-me", Permissions.ProductivityViewOwn);

        await Controller(metrics, user).Technician(
            new DashboardController.DashboardQuery { Technician = "tech-someone-else" }, default);

        metrics.Captured!.TechnicianExternalId.Should().Be("tech-me");
    }

    [Fact]
    public async Task A_technician_asking_for_everyone_is_pinned_to_themselves()
    {
        // The dangerous case: omitting the filter entirely used to mean "no restriction",
        // returning org-wide figures.
        var metrics = new CapturingMetrics();
        var user = new ICurrentUserStub("tech-me", Permissions.ProductivityViewOwn);

        await Controller(metrics, user).Technician(new DashboardController.DashboardQuery(), default);

        metrics.Captured!.TechnicianExternalId.Should().Be("tech-me");
    }

    [Fact]
    public async Task A_manager_with_the_team_permission_may_still_ask_for_a_named_technician()
    {
        var metrics = new CapturingMetrics();
        var user = new ICurrentUserStub("mgr", Permissions.ProductivityViewOwn, Permissions.ProductivityViewTeam);

        await Controller(metrics, user).Technician(
            new DashboardController.DashboardQuery { Technician = "tech-someone-else" }, default);

        metrics.Captured!.TechnicianExternalId.Should().Be("tech-someone-else");
    }

    [Fact]
    public async Task An_account_with_no_PSA_link_is_pinned_to_its_own_portal_identity()
    {
        // This case used to be REFUSED outright, on the reasoning that an account with no
        // technician id had nothing to clamp to. That was right about the danger and wrong about
        // the remedy: a portal-only technician has an identity, it is just not the provider's, and
        // refusing them told the larger half of a desk that their own work does not count.
        //
        // The property that mattered still holds, and is what this asserts: the filter is never
        // left open. Unclamped means the whole organization's figures, which is the actual leak.
        var metrics = new CapturingMetrics();
        var user = new ICurrentUserStub(null, Permissions.ProductivityViewOwn);

        await Controller(metrics, user).Technician(new DashboardController.DashboardQuery(), default);

        metrics.Captured!.AppUserId.Should().Be(user.UserIdValue);
        metrics.Captured.TechnicianExternalId.Should().BeNull("the two identities must not both be set, or they AND together to nothing");
    }

    [Fact]
    public async Task An_account_asking_for_a_colleagues_portal_figures_is_pinned_to_itself()
    {
        // The same clamp as the PSA-side one above, on the new axis. Without it, passing another
        // person's user id in the query string would read their numbers.
        var metrics = new CapturingMetrics();
        var user = new ICurrentUserStub(null, Permissions.ProductivityViewOwn);

        await Controller(metrics, user).Technician(
            new DashboardController.DashboardQuery { AppUserId = Guid.NewGuid() }, default);

        metrics.Captured!.AppUserId.Should().Be(user.UserIdValue);
    }

    [Fact]
    public async Task A_sign_in_with_no_portal_user_at_all_is_still_refused()
    {
        // Fail closed is preserved for the case that genuinely has no identity: not linked to the
        // PSA and not resolved to a portal row either. There is nothing to clamp to, and an open
        // filter would hand back the organization.
        var metrics = new CapturingMetrics();
        var user = new NoIdentityStub(Permissions.ProductivityViewOwn);

        var act = () => Controller(metrics, user).Technician(new DashboardController.DashboardQuery(), default);

        await act.Should().ThrowAsync<ForbiddenException>();
        metrics.Captured.Should().BeNull("the service must not be reached at all");
    }

    private sealed class NoIdentityStub(params string[] perms) : Desk.Application.Abstractions.ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? Subject => "sub";
        public string? Email => "t@test";
        public string? DisplayName => "Tech";
        public Guid? OrganizationId => null;
        public Guid? UserId => null;
        public string? TechnicianExternalId => null;
        public IReadOnlySet<string> Permissions => perms.ToHashSet();
        public bool HasPermission(string permissionKey) => perms.Contains(permissionKey);
    }
}
