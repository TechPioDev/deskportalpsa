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
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        // Strict about the query warnings production would log: a query that loads two collections must
        // say AsSingleQuery or AsSplitQuery on purpose, and a split one must not take "the first" of an
        // unordered set (its separate queries could then disagree on which row that is).
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection)
            .ConfigureWarnings(w => w.Throw(
                RelationalEventId.MultipleCollectionIncludeWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning,
                CoreEventId.FirstWithoutOrderByAndFilterWarning)).Options, _tenant, _clock);
        _db.Database.EnsureCreated();

        var org = new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio" };
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "dalbir@techpio.test", IsActive = true };
        var colleague = new AppUser { MspOrganizationId = Org, DisplayName = "Anika", Email = "anika@techpio.test", IsActive = true };
        var dept = new Department { MspOrganizationId = Org, Name = "IT Support" };
        var team = new Team { MspOrganizationId = Org, DepartmentId = dept.Id, Name = "Level 2" };
        // NextNumber past the ticket seeded below as INT-000001, as a board that raised it would be.
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal, NextNumber = 2 };
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
    public async Task Two_people_cannot_take_the_same_board_number()
    {
        // Both read the same next number. Without the concurrency token the second save simply won,
        // and the collision surfaced later as a unique-index failure on the ticket itself.
        await using var other = new DeskDbContext(
            new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        var mine = await _db.Boards.SingleAsync(b => b.Id == _boardId);
        var theirs = await other.Boards.SingleAsync(b => b.Id == _boardId);

        mine.NextNumber++;
        await _db.SaveChangesAsync();
        theirs.NextNumber++;

        await other.Invoking(o => o.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task The_paged_list_facets_breakdown_summary_and_workload_translate()
    {
        // Every new list query through a real SQL translator: group-bys on an upper-cased column,
        // distinct anonymous pairs, sums as double, the integration account's OR-chain.
        var reads = new TicketReadService(_db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: _me));
        var page = await reads.PageAsync(new TicketQuery(
            Q: "swelling", Status: "IN_PROGRESS", Priority: "HIGH", Openness: "open", MineOnly: true, FollowingOnly: true,
            UnassignedOnly: true, OverdueOnly: true, DueSoonOnly: true, CompanyName: "Acme", QueueName: "Internal",
            ConnectionName: "Autotask", PersonKey: PersonKey.For(_me, null), RaisedSince: DateTimeOffset.UtcNow.AddDays(-30),
            Kind: "internal", Skip: 0, Take: 10));
        page.Total.Should().Be(0);
        (await reads.PageAsync(new TicketQuery(PersonKey: "x:123", Skip: 1, Take: 5))).Total.Should().BeGreaterThanOrEqualTo(0);
        (await reads.PageAsync(new TicketQuery())).Total.Should().BeGreaterThan(0);
        (await reads.FacetsAsync()).Statuses.Should().NotBeEmpty();
        (await reads.BreakdownAsync(DateTimeOffset.UtcNow.AddDays(-30))).Should().NotBeNull();
        (await reads.SummaryAsync(mineOnly: true)).Should().NotBeNull();
        (await reads.SummaryAsync(mineOnly: false)).Open.Should().BeGreaterThanOrEqualTo(0);
        (await reads.WorkloadAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Technician_metrics_translate_with_either_identity_and_ratings()
    {
        var metrics = new Desk.Infrastructure.Analytics.TechnicianMetricsService(_db, new Desk.Application.Analytics.ProductivityScorer(), _clock);
        var self = new Desk.Application.Analytics.MetricsFilter { AppUserId = _me, TechnicianExternalId = "123", EitherIdentity = true };
        (await metrics.ForTechnicianAsync(self, Desk.Application.Analytics.ProductivityWeights.Default)).Should().NotBeNull();
        (await metrics.DailyAsync(self)).Should().NotBeNull();
        (await metrics.TeamAsync(new Desk.Application.Analytics.MetricsFilter(), Desk.Application.Analytics.ProductivityWeights.Default)).Should().NotBeNull();

        // Credit through a linked PSA login: the correlated identity subqueries, for tickets and time.
        var conn = new Desk.Domain.Tenancy.PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = Desk.Domain.Enums.ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "m",
        };
        _db.PsaConnections.Add(conn);
        _db.UserPsaIdentities.Add(new Desk.Domain.Identity.UserPsaIdentity
            { MspOrganizationId = Org, AppUserId = _me, PsaConnectionId = conn.Id, ExternalTechnicianId = "123" });
        await _db.SaveChangesAsync();
        var manager = new Desk.Application.Analytics.MetricsFilter { AppUserId = _me };
        (await metrics.ForTechnicianAsync(manager, Desk.Application.Analytics.ProductivityWeights.Default)).Should().NotBeNull();
        (await metrics.DailyAsync(manager)).Should().NotBeNull();
        var login = new Desk.Application.Analytics.MetricsFilter { TechnicianExternalId = "123" };
        (await metrics.ForTechnicianAsync(login, Desk.Application.Analytics.ProductivityWeights.Default)).Should().NotBeNull();
        (await metrics.DailyAsync(login)).Should().NotBeNull();
        var reads = new TicketReadService(_db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: _me));
        (await reads.PageAsync(new TicketQuery(PersonKey: PersonKey.For(_me, null)))).Should().NotBeNull();
        (await reads.WorkloadAsync()).Should().NotBeNull();
        (await reads.FacetsAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Review_queue_and_links_translate()
    {
        var reads = new TicketReadService(_db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: _me));
        (await reads.PageAsync(new TicketQuery(ReviewPending: true))).Total.Should().Be(0);
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        var links = new TicketLinkService(_db, new NoopTicketScopeQuery(), new Desk.Infrastructure.Admin.AuditWriter(_db, new TestCurrentUser(Org, userId: _me), _tenant, _clock));
        (await links.ListAsync(ticket, _me)).Should().BeEmpty();
        (await new TicketStatusWriter(_db, null!, null!).ReviewRequiredAsync(ticket, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Workforce_schedules_and_skills_translate()
    {
        // Every workforce query through a real SQL translator: the scope subqueries (team,
        // department), the all-skills count, holder counts, and time/date columns.
        var role = new Desk.Domain.Identity.Role { MspOrganizationId = Org, Name = "Admin", BuiltInType = Desk.Domain.Enums.RoleType.MspAdministrator };
        role.Permissions.Add(new Desk.Domain.Identity.RolePermission { PermissionKey = Desk.Domain.Authorization.Permissions.ScheduleView, Scope = Desk.Domain.Authorization.PermissionScope.Team });
        role.Permissions.Add(new Desk.Domain.Identity.RolePermission { PermissionKey = Desk.Domain.Authorization.Permissions.WorkforceManage });
        _db.Roles.Add(role);
        _db.UserRoles.Add(new Desk.Domain.Identity.UserRole { AppUserId = _me, RoleId = role.Id });
        await _db.SaveChangesAsync();

        var access = new Desk.Infrastructure.Workforce.WorkforceAccess(_db, _tenant, new Desk.Infrastructure.Authorization.EffectivePermissionService(_db));
        var audit = new Desk.Infrastructure.Admin.AuditWriter(_db, User, _tenant, _clock);
        var schedules = new Desk.Infrastructure.Workforce.WorkScheduleService(_db, access, audit, _clock);
        var skills = new Desk.Infrastructure.Workforce.SkillService(_db, access, _tenant, User, audit);

        var saved = await schedules.SaveAsync(_me, _me, new Desk.Application.Workforce.WorkScheduleInput(null, "UTC",
            [new Desk.Application.Workforce.WorkDayInput(DayOfWeek.Monday, "18:00", "03:00", [new Desk.Application.Workforce.WorkBreakDto("00:00", "00:30")])]));
        saved.Current!.Days.Single().UsableMinutes.Should().Be(510);
        (await schedules.GetAsync(_me, _me)).Current!.Days.Single().Breaks.Single().Start.Should().Be("00:00");

        var skill = await skills.CreateAsync("SonicWall", null);
        await skills.AssignAsync(_me, _me, skill.Id, Desk.Domain.Workforce.SkillLevel.Expert);
        (await skills.ListAsync(includeInactive: true)).Single().HolderCount.Should().Be(1);
        var all = await schedules.PeopleAsync(_me, new Desk.Application.Workforce.WorkforceQuery(SkillIds: [skill.Id], MatchAllSkills: true));
        all.Single().Skills.Single().Name.Should().Be("SonicWall");
        (await schedules.PeopleAsync(_me, new Desk.Application.Workforce.WorkforceQuery(DepartmentId: Guid.NewGuid()))).Should().BeEmpty();

        role.Permissions.Single(p => p.PermissionKey == Desk.Domain.Authorization.Permissions.ScheduleView).Scope = Desk.Domain.Authorization.PermissionScope.Department;
        await _db.SaveChangesAsync();
        (await schedules.PeopleAsync(_me, new Desk.Application.Workforce.WorkforceQuery())).Should().ContainSingle();
    }

    [Fact]
    public async Task Ticket_history_translates()
    {
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        (await new TicketHistoryService(_db).ForAsync(ticket)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Every_search_filter_translates()
    {
        var all = await Reads.SearchAsync(new TicketQuery(
            Q: "swelling", IncludeNotes: true, Openness: "open", OverdueOnly: true, MineOnly: true,
            RaisedWithinDays: 7, BoardId: _boardId, Take: 10));

        all.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Number = "INT-000001", AssignedTeamName = "Level 2" });
        (await Reads.SearchAsync(new TicketQuery(Openness: "resolved", UnassignedOnly: true, FollowingOnly: true))).Total.Should().Be(0);
        // A client's conversation search reads public notes only; the clause must still translate.
        (await Reads.SearchAsync(new TicketQuery(Q: "swelling", IncludeNotes: true),
            new ClientAccess(Org, Guid.NewGuid(), Guid.NewGuid(), IsCompanyAdministrator: true))).Total.Should().Be(0);
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

    [Fact]
    public async Task Holidays_recurring_tickets_and_the_pause_translate()
    {
        var audit = new AuditWriter(_db, User, _tenant, _clock);
        var plans = new SlaPlanService(_db, _tenant, audit);
        await plans.AddHolidayAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), "Diwali");
        (await plans.HolidaysAsync(DateOnly.FromDateTime(DateTime.UtcNow))).Should().ContainSingle();
        var (_, holidays) = await SlaPlanner.CalendarAsync(_db, _db.MspOrganizations.Single().Id, default);
        holidays.Should().ContainSingle();

        var tickets = new InternalTicketService(_db, _tenant, _clock, new RecordingActivity());
        var recurring = new RecurringTicketService(_db, _tenant, tickets, audit, _clock);
        var saved = await recurring.SaveAsync(null, new Desk.Application.Boards.RecurringTicketInput(
            _boardId, "Check the backups", Checklist: "Look\nRecord", Frequency: RecurrenceFrequency.Daily), _me);
        (await recurring.ListAsync()).Single().Schedule.Should().Be("Every day at 09:00");

        // The worker's question, as it asks it: due and active, across every tenant.
        var now = DateTimeOffset.UtcNow.AddDays(2);
        (await _db.RecurringTickets.AsNoTracking().Where(r => r.IsActive && r.NextRunAt <= now).CountAsync()).Should().Be(1);

        var run = await recurring.RunNowAsync(saved.Id);
        run.Number.Should().NotBeNull();
        (await recurring.ListAsync()).Single().LastTicketNumber.Should().Be(run.Number);

        // Pausing a ticket whose plan asks for it loads plan, zone and holidays in one go.
        var plan = await plans.SaveAsync(null, new Desk.Application.Boards.SlaPlanInput("Standard", 8));
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == run.TicketId);
        ticket.SlaPlanId = plan.Id;
        await SlaPlanner.ApplyStatusAsync(_db, ticket, "WAITING_CUSTOMER", DateTimeOffset.UtcNow, default);
        ticket.SlaPausedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Satisfaction_translates()
    {
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        _db.TicketSatisfactions.Add(new TicketSatisfaction
        {
            MspOrganizationId = ticket.MspOrganizationId, TicketId = _ticketId, ClientUserId = Guid.NewGuid(),
            Rating = 2, Comment = "Slow", RatedAt = DateTimeOffset.UtcNow, TechnicianAppUserId = _me, TechnicianName = "Dalbir",
        });
        await _db.SaveChangesAsync();

        var summary = await new SatisfactionService(_db, _clock).SummaryAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        summary.Recent.Should().ContainSingle().Which.Reference.Should().Be("INT-000001");
        (await Reads.GetDetailForStaffAsync(_ticketId))!.Rating!.Rating.Should().Be(2);
        // The needs-attention query, as it is written there: the cut-off captured first.
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        (await _db.TicketSatisfactions.AsNoTracking().Where(s => s.RatedAt >= weekAgo && s.Rating <= 2).CountAsync())
            .Should().Be(1);
    }

    private sealed class NoResync : Desk.Application.Admin.ITicketResyncService
    {
        public Task<Desk.Application.Admin.UnsyncedTicketsDto> ListAsync(Guid? connectionId = null, CancellationToken ct = default)
            => Task.FromResult(new Desk.Application.Admin.UnsyncedTicketsDto(0, []));
        public Task<Desk.Application.Admin.ResyncResultDto> ResyncAsync(Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoMail : Desk.Application.Common.IEmailSender
    {
        public Task<Desk.Application.Common.EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(Desk.Application.Common.EmailSenderStatus.None);
        public Task SendAsync(Guid organizationId, Desk.Application.Common.EmailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Sla_warnings_and_due_soon_translate()
    {
        // The seeded ticket is IN_PROGRESS and two hours overdue: it is exactly a breached one.
        var attention = new AttentionService(_db, _tenant, new NoResync(), new NoMail(),
            new AuditWriter(_db, User, _tenant, _clock), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AttentionService>.Instance);
        (await attention.ListAsync()).Items.Should().Contain(i => i.Kind == "sla-breached" && i.Count == 1);

        (await Reads.SearchAsync(new TicketQuery(DueSoonOnly: true))).Total.Should().Be(0);
    }

    [Fact]
    public async Task Approvals_translate()
    {
        var connection = new Desk.Domain.Tenancy.PsaConnection
        {
            MspOrganizationId = Org, Name = "TechPio AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        };
        var company = new Desk.Domain.Tenancy.ClientCompany { MspOrganizationId = Org, Name = "Acme", ExternalCompanyId = "9", PsaConnectionId = connection.Id };
        var board = new Board { MspOrganizationId = Org, Name = "Acme monitoring", Key = "ACP", Kind = BoardKind.Rmm, ClientVisible = true };
        var rahul = new Desk.Domain.Tenancy.ClientUser { MspOrganizationId = Org, ClientCompanyId = company.Id, Email = "Rahul@Acme.test", DisplayName = "Rahul" };
        var approver = new Desk.Domain.ControlPanel.Approver { MspOrganizationId = Org, ClientCompanyId = company.Id, Name = "Rahul", Email = "rahul@acme.test" };
        var ticket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = "ACP-000001", ClientCompanyId = company.Id,
            RequesterName = "Priya", RequesterEmail = "p@acme.test", Title = "Acrobat", PortalStatus = "NEW", SyncStatus = TicketSyncStatus.Synced,
        };
        _db.AddRange(connection, company, board, rahul, approver, ticket);
        await _db.SaveChangesAsync();

        var svc = new ApprovalService(_db, new NoopTicketScopeQuery(), new TicketStatusWriter(_db, null!, null!), new NoTrail(),
            new AuditWriter(_db, User, _tenant, _clock), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ApprovalService>.Instance);
        (await svc.StaffViewAsync(_me, ticket.Id)).Approvers.Should().ContainSingle().Which.CanAnswerInPortal.Should().BeTrue();
        var asked = await svc.RequestAsync(_me, "Dalbir", ticket.Id, new ApprovalRequestInput(approver.Id, "Acrobat licence"));

        var access = new ClientAccess(Org, company.Id, rahul.Id, IsCompanyAdministrator: true);
        (await svc.MineAsync(access)).Should().ContainSingle().Which.Reference.Should().Be("ACP-000001");
        (await svc.ClientViewAsync(access, ticket.Id)).Should().ContainSingle().Which.CanAnswer.Should().BeTrue();

        // The needs-attention query, run against a request old enough to be listed.
        var row = await _db.TicketApprovals.SingleAsync(a => a.Id == asked.Id);
        row.RequestedAt = DateTimeOffset.UtcNow.AddDays(-3);
        await _db.SaveChangesAsync();
        var attention = new AttentionService(_db, _tenant, new NoResync(), new NoMail(),
            new AuditWriter(_db, User, _tenant, _clock), _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AttentionService>.Instance);
        (await attention.ListAsync()).Items.Should().Contain(i => i.Kind == "approval-waiting" && i.Count == 1);

        (await svc.DecideAsync(access, asked.Id, true, null)).State.Should().Be("Approved");
        (await _db.Tickets.SingleAsync(t => t.Id == ticket.Id)).PortalStatus.Should().Be("IN_PROGRESS");
    }

    [Fact]
    public async Task Devices_translate()
    {
        var connection = new Desk.Domain.Tenancy.PsaConnection
        {
            MspOrganizationId = Org, Name = "TechPio AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        };
        var company = new Desk.Domain.Tenancy.ClientCompany { MspOrganizationId = Org, Name = "Acme", ExternalCompanyId = "176", PsaConnectionId = connection.Id };
        var ticket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = connection.Id, Provider = ProviderType.AutotaskPsa,
            ExternalTicketId = "9001", ClientCompanyId = company.Id, RequesterName = "Priya", RequesterEmail = "p@acme.test",
            Title = "Disk failing", PortalStatus = "IN_PROGRESS", DeviceExternalId = "70", SyncStatus = TicketSyncStatus.Synced,
        };
        _db.AddRange(connection, company, ticket);
        await _db.SaveChangesAsync();

        var psa = new StubConnector();
        psa.Devices["176"] = [new Desk.PsaCore.Models.ExternalDevice("70", "ACME-SRV01", "Server", "7XK29", true, DateTimeOffset.UtcNow.AddYears(1))];
        var sync = new Desk.Infrastructure.ControlPanel.DeviceSyncService(_db, new OneConnector(psa), _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Desk.Infrastructure.ControlPanel.DeviceSyncService>.Instance);
        (await sync.SyncConnectionAsync(connection.Id)).TicketsLinked.Should().Be(1);

        var settings = new Desk.Infrastructure.ControlPanel.AccountSettingsService(_db, new AuditWriter(_db, User, _tenant, _clock), new OneConnector(psa), sync);
        var admin = new ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true);
        var device = (await settings.ListDevicesAsync(admin)).Should().ContainSingle().Subject;
        device.OpenTickets.Should().Be(1);
        (await settings.GetDeviceAsync(admin, device.Id)).Tickets.Should().ContainSingle();

        // The pickers' queries: a client's list, a technician's list and a change on a PSA ticket.
        var pickers = new Desk.Infrastructure.Tickets.TicketDeviceService(_db, new NoopTicketScopeQuery(), new OneConnector(psa));
        (await pickers.ClientChoicesAsync(admin)).Should().ContainSingle().Which.Name.Should().Be("ACME-SRV01");
        (await pickers.ChoicesAsync(_me, ticket.Id)).Should().ContainSingle().Which.Unavailable.Should().BeNull();
        (await pickers.SetAsync(_me, ticket.Id, null)).Should().BeNull();
    }

    [Fact]
    public async Task Knowledge_base_translates()
    {
        var connection = new Desk.Domain.Tenancy.PsaConnection
        {
            MspOrganizationId = Org, Name = "TechPio AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        };
        var company = new Desk.Domain.Tenancy.ClientCompany { MspOrganizationId = Org, Name = "Acme", ExternalCompanyId = "176", PsaConnectionId = connection.Id };
        _db.AddRange(connection, company);
        _db.FaqArticles.Add(new Desk.Domain.ControlPanel.FaqArticle { MspOrganizationId = Org, ClientCompanyId = company.Id, Question = "Where is the printer?", Answer = "Upstairs." });
        await _db.SaveChangesAsync();

        var kb = new Desk.Infrastructure.Knowledge.KnowledgeBaseService(_db, new AuditWriter(_db, User, _tenant, _clock), _clock);
        var article = await kb.SaveAsync(null, new Desk.Application.Knowledge.KbArticleInput(
            "Connect to the VPN", "Open FortiClient.", "VPN", "SelectedClients", [company.Id], true), _me, "Dalbir");
        var access = new ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: false);

        (await kb.HelpAsync(access, null)).Should().HaveCount(2);
        (await kb.SuggestAsync(access, "vpn at home")).Should().ContainSingle();
        await kb.SolvedAsync(access, "Team", article.Id, "vpn at home");
        (await kb.StatsAsync(30)).TicketsAvoided.Should().Be(1);
        (await kb.ListAsync(null)).Single().Solved.Should().Be(1);
        await kb.DeleteAsync(article.Id);
    }

    [Fact]
    public async Task Push_scan_and_delivery_translate()
    {
        _db.PushSubscriptions.Add(new Desk.Domain.Notifications.PushSubscription
        {
            MspOrganizationId = Org, AppUserId = _me, Endpoint = "https://fcm.googleapis.com/fcm/send/x",
            P256dh = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", Auth = "BTBZMqHH6r4Tts7J_aSIgg",
        });
        await _db.SaveChangesAsync();
        var scanner = new Desk.Infrastructure.Notifications.PushScanner(_db, _clock);
        await scanner.ScanAsync();

        // The seeded ticket gets a client reply and a new holder.
        var ticket = await _db.Tickets.SingleAsync(t => t.Id == _ticketId);
        ticket.AssignedAppUserId = _me;
        _db.TicketNotes.Add(new TicketNote { MspOrganizationId = Org, TicketId = _ticketId, AuthorName = "Priya", AuthoredByClient = true,
            IsPublic = true, Body = "Still broken", NoteCreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        (await scanner.ScanAsync()).Should().Be(2);

        // Delivery reads what is unsent, by age; with nothing reachable it records why rather than throwing.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2); // captured first, as PushDelivery does
        (await _db.PushNotifications.Where(n => n.SentAt == null && n.CreatedAt >= cutoff)
            .OrderBy(n => n.CreatedAt).CountAsync()).Should().Be(2);
    }

    private sealed class OneConnector(Desk.PsaCore.Contracts.IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<Desk.PsaCore.Contracts.IServiceManagementConnector> ResolveAsync(Guid connectionId, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed class NoTrail : ITicketCommandService
    {
        public Task PostTrailNoteAsync(Guid ticketId, string authorName, bool authoredByClient, string body, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CreateTicketResultDto> CreateAsync(ClientAccess access, CreateTicketInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddCommentAsync(ClientAccess access, Guid ticketId, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic, bool emailContact, IReadOnlyList<string> emailCc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReplyRecipientsDto> ListReplyRecipientsAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RefreshContactAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
