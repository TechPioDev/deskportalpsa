using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Proves the actual row-level filtering, not just the scope value it's based on — this is the
/// layer that closes the gap TicketsController.Detail had (a claim naming "assigned" with nothing
/// checking it). Every test seeds real Ticket rows and asserts which ones a caller can and cannot
/// reach.
/// </summary>
public class TicketScopeQueryTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static DeskDbContext NewDb()
    {
        var tenant = new TenantContext();
        tenant.SetPlatformScope();
        var options = new DbContextOptionsBuilder<DeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeskDbContext(options, tenant, new TestClock());
    }

    private static Ticket NewTicket(string title, string? assignedTo = null, Guid? connectionId = null,
        string? board = null, Guid? assignedUser = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = connectionId ?? Guid.NewGuid(), Provider = ProviderType.ConnectWisePsa,
        ClientCompanyId = Guid.NewGuid(), RequesterName = "r", RequesterEmail = "r@test",
        Title = title, PortalStatus = "NEW", PortalPriority = "NORMAL",
        AssignedTechnicianExternalId = assignedTo, QueueOrBoard = board, AssignedAppUserId = assignedUser,
    };

    private static async Task<Guid> SeedUserAsync(DeskDbContext db, string? externalTechnicianId = null)
    {
        var user = new Desk.Domain.Identity.AppUser
        {
            MspOrganizationId = Org, Email = $"{Guid.NewGuid()}@test", DisplayName = "T",
            ExternalTechnicianId = externalTechnicianId,
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task GrantAsync(DeskDbContext db, Guid userId, string key, PermissionScope scope)
    {
        var role = new Role { Name = "R-" + Guid.NewGuid(), MspOrganizationId = Org };
        role.Permissions.Add(new RolePermission { PermissionKey = key, Scope = scope });
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.UserRoles.Add(new UserRole { AppUserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    private static TicketScopeQuery Query(DeskDbContext db) => new(db, new EffectivePermissionService(db));

    [Fact]
    public async Task A_portal_only_technician_sees_the_tickets_assigned_to_them_here()
    {
        // The case the whole change exists for. This user has no PSA resource, and before it they
        // saw NOTHING - the scope returned Where(_ => false) on the reasoning that nothing could be
        // assigned to someone the PSA has never heard of. Forty technicians would have signed in to
        // an empty list.
        var db = NewDb();
        var meId = await SeedUserAsync(db);           // no external technician id
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);

        db.Tickets.AddRange(
            NewTicket("mine", assignedUser: meId),
            NewTicket("a colleague's", assignedUser: Guid.NewGuid()),
            NewTicket("the PSA's", assignedTo: "tech-someone-else"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.Select(t => t.Title).ToListAsync()).Should().BeEquivalentTo(["mine"]);
    }

    [Fact]
    public async Task A_linked_technician_sees_both_identities_worth_of_work()
    {
        // Someone who exists in both places must not lose either half. Picking one identity would
        // have quietly broken whichever group was not chosen.
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);

        db.Tickets.AddRange(
            NewTicket("assigned in the PSA", assignedTo: "tech-me"),
            NewTicket("assigned in the portal", assignedUser: meId),
            NewTicket("neither", assignedTo: "tech-other"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.Select(t => t.Title).ToListAsync())
            .Should().BeEquivalentTo(["assigned in the PSA", "assigned in the portal"]);
    }

    [Fact]
    public async Task Without_board_grants_the_unclaimed_queue_stays_hidden()
    {
        // The firehose guard. A technician with no board access has no queue to pick up from, so
        // "everything nobody owns, across the whole tenant" is not a useful list - it is every
        // ticket in the desk shown to everyone.
        var db = NewDb();
        var meId = await SeedUserAsync(db);
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);

        db.Tickets.AddRange(NewTicket("mine", assignedUser: meId), NewTicket("nobody's"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.Select(t => t.Title).ToListAsync()).Should().BeEquivalentTo(["mine"]);
    }

    [Fact]
    public async Task A_ticket_the_PSA_gave_to_the_integration_is_not_unclaimed()
    {
        // Work arriving under the API user's identity carries an external id, so it is owned - by
        // whoever the portal gave it to. Treating it as unclaimed would put one job in every
        // technician's queue and invite two people to start it.
        var db = NewDb();
        var meId = await SeedUserAsync(db);
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);

        db.Tickets.Add(NewTicket("held by the integration", assignedTo: "api-user-id"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Assigned_scope_shows_only_the_callers_own_tickets()
    {
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);

        var mine = NewTicket("mine", assignedTo: "tech-me");
        var theirs = NewTicket("theirs", assignedTo: "tech-someone-else");
        db.Tickets.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);
        var titles = await visible.Select(t => t.Title).ToListAsync();

        titles.Should().BeEquivalentTo(["mine"]);
    }

    [Fact]
    public async Task Assigned_scope_by_id_refuses_a_colleagues_ticket()
    {
        // The exact IDOR this closes: fetching by GUID must be constrained the same way listing is.
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);
        var theirs = NewTicket("theirs", assignedTo: "tech-someone-else");
        db.Tickets.Add(theirs);
        await db.SaveChangesAsync();

        var found = await Query(db).FindAsync(db.Tickets, theirs.Id, meId, Permissions.TicketsUpdate);

        found.Should().BeNull();
    }

    [Fact]
    public async Task An_account_not_linked_to_a_technician_sees_nothing_under_assigned_scope()
    {
        var db = NewDb();
        var meId = await SeedUserAsync(db, externalTechnicianId: null);
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Assigned);
        db.Tickets.Add(NewTicket("unassigned-owner"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Department_scope_shows_teammates_tickets_and_hides_other_departments()
    {
        var db = NewDb();
        var deptA = new Department { MspOrganizationId = Org, Name = "IT Support" };
        var deptB = new Department { MspOrganizationId = Org, Name = "Billing" };
        db.Departments.AddRange(deptA, deptB);
        await db.SaveChangesAsync();

        var meId = await SeedUserAsync(db, "tech-me");
        var teammateId = await SeedUserAsync(db, "tech-teammate");
        var otherDeptId = await SeedUserAsync(db, "tech-other-dept");
        db.UserDepartments.AddRange(
            new UserDepartment { MspOrganizationId = Org, AppUserId = meId, DepartmentId = deptA.Id, IsPrimary = true },
            new UserDepartment { MspOrganizationId = Org, AppUserId = teammateId, DepartmentId = deptA.Id, IsPrimary = true },
            new UserDepartment { MspOrganizationId = Org, AppUserId = otherDeptId, DepartmentId = deptB.Id, IsPrimary = true });
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Department);

        var mine = NewTicket("mine", assignedTo: "tech-me");
        var teammates = NewTicket("teammates", assignedTo: "tech-teammate");
        var otherDept = NewTicket("other-dept", assignedTo: "tech-other-dept");
        db.Tickets.AddRange(mine, teammates, otherDept);
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);
        var titles = await visible.Select(t => t.Title).ToListAsync();

        titles.Should().BeEquivalentTo(["mine", "teammates"]);
    }

    [Fact]
    public async Task Department_scope_still_shows_unassigned_tickets()
    {
        // The explicit product decision: unassigned tickets are the unclaimed queue and must not
        // disappear from department-scoped view just because they have no technician to join through.
        var db = NewDb();
        var dept = new Department { MspOrganizationId = Org, Name = "IT Support" };
        db.Departments.Add(dept);
        await db.SaveChangesAsync();
        var meId = await SeedUserAsync(db, "tech-me");
        db.UserDepartments.Add(new UserDepartment { MspOrganizationId = Org, AppUserId = meId, DepartmentId = dept.Id, IsPrimary = true });
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.Department);

        var unassigned = NewTicket("unclaimed");
        db.Tickets.Add(unassigned);
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.Select(t => t.Title).ToListAsync()).Should().BeEquivalentTo(["unclaimed"]);
    }

    [Fact]
    public async Task Board_fence_hides_tickets_on_a_board_not_granted()
    {
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.All);
        var connId = Guid.NewGuid();
        db.UserBoardAccesses.Add(new UserBoardAccess { MspOrganizationId = Org, AppUserId = meId, Mode = BoardAccessMode.Selected });
        db.UserBoardGrants.Add(new UserBoardGrant
        {
            MspOrganizationId = Org, AppUserId = meId, PsaConnectionId = connId, BoardName = "Help Desk", Actions = BoardAction.Edit,
        });

        var onGrantedBoard = NewTicket("help-desk-ticket", connectionId: connId, board: "Help Desk");
        var onOtherBoard = NewTicket("billing-ticket", connectionId: connId, board: "Billing");
        db.Tickets.AddRange(onGrantedBoard, onOtherBoard);
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);
        var titles = await visible.Select(t => t.Title).ToListAsync();

        titles.Should().BeEquivalentTo(["help-desk-ticket"]);
    }

    [Fact]
    public async Task No_board_access_row_means_all_boards_stay_visible()
    {
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        await GrantAsync(db, meId, Permissions.TicketsUpdate, PermissionScope.All);
        db.Tickets.Add(NewTicket("any-board", board: "Whatever"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.ToListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task No_grant_at_all_yields_nothing()
    {
        var db = NewDb();
        var meId = await SeedUserAsync(db, "tech-me");
        db.Tickets.Add(NewTicket("anything", assignedTo: "tech-me"));
        await db.SaveChangesAsync();

        var visible = await Query(db).VisibleAsync(db.Tickets, meId, Permissions.TicketsUpdate);

        (await visible.ToListAsync()).Should().BeEmpty();
    }
}
