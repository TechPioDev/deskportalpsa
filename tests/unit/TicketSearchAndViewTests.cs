using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Searching, saved views and followers — the three things that turn a list of tickets into a desk
/// somebody can work all day. The rule under every one of them: a search may only ever find what the
/// caller's own list would show them.
/// </summary>
public class TicketSearchAndViewTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid ClientUser = Guid.NewGuid();

    private static AdminHarness Harness(Guid? me = null)
    {
        var h = AdminHarness.Create(Org);
        return me is null ? h : As(h, me.Value);
    }

    /// <summary>
    /// The same database, read by somebody else. "Mine" and "following" are questions about the
    /// caller, so testing them at all means being able to ask as two different people over one set
    /// of tickets.
    /// </summary>
    private static AdminHarness As(AdminHarness h, Guid me) => new()
    {
        Db = h.Db, Tenant = h.Tenant, Secrets = h.Secrets, Clock = h.Clock,
        User = new TestCurrentUser(Org, userId: me),
    };

    private static TicketReadService Reads(AdminHarness h) => new(h.Db, new NoopTicketScopeQuery(), h.User);

    private static async Task<Guid> StaffAsync(AdminHarness h, string name)
    {
        var u = new AppUser { MspOrganizationId = Org, DisplayName = name, Email = $"{name}@techpio.test", IsActive = true };
        h.Db.AppUsers.Add(u);
        await h.Db.SaveChangesAsync();
        return u.Id;
    }

    private static Ticket Psa(string title, string status = "NEW", Guid? company = null) => new()
    {
        MspOrganizationId = Org,
        Origin = TicketOrigin.Psa,
        PsaConnectionId = Guid.NewGuid(),
        Provider = ProviderType.AutotaskPsa,
        ExternalTicketId = "T" + Random.Shared.Next(10000, 99999),
        ClientCompanyId = company ?? Company,
        RequesterName = "Priya Sharma",
        RequesterEmail = "priya@acme.test",
        Title = title,
        PortalStatus = status,
    };

    private static async Task SeedCompanyAsync(AdminHarness h)
    {
        h.Db.ClientCompanies.Add(new Desk.Domain.Tenancy.ClientCompany
        {
            Id = Company, MspOrganizationId = Org, Name = "Acme Manufacturing", ExternalCompanyId = "1",
            PsaConnectionId = Guid.NewGuid(),
        });
        await h.Db.SaveChangesAsync();
    }

    // ---- search -------------------------------------------------------------------------------

    [Fact]
    public async Task A_search_matches_the_number_the_reference_the_subject_and_the_customer()
    {
        var h = Harness();
        await SeedCompanyAsync(h);
        h.Db.Tickets.AddRange(
            Psa("Exchange mailbox quota reached"),
            new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = Guid.NewGuid(),
                Number = "INT-000042", RequesterName = "Dalbir", RequesterEmail = "d@techpio.test",
                Title = "Rebuild the spare laptop", PortalStatus = "NEW",
            });
        await h.Db.SaveChangesAsync();
        var reads = Reads(As(h, await StaffAsync(h, "reader")));

        (await reads.SearchAsync(new TicketQuery(Q: "mailbox"))).Items.Should().HaveCount(1);
        // The number people actually quote to each other, which is not the provider's reference.
        (await reads.SearchAsync(new TicketQuery(Q: "int-000042"))).Items.Should().ContainSingle()
            .Which.Title.Should().Be("Rebuild the spare laptop");
        (await reads.SearchAsync(new TicketQuery(Q: "acme"))).Items.Should().ContainSingle()
            .Which.Title.Should().Be("Exchange mailbox quota reached");
        (await reads.SearchAsync(new TicketQuery(Q: "priya"))).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_conversation_is_searched_only_when_it_is_asked_for()
    {
        var h = Harness();
        var ticket = Psa("Site visit");
        h.Db.Tickets.Add(ticket);
        h.Db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticket.Id, AuthorName = "Basit",
            Body = "Replaced the failing disk in bay 3", NoteCreatedAt = DateTimeOffset.UtcNow, IsPublic = false,
        });
        await h.Db.SaveChangesAsync();
        var reads = Reads(As(h, await StaffAsync(h, "reader")));

        // The whole reason the search exists beside the list page's own filters: this phrase is in
        // a note, and no client-side filter over the loaded columns could ever find it.
        (await reads.SearchAsync(new TicketQuery(Q: "failing disk"))).Items.Should().BeEmpty();
        (await reads.SearchAsync(new TicketQuery(Q: "failing disk", IncludeNotes: true))).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_result_set_cut_at_the_limit_says_so()
    {
        var h = Harness();
        for (var i = 0; i < 12; i++) h.Db.Tickets.Add(Psa($"Printer jam {i}"));
        await h.Db.SaveChangesAsync();

        var page = await Reads(As(h, await StaffAsync(h, "reader")))
            .SearchAsync(new TicketQuery(Q: "printer", Take: 5));

        page.Items.Should().HaveCount(5);
        // Both, and separately: a set silently truncated reads as "there is no more", and somebody
        // then concludes their ticket does not exist.
        page.Total.Should().Be(12);
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Open_unassigned_and_overdue_mean_what_a_desk_means_by_them()
    {
        var h = Harness();
        var held = Psa("Held", "IN_PROGRESS");
        held.AssignedAppUserId = await StaffAsync(h, "basit");
        var lateAndOpen = Psa("Late", "IN_PROGRESS");
        lateAndOpen.SlaDueAt = DateTimeOffset.UtcNow.AddHours(-3);
        var lateButClosed = Psa("Late but done", "CLOSED");
        lateButClosed.SlaDueAt = DateTimeOffset.UtcNow.AddHours(-9);
        h.Db.Tickets.AddRange(held, lateAndOpen, lateButClosed, Psa("Resolved one", "RESOLVED"));
        await h.Db.SaveChangesAsync();
        var reads = Reads(As(h, await StaffAsync(h, "reader")));

        (await reads.SearchAsync(new TicketQuery(Openness: "open"))).Items
            .Select(t => t.Title).Should().BeEquivalentTo("Held", "Late");
        (await reads.SearchAsync(new TicketQuery(Openness: "resolved"))).Items
            .Select(t => t.Title).Should().BeEquivalentTo("Late but done", "Resolved one");
        (await reads.SearchAsync(new TicketQuery(UnassignedOnly: true, Openness: "open"))).Items
            .Select(t => t.Title).Should().BeEquivalentTo("Late");
        // Overdue is work to do, not history: a ticket closed late is not on anybody's list.
        (await reads.SearchAsync(new TicketQuery(OverdueOnly: true))).Items
            .Select(t => t.Title).Should().BeEquivalentTo("Late");
    }

    [Fact]
    public async Task Mine_covers_a_team_the_caller_is_in_as_well_as_their_own_name()
    {
        var h = Harness();
        var me = await StaffAsync(h, "dalbir");
        var colleague = await StaffAsync(h, "tanvi");
        var dept = new Department { MspOrganizationId = Org, Name = "IT Support" };
        var team = new Team { MspOrganizationId = Org, DepartmentId = dept.Id, Name = "Level 2" };
        h.Db.Departments.Add(dept);
        h.Db.Teams.Add(team);
        h.Db.UserTeams.Add(new UserTeam { MspOrganizationId = Org, AppUserId = me, TeamId = team.Id });

        var mine = Psa("My own");
        mine.AssignedAppUserId = me;
        var myTeams = Psa("Routed to Level 2");
        myTeams.AssignedTeamId = team.Id;
        var theirs = Psa("Somebody else's");
        theirs.AssignedAppUserId = colleague;
        h.Db.Tickets.AddRange(mine, myTeams, theirs);
        await h.Db.SaveChangesAsync();

        var reads = Reads(As(h, me));

        (await reads.SearchAsync(new TicketQuery(MineOnly: true))).Items
            .Select(t => t.Title).Should().BeEquivalentTo("My own", "Routed to Level 2");
    }

    [Fact]
    public async Task Mine_asked_by_a_caller_with_no_portal_identity_returns_nothing_not_everything()
    {
        var h = Harness();
        h.Db.Tickets.Add(Psa("Somebody's ticket"));
        await h.Db.SaveChangesAsync();

        // No UserId: a filter about "me" cannot be answered, and answering it with the whole tenant
        // would be the worst possible reading of the question.
        (await Reads(h).SearchAsync(new TicketQuery(MineOnly: true))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_searching_cannot_find_what_their_list_would_not_show_them()
    {
        var h = Harness();
        await SeedCompanyAsync(h);
        var otherCompany = Guid.NewGuid();
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal };
        h.Db.Boards.Add(board);
        h.Db.Tickets.AddRange(
            Psa("Their own printer"),
            Psa("Another customer's printer", company: otherCompany),
            // Names the client, on the team's own board. Never theirs to see.
            new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "INT-000001",
                ClientCompanyId = Company, RequesterName = "Dalbir", RequesterEmail = "d@techpio.test",
                Title = "Printer audit for Acme", PortalStatus = "NEW",
            });
        await h.Db.SaveChangesAsync();

        var access = new ClientAccess(Org, Company, ClientUser, IsCompanyAdministrator: true);
        var found = await Reads(h).SearchAsync(new TicketQuery(Q: "printer"), access);

        found.Items.Select(t => t.Title).Should().BeEquivalentTo("Their own printer");
        // And nothing about the desk travels with the results.
        found.Items.Should().OnlyContain(t => t.People == null && t.BoardId == null && t.AssignedTeamName == null);
    }

    // ---- followers ----------------------------------------------------------------------------

    [Fact]
    public async Task Following_a_ticket_twice_is_the_same_as_following_it_once()
    {
        var h = Harness();
        var me = await StaffAsync(h, "dalbir");
        var ticket = Psa("Watch this");
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();
        var h2 = As(h, me);
        var followers = new TicketFollowerService(h2.Db, new NoopTicketScopeQuery(), h2.User,
            new AuditWriter(h2.Db, h2.User, h2.Tenant, h2.Clock));

        await followers.AddAsync(ticket.Id, me);
        var after = await followers.AddAsync(ticket.Id, me);

        after.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "dalbir", IsMe = true });
        (await followers.FollowedTicketIdsAsync()).Should().BeEquivalentTo([ticket.Id]);
    }

    [Fact]
    public async Task Following_does_not_change_who_holds_the_ticket()
    {
        var h = Harness();
        var holder = await StaffAsync(h, "basit");
        var watcher = await StaffAsync(h, "tanvi");
        var ticket = Psa("Held by Basit");
        ticket.AssignedAppUserId = holder;
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();
        var h2 = As(h, watcher);
        var followers = new TicketFollowerService(h2.Db, new NoopTicketScopeQuery(), h2.User,
            new AuditWriter(h2.Db, h2.User, h2.Tenant, h2.Clock));

        await followers.AddAsync(ticket.Id, watcher);

        // A follower is not accountable for the work. That is the whole distinction, so it is the
        // thing worth a test.
        (await h.Db.Tickets.FindAsync(ticket.Id))!.AssignedAppUserId.Should().Be(holder);
        var listed = await Reads(h2).SearchAsync(new TicketQuery(FollowingOnly: true));
        listed.Items.Should().ContainSingle().Which.Following.Should().BeTrue();
    }

    [Fact]
    public async Task Unfollowing_something_nobody_follows_is_not_an_error()
    {
        var h = Harness();
        var me = await StaffAsync(h, "dalbir");
        var ticket = Psa("Nobody watching");
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();
        var h2 = As(h, me);
        var followers = new TicketFollowerService(h2.Db, new NoopTicketScopeQuery(), h2.User,
            new AuditWriter(h2.Db, h2.User, h2.Tenant, h2.Clock));

        (await followers.RemoveAsync(ticket.Id, me)).Should().BeEmpty();
    }

    // ---- saved views --------------------------------------------------------------------------

    private static TicketViewService Views(AdminHarness h) => new(h.Db, h.User, h.Tenant);

    [Fact]
    public async Task A_saved_view_keeps_the_filters_it_was_given()
    {
        var h = Harness();
        var me = await StaffAsync(h, "dalbir");
        var h2 = As(h, me);

        var saved = await Views(h2).SaveAsync(null, "Mohali overdue", shared: true, null,
            new SavedViewFilters(Company: "Acme Manufacturing", OverdueOnly: true, RaisedWithinDays: 30));

        saved.Should().BeEquivalentTo(new { Name = "Mohali overdue", Shared = true, IsMine = true, OwnerName = "dalbir" });
        saved.Filters.Should().BeEquivalentTo(new { Company = "Acme Manufacturing", OverdueOnly = true, RaisedWithinDays = 30 });
        (await Views(h2).ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_view_that_narrows_nothing_is_the_list_itself_and_is_refused()
    {
        var h = Harness();
        var h2 = As(h, await StaffAsync(h, "dalbir"));

        await Assert.ThrowsAsync<ValidationFailedException>(
            () => Views(h2).SaveAsync(null, "Everything", false, null, new SavedViewFilters()));
    }

    [Fact]
    public async Task Two_people_may_each_have_a_view_of_the_same_name_but_one_person_may_not()
    {
        var h = Harness();
        var mine = As(h, await StaffAsync(h, "dalbir"));
        var theirs = As(h, await StaffAsync(h, "tanvi"));

        await Views(mine).SaveAsync(null, "Overdue kit", false, null, new SavedViewFilters(OverdueOnly: true));
        // A name is one person's own label, so a colleague's identical one is not a clash.
        await Views(theirs).SaveAsync(null, "Overdue kit", false, null, new SavedViewFilters(OverdueOnly: true));

        await Assert.ThrowsAsync<ValidationFailedException>(
            () => Views(mine).SaveAsync(null, "overdue kit", false, null, new SavedViewFilters(MineOnly: true)));
    }

    [Fact]
    public async Task A_shared_view_is_offered_to_the_team_but_only_its_owner_may_change_it()
    {
        var h = Harness();
        var owner = await StaffAsync(h, "dalbir");
        var colleague = await StaffAsync(h, "tanvi");
        var mine = As(h, owner);
        var theirs = As(h, colleague);

        var shared = await Views(mine).SaveAsync(null, "Night shift", shared: true, null, new SavedViewFilters(UnassignedOnly: true));
        var privateOne = await Views(mine).SaveAsync(null, "Just mine", shared: false, null, new SavedViewFilters(MineOnly: true));

        var seen = await Views(theirs).ListAsync();
        seen.Select(v => v.Name).Should().BeEquivalentTo("Night shift");
        seen.Single().Should().BeEquivalentTo(new { IsMine = false, OwnerName = "dalbir" });

        // Editing or deleting it would change or remove the view under everybody else using it.
        await Assert.ThrowsAsync<ForbiddenException>(
            () => Views(theirs).SaveAsync(shared.Id, "Renamed", true, null, new SavedViewFilters(OverdueOnly: true)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Views(theirs).DeleteAsync(shared.Id));
        await Views(mine).DeleteAsync(privateOne.Id);
        (await Views(mine).ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_board_view_belongs_to_that_board_and_is_not_offered_on_the_whole_list()
    {
        var h = Harness();
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal };
        h.Db.Boards.Add(board);
        await h.Db.SaveChangesAsync();
        var me = As(h, await StaffAsync(h, "dalbir"));

        await Views(me).SaveAsync(null, "Patching backlog", false, board.Id, new SavedViewFilters(Openness: "open"));

        (await Views(me).ListAsync(board.Id)).Should().ContainSingle();
        // Its filters are about that queue; offering it over every ticket in the tenant would mean
        // something else entirely.
        (await Views(me).ListAsync()).Should().BeEmpty();
    }
}
