using Desk.Api.Controllers;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Who the recent-activity feed serves. It backs the staff header bell, both dashboard activity
/// panels and the Notifications page - and it used to resolve every caller as a client, so every
/// technician and manager got a 403 on every page and a bell that could never show anything.
/// </summary>
public class StaffNotificationsTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();
    private static readonly DateTimeOffset Sep1 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    // Explicitly empty: TestCurrentUser grants EVERYTHING when no set is passed, which would quietly
    // turn every "no access" case below into a fully privileged staff member.
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    private sealed class Resolver(ClientAccess? access) : IClientAccessResolver
    {
        public Task<ClientAccess?> ResolveAsync(string idpSubject, CancellationToken ct = default) => Task.FromResult(access);
    }

    private static async Task<DeskDbContext> SeedAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        db.ClientCompanies.Add(new ClientCompany { Id = Acme, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" });
        db.ClientCompanies.Add(new ClientCompany { Id = Globex, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Globex", ExternalCompanyId = "2" });
        db.Tickets.AddRange(
            Ticket(Acme, "Acme printer", syncedDaysAfter: 3),
            Ticket(Globex, "Globex VPN", syncedDaysAfter: 2),
            Ticket(Acme, "Acme mailbox", syncedDaysAfter: 1));
        await db.SaveChangesAsync();
        return db;
    }

    private static Ticket Ticket(Guid company, string title, int syncedDaysAfter) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa, ClientCompanyId = company,
        ExternalTicketId = title, RequesterName = "r", RequesterEmail = "r@a.test", Title = title,
        PortalStatus = "IN_PROGRESS", PortalPriority = "NORMAL", LastSyncedAt = Sep1.AddDays(syncedDaysAfter),
    };

    private static PortalController Controller(DeskDbContext db, TestCurrentUser user, ClientAccess? access)
        => new(user, new Resolver(access), new TicketReadService(db, new NoopTicketScopeQuery(), user));

    private static IReadOnlyList<NotificationDto> Feed(IActionResult result)
        => (IReadOnlyList<NotificationDto>)((OkObjectResult)result).Value!;

    [Fact]
    public async Task Staff_with_ticket_access_get_the_activity_on_every_ticket_they_can_see()
    {
        await using var db = await SeedAsync();
        var staff = new TestCurrentUser(Org, permissions: new HashSet<string> { Permissions.TicketsViewAll }, userId: Guid.NewGuid());

        var feed = Feed(await Controller(db, staff, access: null).Notifications(default));

        feed.Select(n => n.Title).Should().Equal("Acme printer", "Globex VPN", "Acme mailbox");
    }

    [Fact]
    public async Task Staff_without_ticket_access_get_an_empty_feed_rather_than_a_refusal()
    {
        // The bell asks on every page load. A refusal is a console error on every page; an empty
        // feed is the truth - this person has no ticket activity to see.
        await using var db = await SeedAsync();
        var staff = new TestCurrentUser(Org, permissions: NoPermissions, userId: Guid.NewGuid());

        var feed = Feed(await Controller(db, staff, access: null).Notifications(default));

        feed.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_users_still_get_only_their_own_companys_activity()
    {
        await using var db = await SeedAsync();
        var client = new TestCurrentUser(Org, permissions: NoPermissions);

        var feed = Feed(await Controller(db, client, new ClientAccess(Org, Acme, Guid.NewGuid(), IsCompanyAdministrator: true))
            .Notifications(default));

        feed.Select(n => n.Title).Should().Equal("Acme printer", "Acme mailbox");
    }

    [Fact]
    public async Task A_caller_who_is_neither_staff_nor_a_client_is_refused()
    {
        await using var db = await SeedAsync();

        var act = async () => await Controller(db, new TestCurrentUser(Org, permissions: NoPermissions), access: null).Notifications(default);

        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
