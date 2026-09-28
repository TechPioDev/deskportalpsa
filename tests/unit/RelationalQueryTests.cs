using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The same queries, run through a real SQL translator.
///
/// Every other test here uses the in-memory provider, which does not translate LINQ at all — it runs
/// it as C#. So a query that no database can execute passes every one of those tests: the board
/// members list ordered by a property of the record it had just constructed, and answered 500 on
/// Postgres for as long as it existed, with its tests green the whole time. SQLite is the database
/// local mode already runs, and it refuses exactly what Postgres refuses.
///
/// Not a behaviour suite — the in-memory tests cover behaviour. Each test here exists to prove a
/// query TRANSLATES, and asserts only enough to show it ran.
/// </summary>
public sealed class RelationalQueryTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DeskDbContext _db;
    private readonly TenantContext _tenant = new();
    // Now, not the default fixed date: the search's date window and its overdue test are measured
    // against the real clock, and rows stamped in January fall outside "raised in the last 7 days".
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);
    private readonly Guid _me;
    private readonly Guid _ticketId;
    private readonly Guid _boardId;

    public RelationalQueryTests()
    {
        // One connection held open for the life of the test: an in-memory SQLite database exists
        // only while a connection to it does.
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();

        var org = new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio" };
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "dalbir@techpio.test", IsActive = true };
        var colleague = new AppUser { MspOrganizationId = Org, DisplayName = "Anika", Email = "anika@techpio.test", IsActive = true };
        var dept = new Department { MspOrganizationId = Org, Name = "IT Support" };
        var team = new Team { MspOrganizationId = Org, DepartmentId = dept.Id, Name = "Level 2" };
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal };
        var ticket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "INT-000001",
            RequesterName = "Dalbir", RequesterEmail = "dalbir@techpio.test", Title = "Replace the UPS battery",
            PortalStatus = "IN_PROGRESS", AssignedTeamId = team.Id, SlaDueAt = DateTimeOffset.UtcNow.AddHours(-2),
            SyncStatus = TicketSyncStatus.Synced,
        };
        _db.AddRange(org, me, colleague, dept, team, board, ticket);
        _db.UserTeams.Add(new UserTeam { MspOrganizationId = Org, AppUserId = me.Id, TeamId = team.Id });
        _db.BoardMembers.AddRange(
            new BoardMember { MspOrganizationId = Org, BoardId = board.Id, AppUserId = me.Id },
            new BoardMember { MspOrganizationId = Org, BoardId = board.Id, AppUserId = colleague.Id });
        _db.TicketNotes.Add(new TicketNote
        {
            MspOrganizationId = Org, TicketId = ticket.Id, AuthorName = "Anika",
            Body = "Battery bank in rack 2 is swelling", NoteCreatedAt = DateTimeOffset.UtcNow, IsPublic = false,
        });
        _db.SaveChanges();

        _me = me.Id;
        _ticketId = ticket.Id;
        _boardId = board.Id;
    }

    private TestCurrentUser User => new(Org, userId: _me);
    private TicketReadService Reads => new(_db, new NoopTicketScopeQuery(), User);

    [Fact]
    public async Task Every_search_filter_translates()
    {
        var all = await Reads.SearchAsync(new TicketQuery(
            Q: "swelling", IncludeNotes: true, Openness: "open", OverdueOnly: true, MineOnly: true,
            RaisedWithinDays: 7, BoardId: _boardId, Take: 10));

        all.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Number = "INT-000001", AssignedTeamName = "Level 2" });
        (await Reads.SearchAsync(new TicketQuery(Openness: "resolved", UnassignedOnly: true, FollowingOnly: true))).Total.Should().Be(0);
    }

    [Fact]
    public async Task The_list_and_the_detail_translate_with_followers_and_a_team()
    {
        var followers = new TicketFollowerService(_db, new NoopTicketScopeQuery(), User, new AuditWriter(_db, User, _tenant, _clock));
        (await followers.AddAsync(_ticketId, _me)).Should().ContainSingle();
        (await followers.FollowedTicketIdsAsync()).Should().ContainSingle();

        (await Reads.ListAllAsync()).Should().ContainSingle().Which.Following.Should().BeTrue();
        var detail = await Reads.GetDetailForStaffAsync(_ticketId);
        detail!.Followers.Should().ContainSingle();
        detail.AssignedTeamName.Should().Be("Level 2");
    }

    [Fact]
    public async Task Saved_views_translate()
    {
        var views = new TicketViewService(_db, User, _tenant);
        await views.SaveAsync(null, "Overdue", shared: true, _boardId, new SavedViewFilters(OverdueOnly: true));
        (await views.ListAsync(_boardId)).Should().ContainSingle().Which.OwnerName.Should().Be("Dalbir");
    }

    [Fact]
    public async Task Board_members_translate()
    {
        // The query that answered 500 in production while its tests passed.
        var boards = new BoardService(_db, _tenant, new AuditWriter(_db, User, _tenant, _clock));
        (await boards.MembersAsync(_boardId)).Select(m => m.DisplayName).Should().Equal("Anika", "Dalbir");
    }

    [Fact]
    public async Task Sla_plans_canned_responses_and_tasks_translate()
    {
        var audit = new AuditWriter(_db, User, _tenant, _clock);
        var plans = new SlaPlanService(_db, _tenant, audit);
        var plan = await plans.SaveAsync(null, new Desk.Application.Boards.SlaPlanInput("Priority", 8, 1, BusinessHoursOnly: true));
        var boards = new BoardService(_db, _tenant, audit);
        var board = (await boards.ListAsync()).Single();
        await boards.UpdateAsync(board.Id, new Desk.Application.Boards.BoardInput(board.Name, board.Key, null, DefaultSlaPlanId: plan.Id));
        (await plans.ListAsync()).Single().UsedBy.Should().Be(1);
        (await boards.ListAsync()).Single().DefaultSlaPlanName.Should().Be("Priority");

        var canned = new CannedResponseService(_db, _tenant, User, new NoopTicketScopeQuery(), audit);
        await canned.SaveAsync(null, new Desk.Application.Boards.CannedResponseInput("Received", "Thanks — {ticket.number}"));
        await canned.SaveAsync(null, new Desk.Application.Boards.CannedResponseInput("Board only", "Here", _boardId));
        (await canned.ListAsync(_ticketId)).Should().HaveCount(2);

        var tasks = new TicketTaskService(_db, _tenant, User, new NoopTicketScopeQuery(), _clock);
        var list = await tasks.AddAsync(_ticketId, "Order the battery", _me);
        list = await tasks.AddAsync(_ticketId, "Fit it", null);
        list = await tasks.SetDoneAsync(list[0].Id, true);
        list = await tasks.MoveAsync(list[1].Id, -1);
        list.Select(t => t.Title).Should().Equal("Fit it", "Order the battery");
        list[1].Should().BeEquivalentTo(new { AssignedName = "Dalbir", DoneByName = "Dalbir" });
        (await TicketTaskService.OpenCountAsync(_db, _ticketId, default)).Should().Be(1);

        // The list's task counts are two more subqueries in an already long projection.
        var row = (await Reads.ListAllAsync()).Single();
        row.Should().BeEquivalentTo(new { TaskCount = 2, TasksDone = 1 });
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
