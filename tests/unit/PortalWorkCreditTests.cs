using Desk.Application.Analytics;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A desk with ONE PSA login and a team working in the portal. Autotask holds every client ticket
/// under "TechPio Support"; Arjun and Ravi do the work here. Each is credited with their own work -
/// the ticket they resolved, the hours they logged - and the one login, once linked to Ravi, is Ravi.
/// </summary>
public class PortalWorkCreditTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private const string SupportLogin = "29682887";

    private static DateTimeOffset D(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(Desk.Infrastructure.Persistence.DeskDbContext Db, TechnicianMetricsService Svc, Guid Arjun, Guid Ravi);

    private static async Task<Fixture> SetupAsync(bool linkSupportToRavi = false)
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "m",
        });
        var arjun = new AppUser { MspOrganizationId = Org, Email = "arjun@techpio.com", DisplayName = "Arjun" };
        var ravi = new AppUser { MspOrganizationId = Org, Email = "ravi@techpio.com", DisplayName = "Ravi" };
        db.AppUsers.AddRange(arjun, ravi);
        if (linkSupportToRavi)
            db.UserPsaIdentities.Add(new UserPsaIdentity
            {
                MspOrganizationId = Org, AppUserId = ravi.Id, PsaConnectionId = Conn,
                ExternalTechnicianId = SupportLogin, ExternalTechnicianName = "TechPio Support",
            });
        await db.SaveChangesAsync();
        return new Fixture(db, new TechnicianMetricsService(db, new ProductivityScorer(), new TestClock(Now)), arjun.Id, ravi.Id);
    }

    /// <summary>A client ticket Autotask assigns to the one login.</summary>
    private static Ticket ClientTicket(int resolvedDay, Guid? resolvedBy = null, Guid? workedBy = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = Conn, Provider = ProviderType.AutotaskPsa, Origin = TicketOrigin.Psa,
        RequesterName = "R", RequesterEmail = "r@x", Title = "Outlook not opening", PortalStatus = "RESOLVED", PortalPriority = "NORMAL",
        AssignedTechnicianExternalId = SupportLogin, AssignedTechnicianName = "TechPio Support",
        AssignedAppUserId = workedBy, ResolvedByAppUserId = resolvedBy,
        CreatedAt = D(1), PsaCreatedAt = D(1), ResolvedAt = D(resolvedDay),
    };

    private static TicketTimeEntry Hour(Guid ticketId, Guid? loggedBy, decimal hours) => new()
    {
        // Portal time reaches Autotask under the one login, so it carries that login as well.
        MspOrganizationId = Org, TicketId = ticketId, AppUserId = loggedBy, TechnicianExternalId = SupportLogin,
        Hours = hours, Billable = true, EntryDate = D(2), SyncStatus = TimeEntrySyncStatus.Synced,
    };

    [Fact]
    public async Task Whoever_resolved_it_in_the_portal_gets_the_credit_not_the_PSA_login()
    {
        var f = await SetupAsync();
        var t = ClientTicket(resolvedDay: 2, resolvedBy: f.Arjun);
        f.Db.Tickets.Add(t);
        await f.Db.SaveChangesAsync();
        f.Db.TicketTimeEntries.Add(Hour(t.Id, f.Arjun, 1m));
        await f.Db.SaveChangesAsync();

        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);
        team.Should().ContainSingle().Which.Should().BeEquivalentTo(new { TechnicianName = "Arjun", Resolved = 1 });

        var days = await f.Svc.DailyAsync(new MetricsFilter());
        days.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "Arjun", Hours = 1m, Resolved = 1 });
    }

    [Fact]
    public async Task Whoever_is_working_it_in_the_portal_is_credited_when_it_was_closed_in_the_PSA()
    {
        var f = await SetupAsync();
        f.Db.Tickets.Add(ClientTicket(resolvedDay: 2, workedBy: f.Arjun));
        await f.Db.SaveChangesAsync();

        (await f.Svc.ForTechnicianAsync(new MetricsFilter { AppUserId = f.Arjun }, ProductivityWeights.Default)).Resolved.Should().Be(1);
        (await f.Svc.ForTechnicianAsync(new MetricsFilter { TechnicianExternalId = SupportLogin }, ProductivityWeights.Default))
            .Resolved.Should().Be(0, "the login gets only what nobody here took on");
    }

    [Fact]
    public async Task The_one_linked_login_is_its_person_and_never_takes_a_colleagues_work()
    {
        var f = await SetupAsync(linkSupportToRavi: true);
        var arjuns = ClientTicket(resolvedDay: 2, resolvedBy: f.Arjun);
        var inAutotask = ClientTicket(resolvedDay: 3); // worked and closed directly in Autotask
        f.Db.Tickets.AddRange(arjuns, inAutotask);
        await f.Db.SaveChangesAsync();
        f.Db.TicketTimeEntries.AddRange(Hour(arjuns.Id, f.Arjun, 1m), Hour(inAutotask.Id, loggedBy: null, 2m));
        await f.Db.SaveChangesAsync();

        // One row each; no separate "TechPio Support".
        var team = await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default);
        team.Select(r => (r.TechnicianName, r.Resolved)).Should().BeEquivalentTo([("Arjun", 1), ("Ravi", 1)]);

        // Ravi's own view, as the API pins it (both identities): the Autotask work, none of Arjun's.
        var own = new MetricsFilter { AppUserId = f.Ravi, TechnicianExternalId = SupportLogin, EitherIdentity = true };
        (await f.Svc.ForTechnicianAsync(own, ProductivityWeights.Default)).Resolved.Should().Be(1);
        var ravisDays = await f.Svc.DailyAsync(own);
        (ravisDays.Sum(d => d.Hours), ravisDays.Sum(d => d.Resolved)).Should().Be((2m, 1),
            "Arjun's hour reached Autotask under the same login, but it is Arjun's");

        // A manager opening Ravi from the team table: the same answer.
        var managerView = await f.Svc.DailyAsync(new MetricsFilter { AppUserId = f.Ravi });
        managerView.Sum(d => d.Hours).Should().Be(2m);
        (await f.Svc.DailyAsync(new MetricsFilter())).Select(d => d.Name).Should().OnlyContain(n => n == "Arjun" || n == "Ravi");
    }

    [Fact]
    public async Task A_name_in_the_ticket_list_opens_the_tickets_credited_to_them()
    {
        // The list's people filter must agree with Productivity: the ticket Arjun resolved is his,
        // even though Autotask still shows it under the login linked to Ravi.
        var f = await SetupAsync(linkSupportToRavi: true);
        f.Db.Tickets.AddRange(ClientTicket(resolvedDay: 2, resolvedBy: f.Arjun), ClientTicket(resolvedDay: 3));
        await f.Db.SaveChangesAsync();
        var reads = new TicketReadService(f.Db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: f.Ravi));

        (await reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(f.Ravi, null, null)))).Total.Should().Be(1);
        (await reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(f.Arjun, null, null)))).Total.Should().Be(1);
        (await reads.FacetsAsync()).People.Select(p => p.Name).Should().BeEquivalentTo(["Arjun", "Ravi"]);
    }

    [Fact]
    public async Task A_link_to_the_integration_account_hands_nobody_the_teams_work()
    {
        // Production has exactly this: a portal user linked to the Autotask login the integration
        // writes as. That login carries everyone's portal work, so it must never become one person's.
        var f = await SetupAsync();
        f.Db.PsaConnections.Single().DefaultTimeEntryResourceId = "29682885";
        f.Db.UserPsaIdentities.Add(new UserPsaIdentity
            { MspOrganizationId = Org, AppUserId = f.Ravi, PsaConnectionId = Conn, ExternalTechnicianId = "29682885" });
        var t = ClientTicket(resolvedDay: 2);
        t.AssignedTechnicianExternalId = "29682885";
        f.Db.Tickets.Add(t);
        await f.Db.SaveChangesAsync();
        var hour = Hour(t.Id, loggedBy: null, 1m);
        hour.TechnicianExternalId = "29682885";
        f.Db.TicketTimeEntries.Add(hour);
        await f.Db.SaveChangesAsync();

        (await f.Svc.TeamAsync(new MetricsFilter(), ProductivityWeights.Default)).Should().BeEmpty();
        var ravi = new MetricsFilter { AppUserId = f.Ravi };
        (await f.Svc.ForTechnicianAsync(ravi, ProductivityWeights.Default)).Resolved.Should().Be(0);
        (await f.Svc.DailyAsync(ravi)).Sum(d => d.Hours).Should().Be(0m);
    }

    [Fact]
    public async Task Resolving_records_who_did_it_and_a_reopen_takes_it_back()
    {
        var h = AdminHarness.Create(Org);
        var arjun = new AppUser { MspOrganizationId = Org, DisplayName = "Arjun", Email = "a@techpio.test", IsActive = true };
        var lead = new AppUser { MspOrganizationId = Org, DisplayName = "Lead", Email = "l@techpio.test", IsActive = true };
        var board = new Board { MspOrganizationId = Org, Name = "Ops", Key = "OPS", Kind = BoardKind.Internal };
        h.Db.AddRange(arjun, lead, board);
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "OPS-1", Title = "Patch the NAS",
            RequesterName = "Arjun", RequesterEmail = "a@x.test", PortalStatus = "IN_PROGRESS", PortalPriority = "NORMAL",
        };
        h.Db.Tickets.Add(t);
        await h.Db.SaveChangesAsync();
        TicketStatusWriter As(Guid who) => new(h.Db, null!, null!, null, new TestCurrentUser(Org, userId: who));

        await As(arjun.Id).SetAsync(t, "RESOLVED", null, default);
        t.ResolvedByAppUserId.Should().Be(arjun.Id);

        // The lead tidying it to Closed does not take the credit.
        await As(lead.Id).SetAsync(t, "CLOSED", null, default);
        t.ResolvedByAppUserId.Should().Be(arjun.Id);

        await As(lead.Id).SetAsync(t, "IN_PROGRESS", null, default);
        t.ResolvedByAppUserId.Should().BeNull();
    }
}
