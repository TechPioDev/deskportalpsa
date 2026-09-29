using Desk.Api.Auth;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A Standard Technician holds tickets.view.assigned, not tickets.view.all - by design, the key names
/// carry their scope. Staff screens ask for view.all. Before this, that mismatch meant a technician
/// was sent down the CLIENT path and refused the ticket list outright. They must be treated as staff,
/// and see exactly their own tickets - no more.
/// </summary>
public class TechnicianAccessTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record Kit(AdminHarness H, TicketScopeQuery Scope, Guid Tech, Guid Admin, Guid Mine, Guid Theirs, Guid Unclaimed);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        Role Built(RoleType type) => new()
        {
            Name = type.ToString(), BuiltInType = type, IsSystemRole = true,
            Permissions = [.. Permissions.ForRole(type).Select(p => new RolePermission { PermissionKey = p.Key, Scope = p.Scope })],
        };
        var technician = Built(RoleType.Technician);
        var administrator = Built(RoleType.MspAdministrator);
        var tech = new AppUser { MspOrganizationId = Org, DisplayName = "Arjun Nair", Email = "arjun@techpio.test", IsActive = true };
        var admin = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "dalbir@techpio.test", IsActive = true };
        h.Db.AddRange(technician, administrator, tech, admin);
        h.Db.UserRoles.AddRange(
            new UserRole { AppUserId = tech.Id, RoleId = technician.Id },
            new UserRole { AppUserId = admin.Id, RoleId = administrator.Id });

        Ticket T(string title, Guid? holder) => new()
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, ExternalTicketId = title, RequesterName = "Priya",
            RequesterEmail = "p@acme.test", Title = title, PortalStatus = "IN_PROGRESS", AssignedAppUserId = holder,
        };
        var mine = T("Arjun's printer", tech.Id);
        var theirs = T("Someone else's server", admin.Id);
        var unclaimed = T("Nobody's yet", null);
        h.Db.Tickets.AddRange(mine, theirs, unclaimed);
        await h.Db.SaveChangesAsync();

        return new Kit(h, new TicketScopeQuery(h.Db, new EffectivePermissionService(h.Db)), tech.Id, admin.Id, mine.Id, theirs.Id, unclaimed.Id);
    }

    [Fact]
    public async Task A_technician_asked_for_as_staff_sees_their_own_tickets_and_no_one_elses()
    {
        var k = await BuildAsync();

        var visible = await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Tech, Permissions.TicketsViewAll)).Select(t => t.Id).ToListAsync();

        // Before: nothing at all. Now: their ticket - not a colleague's, and not the whole unclaimed
        // queue (that needs board grants, as it always did).
        visible.Should().Equal(k.Mine);
        (await k.Scope.FindAsync(k.H.Db.Tickets, k.Theirs, k.Tech, Permissions.TicketsViewAll)).Should().BeNull();
    }

    [Fact]
    public async Task An_administrator_still_sees_everything()
    {
        var k = await BuildAsync();

        var visible = await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Admin, Permissions.TicketsViewAll)).CountAsync();

        visible.Should().Be(3);
    }

    [Fact]
    public async Task A_technician_denied_assigned_tickets_by_an_override_sees_nothing()
    {
        var k = await BuildAsync();
        k.H.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        { MspOrganizationId = Org, AppUserId = k.Tech, PermissionKey = Permissions.TicketsViewAssigned, Effect = PermissionEffect.Deny });
        await k.H.Db.SaveChangesAsync();

        (await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Tech, Permissions.TicketsViewAll)).CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(Permissions.TicketsAddPublicNote)]
    [InlineData(Permissions.TicketsLogTime)]
    [InlineData(Permissions.TicketsUpdate)]
    public async Task A_technician_acts_only_on_tickets_they_can_see(string action)
    {
        var k = await BuildAsync();

        // The role grants these at All; sight is what narrows them. A colleague's ticket is a 404 to
        // open, so it must be one to write on too.
        (await k.Scope.FindAsync(k.H.Db.Tickets, k.Mine, k.Tech, action)).Should().NotBeNull();
        (await k.Scope.FindAsync(k.H.Db.Tickets, k.Theirs, k.Tech, action)).Should().BeNull();
        (await k.Scope.FindAsync(k.H.Db.Tickets, k.Unclaimed, k.Tech, action)).Should().BeNull();
    }

    [Fact]
    public async Task An_administrator_still_acts_on_any_ticket()
    {
        var k = await BuildAsync();

        (await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Admin, Permissions.TicketsUpdate)).CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_team_board_is_shared_by_membership_not_by_assignment()
    {
        var k = await BuildAsync();
        var open = new Board { MspOrganizationId = Org, Name = "Office IT", Key = "OIT" };
        var closed = new Board { MspOrganizationId = Org, Name = "Leadership", Key = "LDR" };
        k.H.Db.Boards.AddRange(open, closed);
        k.H.Db.BoardMembers.Add(new BoardMember { MspOrganizationId = Org, BoardId = closed.Id, AppUserId = k.Admin });
        Ticket OnBoard(Board board, string title, Guid? holder) => new()
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Title = title,
            RequesterName = "Dalbir", RequesterEmail = "dalbir@techpio.test", PortalStatus = "NEW",
            CreatedByUserId = k.Admin, AssignedAppUserId = holder,
        };
        var colleagues = OnBoard(open, "Patch the file server", k.Admin);
        var unclaimed = OnBoard(open, "Printer jam, floor 2", null);
        var leadership = OnBoard(closed, "Salary review", null);
        k.H.Db.Tickets.AddRange(colleagues, unclaimed, leadership);
        await k.H.Db.SaveChangesAsync();

        var visible = await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Tech, Permissions.TicketsViewAll)).Select(t => t.Id).ToListAsync();

        // The open board is the whole team's: its tickets show whoever holds them, so the unclaimed
        // one can be taken. A board with named members stays theirs. PSA tickets are still narrowed
        // to the technician's own.
        visible.Should().BeEquivalentTo([k.Mine, colleagues.Id, unclaimed.Id]);
        (await k.Scope.FindAsync(k.H.Db.Tickets, unclaimed.Id, k.Tech, Permissions.TicketsUpdate)).Should().NotBeNull();
        (await k.Scope.FindAsync(k.H.Db.Tickets, leadership.Id, k.Tech, Permissions.TicketsUpdate)).Should().BeNull();
    }

    [Theory]
    [InlineData(Permissions.TicketsViewAll, true)]
    [InlineData(Permissions.TicketsViewAssigned, true)]
    [InlineData(Permissions.TicketsViewOwnCompany, false)]
    [InlineData(Permissions.TicketsViewOwn, false)]
    public void Staff_means_either_staff_view_and_a_client_view_is_never_staff(string held, bool staff)
    {
        new TestCurrentUser(Org, permissions: new HashSet<string> { held }, userId: Guid.NewGuid())
            .SeesStaffTickets().Should().Be(staff);
    }

    [Fact]
    public void A_technician_sees_their_own_tickets_and_may_raise_team_tickets()
    {
        // Sight stays narrow; view.all is never granted. Create was added on the owner's call so a
        // technician can log work on the team's own boards.
        Permissions.ForRole(RoleType.Technician).Should().NotContain(p => p.Key == Permissions.TicketsViewAll);
        Permissions.ForRole(RoleType.Technician).Should().Contain(p => p.Key == Permissions.TicketsViewAssigned && p.Scope == PermissionScope.Assigned);
        Permissions.ForRole(RoleType.Technician).Should().Contain(p => p.Key == Permissions.TicketsCreate);
    }

    [Fact]
    public async Task A_ticket_a_technician_raised_for_nobody_stays_in_their_view()
    {
        var k = await BuildAsync();
        var raised = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, Title = "Office Wi-Fi drops at 3pm",
            RequesterName = "Arjun", RequesterEmail = "arjun@techpio.test", PortalStatus = "NEW",
            CreatedByUserId = k.Tech, AssignedAppUserId = null,
        };
        k.H.Db.Tickets.Add(raised);
        await k.H.Db.SaveChangesAsync();

        var visible = await (await k.Scope.VisibleAsync(k.H.Db.Tickets, k.Tech, Permissions.TicketsViewAll)).Select(t => t.Id).ToListAsync();

        // Their own raised ticket joins their assigned one; the admin's and the other unclaimed one stay hidden.
        visible.Should().BeEquivalentTo([k.Mine, raised.Id]);
        (await k.Scope.FindAsync(k.H.Db.Tickets, raised.Id, k.Tech, Permissions.TicketsAddPublicNote)).Should().NotBeNull();
    }
}
