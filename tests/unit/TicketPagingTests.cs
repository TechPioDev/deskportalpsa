using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The ticket list a page at a time, and the counts that replaced loading every ticket: the same rows
/// and the same numbers the browser used to work out for itself, now from the database.
/// </summary>
public class TicketPagingTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record World(AdminHarness H, TicketReadService Reads, AppUser Me, AppUser Colleague, PsaConnection Conn, ClientCompany Acme);

    private static async Task<World> WorldAsync()
    {
        var h = AdminHarness.Create(Org);
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Anika", Email = "anika@techpio.test", IsActive = true };
        var colleague = new AppUser { MspOrganizationId = Org, DisplayName = "Arjun", Email = "arjun@techpio.test", IsActive = true };
        // The integration account writes as resource 99; a ticket "held" by it is held by nobody.
        var conn = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x",
            CredentialSecretRef = "mem://x", DefaultTimeEntryResourceId = "99",
        };
        var acme = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = conn.Id, Name = "Acme Dental", ExternalCompanyId = "1" };
        h.Db.AddRange(me, colleague, conn, acme);
        await h.Db.SaveChangesAsync();
        return new World(h, new TicketReadService(h.Db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: me.Id)), me, colleague, conn, acme);
    }

    private static Ticket Psa(World w, string title, int ageDays, string status = "IN_PROGRESS", string priority = "NORMAL")
        => new()
        {
            MspOrganizationId = Org, PsaConnectionId = w.Conn.Id, Provider = ProviderType.AutotaskPsa, ExternalTicketId = title,
            ClientCompanyId = w.Acme.Id, Title = title, RequesterName = "Priya", RequesterEmail = "p@acme.test",
            PortalStatus = status, PortalPriority = priority, QueueOrBoard = "Service Desk",
            CreatedAt = w.H.Clock.GetUtcNow().AddDays(-ageDays), PsaCreatedAt = w.H.Clock.GetUtcNow().AddDays(-ageDays),
        };

    [Fact]
    public async Task A_page_is_the_list_s_own_order_with_the_whole_set_counted()
    {
        var w = await WorldAsync();
        for (var i = 1; i <= 5; i++)
        {
            var t = Psa(w, $"T{i}", ageDays: i);
            t.TimeWorkedHours = 1.25m;
            t.BillableHours = 1m;
            w.H.Db.Tickets.Add(t);
        }
        await w.H.Db.SaveChangesAsync();

        var page = await w.Reads.PageAsync(new TicketQuery(Skip: 2, Take: 2));

        // Newest first, as the list has always been; the hours cover all five, not the two on screen.
        page.Items.Select(t => t.Title).Should().Equal("T3", "T4");
        page.Should().BeEquivalentTo(new { Total = 5, Skip = 2, Take = 2, HoursWorked = 6.25m, HoursBillable = 5m });
    }

    [Fact]
    public async Task Unassigned_still_means_nobody_including_the_integration_account()
    {
        var w = await WorldAsync();
        var byAccount = Psa(w, "Held by the API user", 1);
        byAccount.AssignedTechnicianExternalId = "99";
        var byPerson = Psa(w, "Held by a technician", 1);
        byPerson.AssignedTechnicianExternalId = "123";
        var mine = Psa(w, "Held here", 1);
        mine.AssignedAppUserId = w.Me.Id;
        w.H.Db.Tickets.AddRange(byAccount, byPerson, mine, Psa(w, "Nobody's", 1));
        await w.H.Db.SaveChangesAsync();

        var page = await w.Reads.PageAsync(new TicketQuery(UnassignedOnly: true));

        // The browser treated the account as nobody; moving the filter to the server must not change that.
        page.Items.Select(t => t.Title).Should().BeEquivalentTo(["Held by the API user", "Nobody's"]);
    }

    [Fact]
    public async Task A_person_filter_finds_what_they_hold_and_what_they_logged_time_on()
    {
        var w = await WorldAsync();
        var held = Psa(w, "Anika holds", 1);
        held.AssignedAppUserId = w.Me.Id;
        var logged = Psa(w, "Anika logged", 2);
        var psaPerson = Psa(w, "Resource 123 holds", 3);
        psaPerson.AssignedTechnicianExternalId = "123";
        w.H.Db.Tickets.AddRange(held, logged, psaPerson, Psa(w, "Nobody", 4));
        w.H.Db.TicketTimeEntries.Add(new TicketTimeEntry { MspOrganizationId = Org, TicketId = logged.Id, AppUserId = w.Me.Id, Hours = 1 });
        await w.H.Db.SaveChangesAsync();

        (await w.Reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(w.Me.Id, null, null)))).Items.Select(t => t.Title)
            .Should().BeEquivalentTo(["Anika holds", "Anika logged"]);
        (await w.Reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(null, w.Conn.Id, "123")))).Items.Select(t => t.Title)
            .Should().Equal("Resource 123 holds");
    }

    [Fact]
    public async Task Company_queue_source_and_kind_filter_by_what_the_list_shows()
    {
        var w = await WorldAsync();
        var other = Psa(w, "Other queue", 1);
        other.QueueOrBoard = "Projects";
        var board = new Board { MspOrganizationId = Org, Name = "Office IT", Key = "OIT" };
        var inside = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Title = "Team job",
            RequesterName = "Anika", RequesterEmail = "a@x.test", PortalStatus = "NEW", PortalPriority = "NORMAL",
        };
        w.H.Db.AddRange(board, inside, other, Psa(w, "Service desk", 2));
        await w.H.Db.SaveChangesAsync();

        (await w.Reads.PageAsync(new TicketQuery(QueueName: "Projects"))).Total.Should().Be(1);
        (await w.Reads.PageAsync(new TicketQuery(CompanyName: "Acme Dental"))).Total.Should().Be(2);
        (await w.Reads.PageAsync(new TicketQuery(ConnectionName: "Autotask"))).Total.Should().Be(2);
        (await w.Reads.PageAsync(new TicketQuery(Kind: "internal"))).Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Title = "Team job", Origin = TicketOrigin.Internal });
    }

    [Fact]
    public async Task The_filters_offer_every_value_the_caller_can_see_not_only_page_one()
    {
        var w = await WorldAsync();
        var a = Psa(w, "A", 1, priority: "HIGH");
        a.AssignedTechnicianExternalId = "99"; // the account: never offered as a person
        var b = Psa(w, "B", 2);
        b.AssignedAppUserId = w.Colleague.Id;
        b.QueueOrBoard = "Projects";
        w.H.Db.Tickets.AddRange(a, b);
        await w.H.Db.SaveChangesAsync();

        var facets = await w.Reads.FacetsAsync();

        facets.Should().BeEquivalentTo(new
        {
            Priorities = new[] { "HIGH", "NORMAL" },
            Companies = new[] { "Acme Dental" },
            Queues = new[] { "Projects", "Service Desk" },
            Sources = new[] { "Autotask" },
        });
        facets.People.Should().ContainSingle().Which.Name.Should().Be("Arjun");
    }

    [Fact]
    public async Task My_work_counts_only_what_I_hold_and_the_week_s_hours()
    {
        var w = await WorldAsync();
        var now = w.H.Clock.GetUtcNow();
        var overdue = Psa(w, "Late", 3, priority: "HIGH");
        overdue.AssignedAppUserId = w.Me.Id;
        overdue.SlaDueAt = now.AddHours(-1);
        var waiting = Psa(w, "Waiting", 2, status: "WAITING_CUSTOMER");
        waiting.AssignedAppUserId = w.Me.Id;
        var theirs = Psa(w, "Theirs", 1);
        theirs.AssignedAppUserId = w.Colleague.Id;
        theirs.SlaDueAt = now.AddHours(-1);
        w.H.Db.Tickets.AddRange(overdue, waiting, theirs, Psa(w, "Closed", 1, status: "CLOSED"));
        w.H.Db.TicketTimeEntries.Add(new TicketTimeEntry { MspOrganizationId = Org, TicketId = overdue.Id, AppUserId = w.Me.Id, Hours = 2.5m, EntryDate = DateTimeOffset.UtcNow });
        await w.H.Db.SaveChangesAsync();

        var mine = await w.Reads.SummaryAsync(mineOnly: true);
        var everyone = await w.Reads.SummaryAsync(mineOnly: false);

        mine.Should().BeEquivalentTo(new { Open = 2, Overdue = 1, Waiting = 1, HighPriority = 1, HoursLoggedThisWeek = (decimal?)2.5m });
        everyone.Should().BeEquivalentTo(new { Open = 3, Overdue = 2, HoursLoggedThisWeek = (decimal?)null });
        everyone.OpenBySource.Should().ContainSingle().Which.Should().Be(new LabelCount("Autotask", 3));
    }

    [Fact]
    public async Task The_workload_shows_each_person_s_open_work_and_what_nobody_holds()
    {
        var w = await WorldAsync();
        var now = w.H.Clock.GetUtcNow();
        Ticket Held(string title, Guid? who, bool late = false)
        {
            var t = Psa(w, title, 2);
            t.AssignedAppUserId = who;
            if (late) t.SlaDueAt = now.AddHours(-2);
            return t;
        }
        var account = Psa(w, "API user's", 1);
        account.AssignedTechnicianExternalId = "99";
        w.H.Db.Tickets.AddRange(Held("A1", w.Me.Id, late: true), Held("A2", w.Me.Id), Held("R1", w.Colleague.Id),
            Held("Free", null, late: true), account);
        await w.H.Db.SaveChangesAsync();

        var load = await w.Reads.WorkloadAsync();

        load.People.Select(p => (p.Name, p.Open, p.Overdue)).Should().Equal(("Anika", 2, 1), ("Arjun", 1, 0));
        load.Should().BeEquivalentTo(new { Unassigned = 2, UnassignedOverdue = 1 });
    }

    [Fact]
    public async Task A_client_s_page_is_their_company_s_own_tickets_with_no_people()
    {
        var w = await WorldAsync();
        var theirs = Psa(w, "Printer offline", 1);
        theirs.AssignedAppUserId = w.Me.Id;
        var board = new Board { MspOrganizationId = Org, Name = "Office IT", Key = "OIT" };
        var inside = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, ClientCompanyId = w.Acme.Id,
            Title = "Acme migration prep", RequesterName = "Anika", RequesterEmail = "a@x.test", PortalStatus = "NEW", PortalPriority = "NORMAL",
        };
        w.H.Db.AddRange(board, theirs, inside);
        await w.H.Db.SaveChangesAsync();
        var client = new ClientAccess(Org, w.Acme.Id, Guid.NewGuid(), IsCompanyAdministrator: true);

        var page = await w.Reads.PageAsync(new TicketQuery(), client);
        var facets = await w.Reads.FacetsAsync(client);

        page.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Title = "Printer offline", People = (object?)null });
        facets.People.Should().BeEmpty();
        // A person filter from a client is ignored rather than honoured.
        (await w.Reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(w.Me.Id, null, null)), client)).Total.Should().Be(1);
    }
}
