using System.Security.Claims;
using Desk.Api.Auth;
using Desk.Api.Middleware;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// "View as": an administrator sees the portal as one of its users, never acts as them, and never as
/// someone they may not see. The header alone grants nothing - every request re-checks the caller.
/// </summary>
public class ViewAsTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record World(DeskDbContext Db, AppUser Admin, AppUser Manager, AppUser Tech, AppUser Outsider, ClientUser Client, ClientUser NewClient);

    private static async Task<World> WorldAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        Role R(string name, RoleType type, params string[] perms)
        {
            var role = new Role { MspOrganizationId = Org, Name = name, BuiltInType = type };
            foreach (var p in perms) role.Permissions.Add(new RolePermission { PermissionKey = p });
            db.Roles.Add(role);
            return role;
        }
        var adminRole = R("Administrator", RoleType.MspAdministrator, Permissions.UsersManage, Permissions.RolesManage, Permissions.TicketsViewAll);
        var managerRole = R("Manager", RoleType.Manager, Permissions.UsersManage, Permissions.TicketsViewAll);
        var techRole = R("Technician", RoleType.Technician, Permissions.TicketsViewAssigned, Permissions.TicketsLogTime);
        AppUser U(string name, Role role, Guid? org = null, string? subject = null)
        {
            var u = new AppUser { MspOrganizationId = org ?? Org, DisplayName = name, Email = $"{name.ToLowerInvariant()}@techpio.test", IsActive = true, IdpSubject = subject ?? $"sub-{name}", LastActiveAt = new TestClock().GetUtcNow() };
            u.Roles.Add(new UserRole { RoleId = role.Id });
            db.AppUsers.Add(u);
            return u;
        }
        var admin = U("Harpal", adminRole);
        var manager = U("Manager", managerRole);
        var tech = U("Sarabjit", techRole, subject: "sub-sarabjit");
        var outsider = U("Outsider", techRole, org: Guid.NewGuid());
        var client = new ClientUser { MspOrganizationId = Org, ClientCompanyId = Guid.NewGuid(), Email = "anu@acme.test", DisplayName = "Anu", IdpSubject = "sub-anu", IsActive = true };
        var newClient = new ClientUser { MspOrganizationId = Org, ClientCompanyId = Guid.NewGuid(), Email = "new@acme.test", DisplayName = "New Client", IdpSubject = null, IsActive = true };
        db.ClientUsers.AddRange(client, newClient);
        await db.SaveChangesAsync();
        return new World(db, admin, manager, tech, outsider, client, newClient);
    }

    private static async Task<ClaimsPrincipal> SignInAsync(World w, AppUser who, string? viewAs)
    {
        var http = new DefaultHttpContext();
        if (viewAs is not null) http.Request.Headers[ViewAs.Header] = viewAs;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, who.IdpSubject!), new Claim("name", who.DisplayName), new Claim(ClaimTypes.Email, who.Email)], "test"));
        return await new DeskClaimsTransformation(w.Db, new TestClock(), new HttpContextAccessor { HttpContext = http }).TransformAsync(principal);
    }

    private static CurrentUser As(ClaimsPrincipal p) => new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = p } });

    [Fact]
    public async Task An_administrator_sees_the_portal_as_the_technician_does()
    {
        var w = await WorldAsync();
        var me = As(await SignInAsync(w, w.Admin, $"u:{w.Tech.Id}"));

        me.Should().BeEquivalentTo(new { UserId = (Guid?)w.Tech.Id, DisplayName = "Sarabjit", Email = "sarabjit@techpio.test", Subject = "sub-sarabjit" },
            o => o.ExcludingMissingMembers());
        me.Permissions.Should().BeEquivalentTo([Permissions.TicketsViewAssigned, Permissions.TicketsLogTime],
            "their access, not the administrator's");
        (me.ViewedByUserId, me.ViewedByName, me.ViewKind).Should().Be((w.Admin.Id, "Harpal", "staff"));
    }

    [Fact]
    public async Task The_header_alone_grants_nothing()
    {
        var w = await WorldAsync();

        // A manager manages users but not roles: not an administrator.
        var manager = As(await SignInAsync(w, w.Manager, $"u:{w.Tech.Id}"));
        (manager.UserId, manager.ViewedByUserId).Should().Be((w.Manager.Id, null));

        // A technician who forges the header is still themselves.
        var tech = As(await SignInAsync(w, w.Tech, $"u:{w.Admin.Id}"));
        (tech.UserId, tech.ViewedByUserId).Should().Be((w.Tech.Id, null));
        tech.Permissions.Should().NotContain(Permissions.UsersManage);
    }

    [Theory]
    [InlineData("outsider")]
    [InlineData("self")]
    [InlineData("new-client")]
    [InlineData("u:not-a-guid")]
    public async Task Someone_the_administrator_may_not_view_is_not_viewed(string who)
    {
        var w = await WorldAsync();
        var key = who switch
        {
            "outsider" => $"u:{w.Outsider.Id}",
            "self" => $"u:{w.Admin.Id}",
            "new-client" => $"c:{w.NewClient.Id}",
            _ => who,
        };
        var me = As(await SignInAsync(w, w.Admin, key));
        (me.UserId, me.ViewedByUserId).Should().Be((w.Admin.Id, null));

        var (target, refusal) = await ViewAs.ResolveAsync(w.Db, key, w.Admin.Id, Org, callerIsPlatform: false);
        target.Should().BeNull();
        refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Viewing_as_a_client_shows_their_companys_portal()
    {
        var w = await WorldAsync();
        var me = As(await SignInAsync(w, w.Admin, $"c:{w.Client.Id}"));

        (me.UserId, me.Subject, me.OrganizationId, me.ViewKind).Should().Be((null, "sub-anu", Org, "client"));
        me.Permissions.Should().BeEquivalentTo([Permissions.TicketsCreate, Permissions.TicketsAddPublicNote]);
        // The client's own access, found the way every client endpoint finds it.
        (await new ClientAccessResolver(w.Db).ResolveAsync(me.Subject!))!.ClientUserId.Should().Be(w.Client.Id);
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("GET", false)]
    public async Task Viewing_is_read_only(string method, bool refused)
    {
        var w = await WorldAsync();
        var http = new DefaultHttpContext { User = await SignInAsync(w, w.Admin, $"u:{w.Tech.Id}") };
        http.Request.Method = method;
        var reached = false;
        var mw = new ViewAsReadOnlyMiddleware(_ => { reached = true; return Task.CompletedTask; });

        if (refused)
        {
            await mw.Invoking(m => m.InvokeAsync(http)).Should().ThrowAsync<ForbiddenException>().WithMessage("*viewing as Sarabjit*read-only*");
            reached.Should().BeFalse();
        }
        else
        {
            await mw.InvokeAsync(http);
            reached.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Changes_are_never_refused_outside_a_view()
    {
        var w = await WorldAsync();
        var http = new DefaultHttpContext { User = await SignInAsync(w, w.Admin, viewAs: null) };
        http.Request.Method = "POST";
        var reached = false;
        await new ViewAsReadOnlyMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(http);
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Anything_recorded_during_a_view_names_the_administrator()
    {
        var w = await WorldAsync();
        var me = As(await SignInAsync(w, w.Admin, $"u:{w.Tech.Id}"));
        var tenant = new TenantContext();
        tenant.SetTenant(Org);

        await new AuditWriter(w.Db, me, tenant, new TestClock()).WriteAsync("ticket.viewed", "Ticket", "t1");

        var row = await w.Db.AuditLog.IgnoreQueryFilters().SingleAsync();
        (row.ActorUserId, row.ActorDisplayName).Should().Be((w.Admin.Id.ToString(), "Harpal (viewing as Sarabjit)"));
    }
}
