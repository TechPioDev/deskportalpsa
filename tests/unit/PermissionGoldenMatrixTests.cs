using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The safety net for the Phase 2 scoped-permission refactor.
///
/// Written and passing BEFORE any enforcement code existed, and pinned to the exact behavior the
/// live system had at that moment. Its entire job is to fail loudly if the refactor changes who can
/// do what — silently altering a live tenant's access is the failure mode this whole phase is built
/// to avoid, and a diff here is the only cheap way to catch it.
///
/// If a role's permissions are deliberately changed later, this matrix is meant to be updated in
/// the same commit as that decision, so the change is visible in review rather than incidental.
/// </summary>
public class PermissionGoldenMatrixTests
{
    /// <summary>Exactly what each built-in role granted before scoped permissions were introduced.</summary>
    private static readonly Dictionary<RoleType, string[]> Golden = new()
    {
        [RoleType.PlatformSuperAdministrator] =
        [
            "platform.organizations.manage", "platform.health.view", "platform.settings.manage",
            "org.manage", "connections.manage", "connections.view", "mappings.manage", "mappings.view",
            "users.manage", "roles.manage", "clientusers.manage",
            "tickets.view.all", "tickets.view.assigned", "tickets.view.company", "tickets.view.own",
            "tickets.create", "tickets.note.public.add", "tickets.time.log", "tickets.update",
            "reports.view", "productivity.team.view", "productivity.own.view",
            "integration.health.view", "jobs.manage", "audit.view", "security.config.view", "enquiries.view",
            // Added with internal boards (Sep 2026): deciding what boards exist is a lead's call.
            "boards.manage",
            // Added with the workforce module (Oct 2026): schedules and skills; then capacity
            // exceptions (time away, extra availability).
            "schedule.view", "workforce.manage", "availability.manage",
        ],
        [RoleType.MspAdministrator] =
        [
            "org.manage", "connections.manage", "connections.view", "mappings.manage", "mappings.view",
            "users.manage", "roles.manage", "clientusers.manage", "tickets.view.all", "tickets.create",
            "tickets.note.public.add", "tickets.time.log", "tickets.update", "reports.view",
            "productivity.team.view", "integration.health.view", "jobs.manage", "audit.view",
            "security.config.view", "enquiries.view", "boards.manage",
            // Workforce module (Oct 2026): administrators see and set up schedules and skills, and
            // record anyone's time away.
            "schedule.view", "workforce.manage", "availability.manage",
        ],
        [RoleType.Manager] =
        [
            "connections.view", "mappings.view", "tickets.view.all", "tickets.time.log",
            "tickets.update", "reports.view", "productivity.team.view", "integration.health.view",
            "enquiries.view", "boards.manage",
            // Workforce module (Oct 2026): managers see everyone's schedule, skills and capacity and
            // record their people's time away; they don't set schedules or skills up.
            "schedule.view", "availability.manage",
        ],
        [RoleType.Technician] =
        [
            // tickets.create added deliberately on 29 Sep 2026 (owner's call): technicians raise
            // tickets on the team's own boards.
            "tickets.view.assigned", "tickets.create", "tickets.note.public.add", "tickets.time.log",
            "tickets.update", "productivity.own.view",
            // Workforce module (Oct 2026): their own schedule, skills and capacity only (scope Own,
            // below). Deliberately NOT availability.manage: recording time away is a manager's job
            // unless an administrator gives the role that claim.
            "schedule.view",
        ],
        [RoleType.ClientAdministrator] =
        [
            "tickets.view.company", "tickets.create", "tickets.note.public.add",
            "clientusers.manage", "reports.view",
        ],
        [RoleType.ClientUser] = ["tickets.view.own", "tickets.create", "tickets.note.public.add"],
        [RoleType.Auditor] = ["audit.view", "security.config.view", "integration.health.view"],
    };

    /// <summary>
    /// The scope each role/permission pair carries on day one of enforcement. These are chosen to
    /// reproduce OBSERVED behavior, not intended behavior — notably a Technician's edit/note/time
    /// permissions are All, because before this phase nothing row-filtered them at all. Tightening
    /// them is a deliberate admin action in a later phase, not a side effect of shipping this one.
    /// </summary>
    private static readonly Dictionary<(RoleType, string), PermissionScope> GoldenScopes = new()
    {
        [(RoleType.Technician, "tickets.view.assigned")] = PermissionScope.Assigned,
        [(RoleType.Technician, "productivity.own.view")] = PermissionScope.Own,
        [(RoleType.Technician, "schedule.view")] = PermissionScope.Own,
        [(RoleType.ClientUser, "tickets.view.own")] = PermissionScope.Own,
        [(RoleType.ClientAdministrator, "tickets.view.company")] = PermissionScope.Selected,
        [(RoleType.PlatformSuperAdministrator, "tickets.view.assigned")] = PermissionScope.Assigned,
        [(RoleType.PlatformSuperAdministrator, "tickets.view.own")] = PermissionScope.Own,
        [(RoleType.PlatformSuperAdministrator, "tickets.view.company")] = PermissionScope.Selected,
        // The super-admin holds every key, so it also holds the own-scoped productivity one. Its
        // reach here is irrelevant in practice (it holds the team-wide key at All too), but it must
        // still match the scope its own key name asserts rather than being silently widened.
        [(RoleType.PlatformSuperAdministrator, "productivity.own.view")] = PermissionScope.Own,
    };

    [Theory]
    [InlineData(RoleType.PlatformSuperAdministrator)]
    [InlineData(RoleType.MspAdministrator)]
    [InlineData(RoleType.Manager)]
    [InlineData(RoleType.Technician)]
    [InlineData(RoleType.ClientAdministrator)]
    [InlineData(RoleType.ClientUser)]
    [InlineData(RoleType.Auditor)]
    public void Role_grants_exactly_the_permissions_it_granted_before_scoping(RoleType role)
    {
        var actual = Permissions.ForRole(role).Select(p => p.Key).ToArray();

        actual.Should().BeEquivalentTo(Golden[role],
            $"the set of permissions {role} grants must not change as a side effect of adding scope");
    }

    [Theory]
    [InlineData(RoleType.PlatformSuperAdministrator)]
    [InlineData(RoleType.MspAdministrator)]
    [InlineData(RoleType.Manager)]
    [InlineData(RoleType.Technician)]
    [InlineData(RoleType.ClientAdministrator)]
    [InlineData(RoleType.ClientUser)]
    [InlineData(RoleType.Auditor)]
    public void Every_permission_carries_the_day_one_scope(RoleType role)
    {
        foreach (var (key, scope) in Permissions.ForRole(role))
        {
            var expected = GoldenScopes.GetValueOrDefault((role, key), PermissionScope.All);
            scope.Should().Be(expected,
                $"{role}/{key} must keep its day-one reach — anything narrower silently removes access from live users");
        }
    }

    [Fact]
    public void No_permission_key_escapes_the_catalogue()
    {
        // A key granted by a role but missing from the catalogue would have no declared scope
        // support, so an admin UI could offer a scope the engine then cannot evaluate.
        foreach (var role in Enum.GetValues<RoleType>())
            foreach (var (key, _) in Permissions.ForRole(role))
                PermissionCatalog.TryGet(key, out _).Should().BeTrue($"'{key}' is granted by {role} but not declared in PermissionCatalog");
    }

    [Fact]
    public void Catalogue_covers_every_declared_permission_constant()
    {
        foreach (var key in Permissions.All)
            PermissionCatalog.TryGet(key, out _).Should().BeTrue($"'{key}' exists in Permissions but not in PermissionCatalog");
    }

    [Fact]
    public void Every_role_scope_is_one_the_catalogue_says_is_legal()
    {
        foreach (var role in Enum.GetValues<RoleType>())
        {
            foreach (var (key, scope) in Permissions.ForRole(role))
            {
                PermissionCatalog.TryGet(key, out var def).Should().BeTrue();
                def!.SupportedScopes.Should().Contain(scope,
                    $"{role}/{key} is seeded with a scope the catalogue does not allow for it");
            }
        }
    }
}
