using System.Data.Common;
using System.Diagnostics;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Desk.Tests.Unit;

/// <summary>
/// Capacity for many people at once, through a real SQL translator, with every database command
/// counted. Two things are proven: the queries translate (the in-memory provider would not notice
/// one that no database can run), and their NUMBER does not grow with the number of people or days -
/// no query per technician, no query per day.
///
/// The timings are printed for the phase report; the time limits asserted are deliberately loose
/// (a shared CI runner is slow) and exist only to catch something going quadratic.
///
/// SQLite by default, so it runs everywhere. Set DESK_TEST_POSTGRES to a server (for example
/// "Host=localhost;Port=15439;Username=desk;Password=...") and the same tests run on PostgreSQL
/// instead, each in a database of its own that is created by the real migrations and dropped
/// afterwards - which is how the figures in docs/workforce-scheduling/capacity.md were measured.
/// </summary>
public sealed class CapacityPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 1, 5);
    private const string Zone = "Asia/Kolkata";
    private static readonly string? Postgres = Environment.GetEnvironmentVariable("DESK_TEST_POSTGRES");
    private readonly string _database = $"desk_p2_{Guid.NewGuid():N}";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TenantContext _tenant = new();
    private readonly TestClock _clock = new();
    private readonly Counter _counter = new();

    private sealed class Counter : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public void Reset() => Commands = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    /// <summary>Planned work for everyone: two confirmed pieces and one tentative, every weekday.</summary>
    private sealed class BusyDesk(TimeZoneInfo zone) : IWorkAllocationReader
    {
        public Task<IReadOnlyList<AllocatedSpan>> ForAsync(Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            var spans = new List<AllocatedSpan>();
            for (var d = DateOnly.FromDateTime(from.UtcDateTime); d <= DateOnly.FromDateTime(to.UtcDateTime); d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                Interval At(string a, string b) => new(
                    Desk.Domain.Common.TimeZones.WallToUtc(d.ToDateTime(TimeOnly.Parse(a)), zone, true),
                    Desk.Domain.Common.TimeZones.WallToUtc(d.ToDateTime(TimeOnly.Parse(b)), zone, false));
                foreach (var id in appUserIds)
                {
                    spans.Add(new AllocatedSpan(id, At("09:00", "11:00"), true, Guid.NewGuid()));
                    spans.Add(new AllocatedSpan(id, At("13:30", "15:00"), true, Guid.NewGuid()));
                    spans.Add(new AllocatedSpan(id, At("16:00", "16:30"), false, Guid.NewGuid()));
                }
            }
            return Task.FromResult<IReadOnlyList<AllocatedSpan>>(spans);
        }
    }

    private DeskDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<DeskDbContext>();
        if (Postgres is null) builder.UseSqlite(_connection);
        else builder.UseNpgsql($"{Postgres};Database={_database}");
        return new DeskDbContext(builder
            .AddInterceptors(_counter)
            .ConfigureWarnings(w => w.Throw(
                RelationalEventId.MultipleCollectionIncludeWarning,
                CoreEventId.RowLimitingOperationWithoutOrderByWarning,
                CoreEventId.FirstWithoutOrderByAndFilterWarning)).Options, _tenant, _clock);
    }

    /// <summary>An organization of <paramref name="people"/> technicians: schedules, teams, skills and some time away.</summary>
    private async Task<(DeskDbContext Db, Guid Admin, Guid TeamId, Guid SkillId, List<Guid> People)> SeedAsync(int people)
    {
        _tenant.SetTenant(Org);
        var db = NewContext();
        if (Postgres is null)
        {
            _connection.Open();
            await db.Database.EnsureCreatedAsync();
        }
        // On PostgreSQL the schema comes from the migrations themselves, as it does in production.
        else await db.Database.MigrateAsync();

        var role = new Role { MspOrganizationId = Org, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        foreach (var key in new[] { Permissions.ScheduleView, Permissions.WorkforceManage, Permissions.AvailabilityManage, Permissions.ScheduleManage, Permissions.ScheduleOverride, Permissions.TicketsViewAll, Permissions.TicketsLogTime, Permissions.WorkforceAnalyticsExport })
            role.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        var admin = new AppUser { MspOrganizationId = Org, DisplayName = "Admin", Email = "admin@techpio.test", IsActive = true };
        admin.Roles.Add(new UserRole { RoleId = role.Id });
        // The administrator works too, so their own work can be planned in the tests below.
        var adminSchedule = new WorkSchedule { MspOrganizationId = Org, AppUserId = admin.Id, EffectiveFrom = new DateOnly(2025, 6, 1), TimeZone = Zone };
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            adminSchedule.Days.Add(new WorkScheduleDay { MspOrganizationId = Org, Day = day, Start = new TimeOnly(8, 30), End = new TimeOnly(17, 30) });
        db.Add(adminSchedule);
        var dept = new Department { MspOrganizationId = Org, Name = "Operations" };
        var teams = Enumerable.Range(0, 5).Select(i => new Team { MspOrganizationId = Org, Department = dept, Name = $"Team {i}" }).ToList();
        var skills = Enumerable.Range(0, 4).Select(i => new Skill { MspOrganizationId = Org, Name = $"Skill {i}", NormalizedName = $"SKILL {i}" }).ToList();
        db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = Zone }, role, admin, dept);
        db.AddRange(teams);
        db.AddRange(skills);

        var ids = new List<Guid>(people);
        for (var i = 0; i < people; i++)
        {
            var p = new AppUser { MspOrganizationId = Org, DisplayName = $"Tech {i:000}", Email = $"tech{i}@techpio.test", IsActive = true };
            ids.Add(p.Id);
            db.Add(p);
            db.Add(new UserTeam { MspOrganizationId = Org, AppUserId = p.Id, TeamId = teams[i % teams.Count].Id });
            db.Add(new StaffSkill { MspOrganizationId = Org, AppUserId = p.Id, SkillId = skills[i % skills.Count].Id, Level = SkillLevel.Proficient });
            // Two schedule versions each: the one in force and an older one, as a real desk accumulates.
            foreach (var from in new[] { new DateOnly(2025, 6, 1), new DateOnly(2025, 12, 1) })
            {
                var schedule = new WorkSchedule { MspOrganizationId = Org, AppUserId = p.Id, EffectiveFrom = from, TimeZone = Zone };
                foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
                {
                    var d = new WorkScheduleDay { MspOrganizationId = Org, Day = day, Start = new TimeOnly(8, 30), End = new TimeOnly(17, 30) };
                    d.Breaks.Add(new WorkScheduleBreak { MspOrganizationId = Org, Start = new TimeOnly(12, 30), End = new TimeOnly(13, 30) });
                    schedule.Days.Add(d);
                }
                db.Add(schedule);
            }
            // Every fifth person has a day off in the range and an appointment.
            if (i % 5 == 0)
            {
                db.Add(new CapacityException { MspOrganizationId = Org, AppUserId = p.Id, TimeZone = Zone, AllDay = true, FromDate = Monday.AddDays(2), ToDate = Monday.AddDays(3), Reason = CapacityExceptionReason.TimeOff });
                db.Add(new CapacityException
                {
                    MspOrganizationId = Org, AppUserId = p.Id, TimeZone = Zone, FromDate = Monday, ToDate = Monday, Reason = CapacityExceptionReason.Appointment,
                    StartsAt = new DateTimeOffset(Monday.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero), EndsAt = new DateTimeOffset(Monday.ToDateTime(new TimeOnly(10, 30)), TimeSpan.Zero),
                });
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, admin.Id, teams[0].Id, skills[0].Id, ids);
    }

    private CapacityService Service(DeskDbContext db)
        => new(db, new WorkforceAccess(db, _tenant, new EffectivePermissionService(db)),
            new BusyDesk(Desk.Domain.Common.TimeZones.Resolve(Zone)), _clock);

    /// <summary>The real stack: allocations from the table, ticket visibility through the real scope query.</summary>
    private (WorkPlanService Plans, CapacityService Capacity) RealStack(DeskDbContext db, Guid callerId)
    {
        var (plans, capacity, _) = FullStack(db, callerId);
        return (plans, capacity);
    }

    /// <summary>The real services, including the Phase 6 clock, over a real database.</summary>
    private (WorkPlanService Plans, CapacityService Capacity, WorkTimeService Time) FullStack(DeskDbContext db, Guid callerId)
    {
        var permissions = new EffectivePermissionService(db);
        var access = new WorkforceAccess(db, _tenant, permissions);
        var scope = new TicketScopeQuery(db, permissions);
        var audit = new AuditWriter(db, new TestCurrentUser(Org, userId: callerId), _tenant, _clock);
        var capacity = new CapacityService(db, access, new WorkAllocationReader(db, scope, _clock), _clock);
        var gate = new PlanningGate(db);
        var plans = new WorkPlanService(db, access, capacity, scope, new InternalTicketService(db, _tenant, _clock, new RecordingActivity()), gate, audit, _clock);
        var time = new WorkTimeService(db, access, capacity, scope, plans, new NoResolver(), new TicketTimeWriter(db, null!, audit), permissions, gate, audit, _clock);
        return (plans, capacity, time);
    }

    private sealed class NoResolver : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<Desk.PsaCore.Contracts.IServiceManagementConnector> ResolveAsync(Guid psaConnectionId, CancellationToken ct = default) => throw new NotSupportedException("no PSA in this test");
    }

    /// <summary>A board with one open ticket per person, and allocations on it: <paramref name="perPerson"/> per weekday over four weeks.</summary>
    private async Task<(Guid BoardId, List<Guid> TicketIds, int Allocations)> SeedAllocationsAsync(DeskDbContext db, Guid adminId, List<Guid> ids, int perPerson, bool everyDay = false)
    {
        var board = new Board { MspOrganizationId = Org, Name = "Internal", Key = "INT", Kind = BoardKind.Internal, NextNumber = ids.Count + 1 };
        db.Add(board);
        var tickets = new List<Guid>();
        var n = 0;
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        foreach (var id in ids)
        {
            var ticket = new Ticket
            {
                MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board.Id, Number = $"INT-{++n:000000}", RequesterName = "Admin", RequesterEmail = "admin@techpio.test",
                Title = $"Work for {n}", PortalStatus = "IN_PROGRESS", AssignedAppUserId = id, SyncStatus = TicketSyncStatus.Synced,
            };
            db.Add(ticket);
            tickets.Add(ticket.Id);
            for (var day = 0; day < 28; day++)
            {
                var d = Monday.AddDays(day);
                if (!everyDay && d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                for (var k = 0; k < perPerson; k++)
                {
                    // Back to back from 08:30, inside the morning: every one lands on working time.
                    var start = Desk.Domain.Common.TimeZones.WallToUtc(d.ToDateTime(new TimeOnly(8 + k, 30)), zone, true);
                    db.Add(new WorkAllocation
                    {
                        MspOrganizationId = Org, TicketId = ticket.Id, AppUserId = id, StartsAt = start, EndsAt = start.AddMinutes(60), PlannedMinutes = 60,
                        Method = SchedulingMethod.AuthorizedUser, ScheduledByUserId = adminId,
                    });
                }
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (board.Id, tickets, await db.WorkAllocations.CountAsync());
    }

    /// <summary>The Phase 7 analytics over the same database: the real access, scope and capacity services.</summary>
    private WorkforceAnalyticsService Analytics(DeskDbContext db, Guid callerId)
    {
        var permissions = new EffectivePermissionService(db);
        var access = new WorkforceAccess(db, _tenant, permissions);
        var scope = new TicketScopeQuery(db, permissions);
        var audit = new AuditWriter(db, new TestCurrentUser(Org, userId: callerId), _tenant, _clock);
        var capacity = new CapacityService(db, access, new WorkAllocationReader(db, scope, _clock), _clock);
        return new WorkforceAnalyticsService(db, access, capacity, scope, permissions, audit, _clock);
    }

    /// <summary>Recorded time on everyone's ticket: <paramref name="perPerson"/> entries of 45 minutes every weekday over four weeks, from 09:00 local.</summary>
    private async Task<int> SeedTimeAsync(DeskDbContext db, List<Guid> ids, List<Guid> tickets, int perPerson)
    {
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        var n = 0;
        for (var i = 0; i < ids.Count; i++)
            for (var day = 0; day < 28; day++)
            {
                var d = Monday.AddDays(day);
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                for (var k = 0; k < perPerson; k++)
                {
                    db.Add(new TicketTimeEntry
                    {
                        MspOrganizationId = Org, TicketId = tickets[i], AppUserId = ids[i], Hours = 0.75m, Billable = k % 2 == 0, WorkTypeLabel = k % 2 == 0 ? "Remote" : "Onsite",
                        EntryDate = Desk.Domain.Common.TimeZones.WallToUtc(d.ToDateTime(new TimeOnly(9 + k, 0)), zone, true), Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced,
                    });
                    n++;
                }
            }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return n;
    }

    /// <summary>
    /// Phase 7: the analytics for everyone over a week and a month, one team, one person's detail, a
    /// drill-down, an export and the filter lists - each a fixed number of queries however many people,
    /// days or hours there are (one load per request; every figure is tallied from it in memory).
    /// </summary>
    [Theory]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    [InlineData(500, 3)]
    public async Task Workforce_analytics_cost_the_same_number_of_queries_whatever_the_number_of_people_days_or_hours(int people, int perPerson)
    {
        var (db, admin, teamId, _, ids) = await SeedAsync(people);
        var (_, tickets, allocations) = await SeedAllocationsAsync(db, admin, ids, perPerson);
        var entries = await SeedTimeAsync(db, ids, tickets, perPerson);
        var analytics = Analytics(db, admin);
        var week = new AnalyticsQuery("custom", Monday, Monday.AddDays(6));
        var month = new AnalyticsQuery("custom", Monday, Monday.AddDays(27));

        var overviewWeek = await MeasureAsync(() => analytics.OverviewAsync(admin, week));
        var overviewMonth = await MeasureAsync(() => analytics.OverviewAsync(admin, month));
        var teamWeek = await MeasureAsync(() => analytics.OverviewAsync(admin, week with { TeamId = teamId }));
        var person = await MeasureAsync(() => analytics.TechnicianAsync(admin, ids[1], month));
        var drill = await MeasureAsync(() => analytics.WorkAsync(admin, week, AnalyticsWorkKind.Actual, 0, 50));
        var export = await MeasureAsync(() => analytics.ExportAsync(admin, month, AnalyticsExportReport.Technicians));
        var filters = await MeasureAsync(() => analytics.FiltersAsync(admin));

        output.WriteLine($"{people} people, {allocations} allocations, {entries} time entries | overview week: {overviewWeek.Commands} queries, {overviewWeek.Ms} ms | overview month: {overviewMonth.Commands} queries, {overviewMonth.Ms} ms | team week: {teamWeek.Commands} queries, {teamWeek.Ms} ms ({teamWeek.Result.People.Count} people)");
        output.WriteLine($"{people} people | technician month: {person.Commands} queries, {person.Ms} ms ({person.Result.Items.Count} items) | drill-down week: {drill.Commands} queries, {drill.Ms} ms ({drill.Result.Total} rows) | export month: {export.Commands} queries, {export.Ms} ms ({export.Result.Rows} rows) | filters: {filters.Commands} queries, {filters.Ms} ms");

        // Right at scale: every weekday holds perPerson hours planned and perPerson × 45 minutes recorded, all on the day's planned ticket.
        overviewWeek.Result.People.Should().HaveCount(people + 1);
        overviewWeek.Result.Totals.PlannedMinutes.Should().Be(people * perPerson * 60 * 5);
        overviewWeek.Result.Totals.ActualSeconds.Should().Be(people * perPerson * 45 * 60 * 5);
        overviewWeek.Result.Totals.PlannedActualSeconds.Should().Be(overviewWeek.Result.Totals.ActualSeconds);
        overviewWeek.Result.Totals.ReactiveActualSeconds.Should().Be(0);
        overviewMonth.Result.Totals.ActualSeconds.Should().Be(people * perPerson * 45 * 60 * 20);
        overviewMonth.Result.Heatmap.Rows.Should().HaveCount(Math.Min(people + 1, WorkforceAnalyticsService.HeatmapMaxPeople));
        overviewWeek.Result.People.Sum(p => p.Figures.ActualSeconds).Should().Be(overviewWeek.Result.Totals.ActualSeconds, "the rows add up to the card");
        overviewWeek.Result.Sources.Sum(s => s.Figures.ActualSeconds).Should().Be(overviewWeek.Result.Totals.ActualSeconds);
        overviewWeek.Result.WorkTypes.Sum(s => s.Figures.ActualSeconds).Should().Be(overviewWeek.Result.Totals.ActualSeconds);
        overviewWeek.Result.Daily.Sum(d => d.Figures.ActualSeconds).Should().Be(overviewWeek.Result.Totals.ActualSeconds);
        teamWeek.Result.People.Should().HaveCountLessThan(people + 1);
        person.Result.Items.Should().ContainSingle().Which.ActualSeconds.Should().Be(perPerson * 45 * 60 * 20);
        drill.Result.TotalSeconds.Should().Be(overviewWeek.Result.Totals.ActualSeconds, "the drill-down reconciles with the card");
        drill.Result.Rows.Should().HaveCount(Math.Min(50, drill.Result.Total));
        export.Result.Rows.Should().Be(people + 1);

        // Constant whatever the size: one load (people, teams, calendar, allocations, entries, clocks, finished and open work, the
        // tickets, visibility, names), then memory. Measured 27 / 27 / 27 / 26 / 26 / 29 / 18 at 50, 100 and 500 people alike.
        overviewWeek.Commands.Should().BeLessThanOrEqualTo(27);
        overviewMonth.Commands.Should().BeLessThanOrEqualTo(27);
        teamWeek.Commands.Should().BeLessThanOrEqualTo(27);
        person.Commands.Should().BeLessThanOrEqualTo(26);
        drill.Commands.Should().BeLessThanOrEqualTo(26);
        export.Commands.Should().BeLessThanOrEqualTo(29);
        filters.Commands.Should().BeLessThanOrEqualTo(18);
        foreach (var ms in new[] { overviewWeek.Ms, overviewMonth.Ms, teamWeek.Ms, person.Ms, drill.Ms, export.Ms, filters.Ms }) ms.Should().BeLessThan(30_000);
    }

    private async Task<(int Commands, long Ms, T Result)> MeasureAsync<T>(Func<Task<T>> work)
    {
        _counter.Reset();
        var sw = Stopwatch.StartNew();
        var result = await work();
        sw.Stop();
        return (_counter.Commands, sw.ElapsedMilliseconds, result);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task Team_capacity_and_the_availability_search_cost_the_same_number_of_queries_for_any_number_of_people(int people)
    {
        var (db, admin, teamId, skillId, ids) = await SeedAsync(people);
        var capacity = Service(db);

        var team = await MeasureAsync(() => capacity.ForTeamAsync(admin, new TeamCapacityQuery(Monday)));
        var filtered = await MeasureAsync(() => capacity.ForTeamAsync(admin, new TeamCapacityQuery(Monday, TeamId: teamId, SkillIds: [skillId])));
        var oneDay = await MeasureAsync(() => capacity.FindAsync(admin, new AvailabilitySearch(Monday, null, 90, EarliestTime: "13:00", LatestTime: "17:30")));
        var sevenDays = await MeasureAsync(() => capacity.FindAsync(admin, new AvailabilitySearch(Monday, Monday.AddDays(6), 90, SkillIds: [skillId])));
        var fourteenDays = await MeasureAsync(() => capacity.FindAsync(admin, new AvailabilitySearch(Monday, Monday.AddDays(13), 240)));
        var month = await MeasureAsync(() => capacity.ForPersonAsync(admin, ids[0], Monday, Monday.AddDays(29)));
        // The engine itself over 30 days for everyone - further than any one request is allowed to
        // ask, to show where the cost would be if it were.
        var everyone = await MeasureAsync(async () =>
        {
            var calendar = await WorkforceCalendar.LoadAsync(db, new BusyDesk(Desk.Domain.Common.TimeZones.Resolve(Zone)), admin, Zone, ids, Monday, Monday.AddDays(29), null, default);
            return ids.Sum(id => calendar.InputsFor(id).Select(CapacityCalculator.ForDay).Sum(d => d.RemainingConfirmedMinutes));
        });

        output.WriteLine($"{people} people | team capacity, 1 day: {team.Commands} queries, {team.Ms} ms | filtered: {filtered.Commands} queries, {filtered.Ms} ms");
        output.WriteLine($"{people} people | search 1 day: {oneDay.Commands} queries, {oneDay.Ms} ms | 7 days: {sevenDays.Commands} queries, {sevenDays.Ms} ms | 14 days: {fourteenDays.Commands} queries, {fourteenDays.Ms} ms");
        output.WriteLine($"{people} people | one person, 30 days: {month.Commands} queries, {month.Ms} ms | engine, everyone x 30 days: {everyone.Commands} queries, {everyone.Ms} ms");

        // The same small number whatever the size: no query per person, no query per day.
        // Measured: 12 for a team day, 13 with a skill filter, 9 for a search, 10 for one person's month,
        // 3 for the calendar itself - at 50, 100 and 500 people alike.
        team.Commands.Should().BeLessThanOrEqualTo(12);
        filtered.Commands.Should().BeLessThanOrEqualTo(13);
        oneDay.Commands.Should().BeLessThanOrEqualTo(9);
        sevenDays.Commands.Should().Be(oneDay.Commands + 1, "seven days cost what one does, plus the one skill-catalogue check this search asked for");
        fourteenDays.Commands.Should().Be(oneDay.Commands);
        month.Commands.Should().BeLessThanOrEqualTo(10);
        everyone.Commands.Should().BeLessThanOrEqualTo(3);

        // Right answers, not just fast ones. Everyone works Monday; a fifth also has an appointment.
        team.Result.People.Should().HaveCount(people + 1);
        team.Result.People.Count(p => p.Day.UsableMinutes == 480).Should().Be(people - (people + 4) / 5);
        team.Result.People.Count(p => p.Day.UnavailableMinutes == 60).Should().Be((people + 4) / 5, "09:30-10:30 UTC is working time wherever this host puts the zone");
        oneDay.Result.Matches.Should().HaveCount(Math.Min(people + 1, 200), "everyone (the administrator too) is free 15:00-17:30, and the answer is capped at 200");
        oneDay.Result.TotalMatches.Should().Be(people + 1, "and it says how many fit in all, so a list cut short is never mistaken for everyone");
        oneDay.Result.PeopleConsidered.Should().Be(people + 1);
        month.Result.Days.Should().HaveCount(30);

        foreach (var ms in new[] { team.Ms, filtered.Ms, oneDay.Ms, sevenDays.Ms, fourteenDays.Ms, month.Ms })
            ms.Should().BeLessThan(15_000);
        everyone.Ms.Should().BeLessThan(30_000);
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    [InlineData(500, 3)]
    public async Task Planned_work_costs_the_same_number_of_queries_whatever_the_number_of_allocations(int people, int perPerson)
    {
        var (db, admin, _, _, ids) = await SeedAsync(people);
        var (_, tickets, total) = await SeedAllocationsAsync(db, admin, ids, perPerson);
        var (plans, capacity) = RealStack(db, admin);

        // Person 1 has no time away (every fifth person does), so every working day holds exactly the seeded work.
        var plan = await MeasureAsync(() => plans.PlanAsync(admin, ids[1], Monday, Monday.AddDays(6)));
        var team = await MeasureAsync(() => capacity.ForTeamAsync(admin, new TeamCapacityQuery(Monday.AddDays(1))));
        var search = await MeasureAsync(() => capacity.FindAsync(admin, new AvailabilitySearch(Monday, Monday.AddDays(13), 90)));
        var onTicket = await MeasureAsync(() => plans.ForTicketAsync(admin, tickets[0]));
        var place = await MeasureAsync(() => plans.CreateAsync(admin, new WorkAllocationInput(tickets[0], ids[0],
            Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(21).ToDateTime(new TimeOnly(15, 0)), Desk.Domain.Common.TimeZones.Resolve(Zone), true),
            Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(21).ToDateTime(new TimeOnly(16, 0)), Desk.Domain.Common.TimeZones.Resolve(Zone), true))));
        // The team scheduler: a day and a week for everyone, and the group's unscheduled work.
        var schedulerDay = await MeasureAsync(() => plans.TeamAsync(admin, new TeamPlanQuery(Monday.AddDays(1), Monday.AddDays(1))));
        var schedulerWeek = await MeasureAsync(() => plans.TeamAsync(admin, new TeamPlanQuery(Monday, Monday.AddDays(6))));
        var queue = await MeasureAsync(() => plans.UnscheduledTeamAsync(admin, new TeamPlanQuery()));
        // Work execution: one person's day (planned against actual, with a clock running), the team today and the active lookup.
        // The technicians here hold no time-logging permission (bare users), so the administrator runs the clock; the day read is a technician's, through the administrator's scope.
        var (_, _, time) = FullStack(db, admin);
        var clockOn = await time.StartAsync(admin, new StartWorkInput(tickets[1]));
        var myDay = await MeasureAsync(() => time.MyDayAsync(admin, ids[1], Monday.AddDays(1)));
        var active = await MeasureAsync(() => time.ActiveAsync(admin));
        var teamToday = await MeasureAsync(() => time.TeamTodayAsync(admin, new TeamPlanQuery(Monday.AddDays(1))));
        await time.StopAsync(admin, clockOn.Id, new StopWorkInput(clockOn.Version, Discard: true));
        // Advanced planning: the planning queue over a fortnight, and a split preview of ten hours across a week for one person.
        var planningQueue = await MeasureAsync(() => plans.QueueAsync(admin, new TeamPlanQuery(), 14));
        var preview = await MeasureAsync(() => plans.PreviewAsync(admin, new PlanPreviewInput(tickets[1], ids[2],
            Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(21).ToDateTime(new TimeOnly(8, 0)), Desk.Domain.Common.TimeZones.Resolve(Zone), true),
            Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(27).ToDateTime(new TimeOnly(18, 0)), Desk.Domain.Common.TimeZones.Resolve(Zone), true), 600, Splittable: true)));

        output.WriteLine($"{people} people, {total} allocations | one person's week: {plan.Commands} queries, {plan.Ms} ms | team day: {team.Commands} queries, {team.Ms} ms | 14-day search: {search.Commands} queries, {search.Ms} ms | on a ticket: {onTicket.Commands} queries, {onTicket.Ms} ms | place work: {place.Commands} queries, {place.Ms} ms");
        output.WriteLine($"{people} people, {total} allocations | scheduler day: {schedulerDay.Commands} queries, {schedulerDay.Ms} ms ({schedulerDay.Result.AllocationCount} blocks) | scheduler week: {schedulerWeek.Commands} queries, {schedulerWeek.Ms} ms ({schedulerWeek.Result.AllocationCount} blocks) | team queue: {queue.Commands} queries, {queue.Ms} ms ({queue.Result.Count} items)");
        schedulerDay.Result.People.Should().HaveCount(people + 1);
        schedulerDay.Result.AllocationCount.Should().Be(people * perPerson);
        schedulerWeek.Result.AllocationCount.Should().Be(people * perPerson * 5);
        // Constant whatever the size: the rows' capacity (as Team capacity costs it) plus one query
        // for everyone's planned work, shaped in one pass.
        schedulerDay.Commands.Should().BeLessThanOrEqualTo(42);
        schedulerWeek.Commands.Should().BeLessThanOrEqualTo(42);
        queue.Commands.Should().BeLessThanOrEqualTo(8);
        output.WriteLine($"{people} people, {total} allocations | planning queue: {planningQueue.Commands} queries, {planningQueue.Ms} ms ({planningQueue.Result.Items.Count} items, {planningQueue.Result.PeopleCounted} people) | split preview: {preview.Commands} queries, {preview.Ms} ms ({preview.Result.Pieces.Count} pieces)");
        // The queue is the unscheduled list plus the planning rows, the effort sums and the group's fortnight of capacity; a preview is one person's capacity over the window.
        planningQueue.Commands.Should().BeLessThanOrEqualTo(26);
        preview.Commands.Should().BeLessThanOrEqualTo(29);
        preview.Result.AllocatedMinutes.Should().Be(600);
        output.WriteLine($"{people} people, {total} allocations | my day: {myDay.Commands} queries, {myDay.Ms} ms ({myDay.Result.Items.Count} items) | active: {active.Commands} queries, {active.Ms} ms | team today: {teamToday.Commands} queries, {teamToday.Ms} ms ({teamToday.Result.People.Count} people)");
        active.Result.Should().ContainSingle().Which.Status.Should().Be(WorkSessionStatus.Active);
        myDay.Result.Items.Should().ContainSingle("every allocation of the day is on the person's one ticket").Which.PlannedMinutes.Should().Be(perPerson * 60);
        teamToday.Result.People.Should().HaveCount(people + 1);
        teamToday.Result.Working.Should().Be(1, "the administrator's clock");
        // Constant whatever the size: a day is the person's capacity, their allocations, entries and sessions; the team is one query each over everyone.
        myDay.Commands.Should().BeLessThanOrEqualTo(27);
        // The active lookup reads the day the clock started on once (the outside-the-schedule fact): the session plus one capacity day.
        active.Commands.Should().BeLessThanOrEqualTo(17);
        teamToday.Commands.Should().BeLessThanOrEqualTo(27);

        plan.Result.Allocations.Should().HaveCount(perPerson * 5);
        plan.Result.Days.Where(d => d.IsWorkingDay).Should().OnlyContain(d => d.ConfirmedMinutes == perPerson * 60);
        team.Result.People.Count(p => p.Day.ConfirmedMinutes == perPerson * 60).Should().Be(people, "every technician; the administrator has nothing planned");
        place.Result.PlannedMinutes.Should().Be(60);
        // The same number whatever the size - no query per person, per day or per allocation. Higher
        // than the Phase 2 figures because every read now also resolves ticket visibility and the
        // caller's planning rights (measured: 40, 17, 14, 21, 50).
        plan.Commands.Should().BeLessThanOrEqualTo(40);
        team.Commands.Should().BeLessThanOrEqualTo(17);
        search.Commands.Should().BeLessThanOrEqualTo(14);
        onTicket.Commands.Should().BeLessThanOrEqualTo(21);
        // Phase 5 added one read per placement: the ticket's planning row (its skill and due date feed the check).
        place.Commands.Should().BeLessThanOrEqualTo(51);
        foreach (var ms in new[] { plan.Ms, team.Ms, search.Ms, onTicket.Ms, place.Ms }) ms.Should().BeLessThan(15_000);
    }

    [Fact]
    public async Task The_team_scheduler_at_a_hundred_thousand_allocations_on_a_real_database_reads_only_its_window()
    {
        // 500 people with seven pieces of work every day for four weeks: ~98,000 rows. A day and a
        // week of the scheduler read only their own window, with the same handful of queries.
        if (Postgres is null) return;
        var (db, admin, _, _, ids) = await SeedAsync(500);
        var (_, _, total) = await SeedAllocationsAsync(db, admin, ids, 7, everyDay: true);
        var (plans, _) = RealStack(db, admin);
        var day = await MeasureAsync(() => plans.TeamAsync(admin, new TeamPlanQuery(Monday.AddDays(8), Monday.AddDays(8))));
        var week = await MeasureAsync(() => plans.TeamAsync(admin, new TeamPlanQuery(Monday.AddDays(7), Monday.AddDays(13))));
        var queue = await MeasureAsync(() => plans.UnscheduledTeamAsync(admin, new TeamPlanQuery()));
        output.WriteLine($"PostgreSQL | 500 people, {total} allocations | scheduler day: {day.Commands} queries, {day.Ms} ms ({day.Result.AllocationCount} blocks) | week: {week.Commands} queries, {week.Ms} ms ({week.Result.AllocationCount} blocks) | queue: {queue.Commands} queries, {queue.Ms} ms");
        total.Should().BeGreaterThan(95_000);
        day.Result.AllocationCount.Should().Be(500 * 7);
        week.Result.AllocationCount.Should().Be(500 * 7 * 7);
        day.Commands.Should().BeLessThanOrEqualTo(42);
        week.Commands.Should().BeLessThanOrEqualTo(42);
        day.Ms.Should().BeLessThan(10_000);
        week.Ms.Should().BeLessThan(20_000);
    }

    [Fact]
    public async Task Two_requests_for_the_same_hour_on_a_real_database_end_with_one_booking()
    {
        // Two contexts on two connections, as two requests are; the gate is the row lock on the
        // person, which holds across API containers. Exactly one wins and the other is told the time
        // has gone. Needs PostgreSQL: the SQLite test database lives on one shared connection, and
        // the in-process gate it would use instead is covered by WorkPlanTests.
        if (Postgres is null) return;
        var (db, admin, _, _, ids) = await SeedAsync(3);
        var (_, tickets, _) = await SeedAllocationsAsync(db, admin, ids, 0);
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var a = RealStack(dbA, admin).Plans;
        var b = RealStack(dbB, admin).Plans;
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        var start = Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(7).ToDateTime(new TimeOnly(15, 0)), zone, true);
        var end = start.AddHours(1);

        // Both plan work the person already holds (the seeded technicians have no ticket scope of
        // their own, so a ticket held by someone else would be refused for a different reason).
        var results = await Task.WhenAll(
            Attempt(() => a.CreateAsync(admin, new WorkAllocationInput(tickets[0], ids[0], start, end))),
            Attempt(() => b.CreateAsync(admin, new WorkAllocationInput(tickets[0], ids[0], start, end))));

        results.Count(r => r is null).Should().Be(1, "one of the two succeeds");
        // The administrator may override, so the loser is told the time clashes and asked for a reason.
        results.Single(r => r is not null).Should().BeOfType<Desk.Application.Common.ConflictException>().Which.Message.Should().Contain("Already has confirmed work during this period");
        (await db.WorkAllocations.CountAsync(x => x.AppUserId == ids[0] && x.StartsAt == start)).Should().Be(1);

        static async Task<Exception?> Attempt(Func<Task<WorkAllocationDto>> call)
        {
            try { await call(); return null; }
            catch (Exception ex) { return ex; }
        }
    }

    [Fact]
    public async Task Every_capacity_query_runs_on_a_real_database()
    {
        var (db, admin, teamId, skillId, ids) = await SeedAsync(6);
        var access = new WorkforceAccess(db, _tenant, new EffectivePermissionService(db));
        var audit = new Desk.Infrastructure.Admin.AuditWriter(db, new TestCurrentUser(Org, userId: admin), _tenant, _clock);
        var exceptions = new CapacityExceptionService(db, access, audit, _clock);
        var capacity = Service(db);
        var person = ids[1];

        var timed = await exceptions.AddAsync(admin, person, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, false, Monday, null, "15:00", "16:00", CapacityExceptionReason.Meeting, "Review"));
        await exceptions.AddAsync(admin, person, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, true, Monday.AddDays(1), Monday.AddDays(2), null, null, CapacityExceptionReason.TimeOff, null));
        await exceptions.AddAsync(admin, person, new CapacityExceptionInput(CapacityExceptionKind.AdditionalAvailability, false, Monday, null, "18:00", "20:00", CapacityExceptionReason.Other, null));
        var again = () => exceptions.AddAsync(admin, person, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, false, Monday, null, "15:00", "16:00", CapacityExceptionReason.Meeting, null));
        await again.Should().ThrowAsync<Desk.Application.Common.ValidationFailedException>("the duplicate check compares dates and instants in SQL");
        (await exceptions.UpdateAsync(admin, person, timed.Id, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, false, Monday, null, "15:00", "16:30", CapacityExceptionReason.Meeting, null))).EndTime.Should().Be("16:30");
        (await exceptions.ListAsync(admin, person, Monday, Monday.AddDays(6))).Should().HaveCount(3);

        var week = await capacity.ForPersonAsync(admin, person, Monday, Monday.AddDays(6));
        week.Days[0].AdditionalMinutes.Should().Be(120);
        week.Days[0].UnavailableMinutes.Should().Be(90);
        week.Days[1].UnavailableAllDay.Should().BeTrue();
        week.Days[2].UsableMinutes.Should().Be(0);
        week.Days[0].Exceptions.Should().HaveCount(2);

        (await capacity.ForTeamAsync(admin, new TeamCapacityQuery(Monday, TeamId: teamId, SkillIds: [skillId], MatchAllSkills: false))).People.Should().NotBeEmpty();
        (await capacity.GroupsAsync(admin)).Teams.Should().HaveCount(5);
        (await capacity.FindAsync(admin, new AvailabilitySearch(Monday, Monday.AddDays(2), 60, TeamId: teamId, SkillIds: [skillId], AppUserIds: ids))).PeopleConsidered.Should().BeGreaterThan(0);
        var check = await capacity.EvaluateAsync(admin, person, new ProposedWork(
            new DateTimeOffset(Monday.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero), new DateTimeOffset(Monday.ToDateTime(new TimeOnly(10, 30)), TimeSpan.Zero), SkillIds: [skillId]));
        check.Conflicts.Should().NotBeEmpty();

        await exceptions.RemoveAsync(admin, person, timed.Id);
        (await db.CapacityExceptions.CountAsync(e => e.AppUserId == person)).Should().Be(2);

        // Planned work, through every path, on the same engine.
        var (_, tickets, _) = await SeedAllocationsAsync(db, admin, ids, 0);
        var (plans, realCapacity) = RealStack(db, admin);
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        DateTimeOffset AtWall(int day, int hour) => Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(day).ToDateTime(new TimeOnly(hour, 0)), zone, true);
        var placed = await plans.CreateAsync(admin, new WorkAllocationInput(tickets[2], ids[2], AtWall(7, 9), AtWall(7, 10), IsFixed: true, Note: "Change window"));
        var moved = await plans.UpdateAsync(admin, placed.Id, new WorkAllocationUpdate(AtWall(7, 10), AtWall(7, 11), placed.Version, IsFixed: false));
        moved.Version.Should().Be(placed.Version + 1);
        var given = await plans.ReassignAsync(admin, placed.Id, new WorkAllocationReassign(ids[3], moved.Version, AtWall(8, 9), AtWall(8, 10)));
        given.AppUserId.Should().Be(ids[3]);
        (await plans.PlanAsync(admin, ids[3], Monday.AddDays(8), Monday.AddDays(8))).Allocations.Should().ContainSingle();
        (await realCapacity.ForPersonAsync(admin, ids[3], Monday.AddDays(8), Monday.AddDays(8))).Days.Single().ConfirmedMinutes.Should().Be(60);
        (await plans.ForTicketAsync(admin, tickets[2])).Should().ContainSingle();
        (await plans.UnscheduledAsync(admin)).Should().BeEmpty("the administrator holds no ticket");
        var internalWork = await plans.CreateInternalWorkAsync(admin, new InternalWorkInput((await db.Boards.SingleAsync()).Id, "Documentation", null, null, AtWall(9, 9), AtWall(9, 10)));
        internalWork.Reference.Should().StartWith("INT-");
        (await plans.CancelAsync(admin, given.Id, "Done elsewhere")).Status.Should().Be(WorkAllocationStatus.Cancelled);
        (await plans.PlannablePeopleAsync(admin)).Should().HaveCount(7);

        // Advanced planning on the real engine: tentative, confirm, pencil in, the requirement, the queue,
        // and a preview written in one transaction or not at all.
        var pencilled = await plans.CreateAsync(admin, new WorkAllocationInput(tickets[4], ids[4], AtWall(10, 9), AtWall(10, 10), Tentative: true));
        pencilled.Status.Should().Be(WorkAllocationStatus.Tentative);
        (await realCapacity.ForPersonAsync(admin, ids[4], Monday.AddDays(10), Monday.AddDays(10))).Days.Single().Should().Match<DayCapacityDto>(d => d.ConfirmedMinutes == 0 && d.TentativeMinutes == 60);
        var confirmed = await plans.ConfirmAsync(admin, pencilled.Id, new WorkAllocationStateInput(pencilled.Version));
        confirmed.Status.Should().Be(WorkAllocationStatus.Planned);
        (await realCapacity.ForPersonAsync(admin, ids[4], Monday.AddDays(10), Monday.AddDays(10))).Days.Single().ConfirmedMinutes.Should().Be(60);
        var backToPencil = await plans.MakeTentativeAsync(admin, pencilled.Id, new WorkAllocationStateInput(confirmed.Version));
        backToPencil.Status.Should().Be(WorkAllocationStatus.Tentative);
        (await db.Tickets.SingleAsync(t => t.Id == tickets[4])).PortalStatus = "CLOSED";
        await db.SaveChangesAsync();
        var finished = () => plans.ConfirmAsync(admin, pencilled.Id, new WorkAllocationStateInput(backToPencil.Version));
        (await finished.Should().ThrowAsync<Desk.Application.Common.ValidationFailedException>()).Which.Message.Should().Be("This ticket is finished; there is nothing left to plan.");

        // Their own ticket already has an hour of confirmed work on the Friday afternoon; the requirement's sums are derived from it.
        await plans.CreateAsync(admin, new WorkAllocationInput(tickets[5], ids[5], AtWall(11, 14), AtWall(11, 15)));
        var need = await plans.SetRequirementAsync(admin, tickets[5], new PlanningRequirementInput(180, AtWall(11, 8), AtWall(12, 18), true, skillId, "Real database"));
        need.RequiredSkillName.Should().NotBeNull();
        (await plans.RequirementAsync(admin, tickets[5])).Should().Match<PlanningRequirementDto>(r => r.RequiredMinutes == 180 && r.ConfirmedMinutes == 60 && r.RemainingMinutes == 120 && r.Splittable);
        (await plans.QueueAsync(admin, new TeamPlanQuery(), 14)).PeopleCounted.Should().BeGreaterThan(0);

        // The preview sees that afternoon work, so its token stays valid while nothing else changes.
        var request = new PlanPreviewInput(tickets[5], ids[5], AtWall(11, 8), AtWall(11, 18), 120, Splittable: true);
        var preview = await plans.PreviewAsync(admin, request);
        preview.Pieces.Should().ContainSingle().Which.Minutes.Should().Be(120);
        // Pieces crafted by hand: the first is fine, the second lands on that confirmed work. Refused, and nothing of the plan is in the database.
        var crafted = new List<PlanPieceDto> { new(AtWall(11, 9), AtWall(11, 10), 60), new(AtWall(11, 14), AtWall(11, 15), 60) };
        var refused = () => plans.ConfirmPreviewAsync(admin, new PlanConfirmInput(request, crafted, preview.PlanToken));
        await refused.Should().ThrowAsync<Desk.Application.Common.ConflictException>();
        (await db.WorkAllocations.CountAsync(a => a.TicketId == tickets[5])).Should().Be(1, "a refused plan writes no piece at all; only the afternoon hour is there");
        var done = await plans.ConfirmPreviewAsync(admin, new PlanConfirmInput(request, preview.Pieces, preview.PlanToken, Note: "On the real engine"));
        done.Allocations.Should().ContainSingle().Which.Note.Should().Be("On the real engine");
        (await db.WorkAllocations.CountAsync(a => a.TicketId == tickets[5])).Should().Be(2);
        (await plans.RequirementAsync(admin, tickets[5])).RemainingMinutes.Should().Be(0);

        // The clock on the real engine: start, pause (a second segment inserted, not updated), resume, stop; one entry per session, held by the database.
        var (_, _, clock) = FullStack(db, admin);
        var session = await clock.StartAsync(admin, new StartWorkInput(tickets[5]));
        _clock.Advance(TimeSpan.FromMinutes(20));
        var paused = await clock.PauseAsync(admin, session.Id, new WorkSessionStateInput(session.Version, WorkPauseReason.WaitingOnClient));
        _clock.Advance(TimeSpan.FromMinutes(10));
        var resumed = await clock.ResumeAsync(admin, session.Id, new WorkSessionStateInput(paused.Version));
        _clock.Advance(TimeSpan.FromMinutes(25));
        (await db.WorkSessionSegments.CountAsync(x => x.SessionId == session.Id)).Should().Be(2, "the resume inserted a second segment");
        var stopped = await clock.StopAsync(admin, session.Id, new StopWorkInput(resumed.Version, "On the real engine"));
        (stopped.Status, stopped.ActiveSeconds, stopped.TimeEntrySyncStatus).Should().Be((WorkSessionStatus.Completed, 45 * 60, TimeEntrySyncStatus.Synced));
        var written = await db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.WorkSessionId == session.Id);
        written.Hours.Should().Be(0.75m);
        var twice = () => db.TicketTimeEntries.Add(new TicketTimeEntry { MspOrganizationId = Org, TicketId = tickets[5], Hours = 0.25m, EntryDate = _clock.GetUtcNow(), WorkSessionId = session.Id, Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Synced });
        twice();
        var saveTwice = () => db.SaveChangesAsync();
        await saveTwice.Should().ThrowAsync<DbUpdateException>("one entry per session is a database rule");
        db.ChangeTracker.Clear();
        // One running clock per person is a database rule too: a second Active row for the same person is refused.
        var another = await clock.StartAsync(admin, new StartWorkInput(tickets[3]));
        db.WorkSessions.Add(new WorkSession { MspOrganizationId = Org, AppUserId = admin, TicketId = tickets[5], Status = WorkSessionStatus.Active, StartedAt = _clock.GetUtcNow(), UpdatedByUserId = admin });
        var secondClock = () => db.SaveChangesAsync();
        await secondClock.Should().ThrowAsync<DbUpdateException>("IX_work_sessions_one_active");
        db.ChangeTracker.Clear();
        await clock.StopAsync(admin, another.Id, new StopWorkInput(another.Version, Discard: true));
        var day = await clock.MyDayAsync(admin, null, Desk.Domain.Common.TimeZones.Resolve(Zone) is var z ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.StartedAt, z).DateTime) : Monday);
        day.Items.Single(i => i.TicketId == tickets[5]).Should().Match<MyDayItemDto>(i => i.ActualSeconds == 45 * 60 && !i.Planned && i.VarianceMinutes == null, "the administrator's own, unplanned work on the real engine");
        (await db.AuditLog.CountAsync(a => a.Action.StartsWith("workforce.session."))).Should().Be(6, "started, paused, resumed, stopped for the first clock; started and cancelled for the second");
        // What finished leaves future plans: on a real database too.
        (await db.Tickets.SingleAsync(t => t.Id == internalWork.TicketId)).PortalStatus = "CLOSED";
        await db.SaveChangesAsync();
        (await new WorkAllocationReleaser(db, new AuditWriter(db, new TestCurrentUser(Org, userId: admin), _tenant, _clock), _clock).ReleaseFinishedAsync())
            .Should().Be(2, "the internal work's ticket and the ticket closed with pencilled-in work on it: tentative work is released like committed work");
    }

    [Fact]
    public async Task The_capacity_migration_adds_one_table_to_a_database_that_has_staff_and_its_down_removes_only_that()
    {
        // Needs a real PostgreSQL server: the migration is written for it. Without one, nothing to check.
        if (Postgres is null) return;
        const string previous = "20261001125607_WorkforceSchedulesAndSkills";
        _tenant.SetTenant(Org);
        var db = NewContext();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // The database as it is in production today: everything up to Phase 1, with people in it.
        await migrator.MigrateAsync(previous);
        var person = new AppUser { MspOrganizationId = Org, DisplayName = "Existing Tech", Email = "existing@techpio.test", IsActive = true };
        var schedule = new WorkSchedule { MspOrganizationId = Org, AppUserId = person.Id, EffectiveFrom = new DateOnly(2025, 12, 1), TimeZone = Zone };
        schedule.Days.Add(new WorkScheduleDay { MspOrganizationId = Org, Day = DayOfWeek.Monday, Start = new TimeOnly(8, 30), End = new TimeOnly(17, 30) });
        db.AddRange(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = Zone }, person, schedule);
        await db.SaveChangesAsync();
        async Task<bool> TableExistsAsync() => await db.Database
            .SqlQuery<int>($"select count(*)::int as \"Value\" from information_schema.tables where table_name = 'capacity_exceptions'").SingleAsync() == 1;
        (await TableExistsAsync()).Should().BeFalse();

        await migrator.MigrateAsync();
        (await TableExistsAsync()).Should().BeTrue();
        (await db.AppUsers.CountAsync()).Should().Be(1, "existing staff are untouched");
        (await db.AppUsers.SingleAsync()).IsSchedulable.Should().BeTrue();
        (await db.WorkScheduleDays.CountAsync()).Should().Be(1, "and so are their schedules");
        db.Add(new CapacityException { MspOrganizationId = Org, AppUserId = person.Id, TimeZone = Zone, AllDay = true, FromDate = Monday, ToDate = Monday, Reason = CapacityExceptionReason.TimeOff });
        await db.SaveChangesAsync();

        // Applying it again changes nothing; taking it back removes the one table and nothing else.
        await migrator.MigrateAsync();
        (await db.CapacityExceptions.CountAsync()).Should().Be(1);
        await migrator.MigrateAsync(previous);
        (await TableExistsAsync()).Should().BeFalse();
        (await db.AppUsers.CountAsync()).Should().Be(1);
        (await db.WorkScheduleDays.CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Postgres is null) return;
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }
}
