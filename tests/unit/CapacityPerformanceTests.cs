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
        /// <summary>The slowest command since the last reset, for the report: which read to look at when a figure grows.</summary>
        public (double Ms, string Sql) Slowest { get; private set; }
        /// <summary>Time the database took to answer, summed: what is left of a read's time is rows travelling and the work in memory.</summary>
        public double DatabaseMs { get; private set; }
        public void Reset() { Commands = 0; Slowest = default; DatabaseMs = 0; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            DatabaseMs += eventData.Duration.TotalMilliseconds;
            if (eventData.Duration.TotalMilliseconds > Slowest.Ms) Slowest = (eventData.Duration.TotalMilliseconds, command.CommandText);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

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
        // One more each than before time entered in the PSA itself was counted as recorded work: the
        // people's PSA logins are read, and where any of them has one, the time filed under it (one more).
        overviewWeek.Commands.Should().BeLessThanOrEqualTo(28);
        overviewMonth.Commands.Should().BeLessThanOrEqualTo(28);
        teamWeek.Commands.Should().BeLessThanOrEqualTo(28);
        person.Commands.Should().BeLessThanOrEqualTo(27);
        drill.Commands.Should().BeLessThanOrEqualTo(27);
        export.Commands.Should().BeLessThanOrEqualTo(30);
        filters.Commands.Should().BeLessThanOrEqualTo(18);
        foreach (var ms in new[] { overviewWeek.Ms, overviewMonth.Ms, teamWeek.Ms, person.Ms, drill.Ms, export.Ms, filters.Ms }) ms.Should().BeLessThan(30_000);

        // The filters through the real translator too (the in-memory provider would not notice one no database can run):
        // a client, a PSA connection, a priority and a kind of work, on a client's ticket finished and worked in the week.
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        var connection = new PsaConnection { MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://psa.example.test", CredentialSecretRef = "ref" };
        var client = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "ABC Company", ExternalCompanyId = "abc" };
        var finishedAt = Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(1).ToDateTime(new TimeOnly(16, 0)), zone, true);
        var psaTicket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.AutotaskPsa, PsaConnectionId = connection.Id, ExternalTicketId = "9001",
            RequesterName = "ABC", RequesterEmail = "it@abc.test", Title = "Client work", PortalStatus = "CLOSED", PortalPriority = "HIGH",
            AssignedAppUserId = ids[1], ClientCompanyId = client.Id, SyncStatus = TicketSyncStatus.Synced, ResolvedAt = finishedAt, ClosedAt = finishedAt,
        };
        db.AddRange(connection, client, psaTicket);
        db.Add(new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = psaTicket.Id, AppUserId = ids[1], Hours = 2m, Billable = true, Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Pending,
            EntryDate = Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(1).ToDateTime(new TimeOnly(14, 0)), zone, true),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var narrowed = await MeasureAsync(() => analytics.OverviewAsync(admin, week with { ClientId = client.Id, Source = "psa:" + connection.Id, Priority = "high", Kind = ActualKindFilter.Reactive }));
        (narrowed.Result.Totals.ActualSeconds, narrowed.Result.Totals.ReactiveActualSeconds, narrowed.Result.Totals.PlannedMinutes, narrowed.Result.Totals.CompletedWork).Should().Be((7200, 7200, 0, 1));
        narrowed.Result.Clients.Should().ContainSingle().Which.Name.Should().Be("ABC Company");
        narrowed.Result.Sources.Should().ContainSingle().Which.Name.Should().Be("Autotask");
        narrowed.Result.Sync.Should().ContainSingle().Which.Connection.Should().Be("Autotask");
        (await analytics.OverviewAsync(admin, week with { Source = "internal" })).Totals.ActualSeconds.Should().Be(people * perPerson * 45 * 60 * 5, "the client's two hours are not the team's own work");
        (await analytics.OverviewAsync(admin, week with { Source = "client" })).Totals.ActualSeconds.Should().Be(7200);
        (await analytics.OverviewAsync(admin, week with { Source = "monitoring" })).Totals.ActualSeconds.Should().Be(0);
        (await analytics.WorkAsync(admin, week with { ClientId = client.Id }, AnalyticsWorkKind.Completed, 0, 50)).Rows.Should().ContainSingle().Which.Reference.Should().Be("Autotask 9001");
        (await analytics.FiltersAsync(admin)).Should().Match<AnalyticsFilterOptionsDto>(o => o.Clients.Count == 1 && o.Priorities.Contains("HIGH") && o.Sources.Any(s => s.Key == "psa:" + connection.Id));
        // The filters add the two existence checks and the caller's ticket scope (a filter on what a ticket says about itself matches only tickets the caller may open), and nothing that grows.
        narrowed.Commands.Should().BeLessThanOrEqualTo(34);
        // A technician's own view with the same kind of filter, so the "may open" subquery of a narrower ticket scope (assigned) is translated too.
        var technician = new Role { MspOrganizationId = Org, Name = "Technician", BuiltInType = RoleType.Technician };
        technician.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAssigned, Scope = PermissionScope.Assigned });
        technician.Permissions.Add(new RolePermission { PermissionKey = Permissions.ScheduleView, Scope = PermissionScope.Own });
        db.Add(technician);
        db.Add(new UserRole { AppUserId = ids[1], RoleId = technician.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var own = Analytics(db, ids[1]);
        var ownClient = await MeasureAsync(() => own.OverviewAsync(ids[1], week with { ClientId = client.Id }));
        (ownClient.Result.People.Count, ownClient.Result.Totals.ActualSeconds, ownClient.Result.Totals.CompletedWork, ownClient.Result.SeesOthers).Should().Be((1, 7200L, 1, false));
        (await own.OverviewAsync(ids[1], week with { Priority = "normal" })).Totals.ActualSeconds.Should().Be(perPerson * 45 * 60 * 5, "their own board ticket");
        (await own.OverviewAsync(ids[1], week)).Totals.ActualSeconds.Should().Be(perPerson * 45 * 60 * 5 + 7200);
        ((Func<Task>)(() => own.TechnicianAsync(ids[1], ids[2], week))).Should().ThrowAsync<Desk.Application.Common.NotFoundException>().GetAwaiter().GetResult();
        output.WriteLine($"{people} people | filtered overview (client + connection + priority + kind): {narrowed.Commands} queries, {narrowed.Ms} ms | a technician's own, by client: {ownClient.Commands} queries, {ownClient.Ms} ms");
    }

    /// <summary>
    /// Phase 8: the forecast for everyone over a week and four weeks, one team, a drill-down, the
    /// comparison with its eight weeks and quality signals, mapping and integration health, and a
    /// report previewed and exported - each a fixed number of queries however many people, days or
    /// hours there are, and every query through a real SQL translator.
    /// </summary>
    [Theory]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    [InlineData(500, 3)]
    public async Task Management_insights_and_reports_cost_the_same_number_of_queries_whatever_the_number_of_people_days_or_hours(int people, int perPerson)
    {
        var (db, admin, teamId, skillId, ids) = await SeedAsync(people);
        var (boardId, tickets, allocations) = await SeedAllocationsAsync(db, admin, ids, perPerson);
        var entries = await SeedTimeAsync(db, ids, tickets, perPerson);
        // Every ticket is sized at 100 hours and asks for a skill; one more is routed to a team and held by nobody;
        // a PSA connection and a recurring ticket exist; the administrator may read integration health and manage boards.
        var role = await db.Roles.AsNoTracking().SingleAsync();
        db.Add(new RolePermission { RoleId = role.Id, PermissionKey = Permissions.IntegrationHealthView, Scope = PermissionScope.All });
        db.Add(new RolePermission { RoleId = role.Id, PermissionKey = Permissions.BoardsManage, Scope = PermissionScope.All });
        foreach (var t in tickets) db.Add(new WorkPlanning { MspOrganizationId = Org, TicketId = t, RequiredMinutes = 6000, Splittable = true, RequiredSkillId = skillId, UpdatedByUserId = admin });
        var routed = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = boardId, Number = "INT-900001", RequesterName = "Admin", RequesterEmail = "admin@techpio.test",
            Title = "Routed to a team", PortalStatus = "NEW", AssignedTeamId = teamId, SyncStatus = TicketSyncStatus.Synced,
        };
        var zone = Desk.Domain.Common.TimeZones.Resolve(Zone);
        var connection = new PsaConnection { MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://psa.example.test", CredentialSecretRef = "ref", Status = ConnectionStatus.Healthy };
        var client = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = connection.Id, Name = "Unknown", ExternalCompanyId = "unknown" };
        var psaTicket = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.AutotaskPsa, PsaConnectionId = connection.Id, ExternalTicketId = "9001", AssignedTechnicianExternalId = "tech-9",
            RequesterName = "ABC", RequesterEmail = "it@abc.test", Title = "Client work", PortalStatus = "Waiting Vendor", PortalPriority = "HIGH",
            AssignedAppUserId = ids[1], ClientCompanyId = client.Id, SyncStatus = TicketSyncStatus.Error, SlaDueAt = Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(1).ToDateTime(new TimeOnly(17, 0)), zone, true),
        };
        db.AddRange(routed, connection, client, psaTicket,
            new WorkPlanning { MspOrganizationId = Org, TicketId = routed.Id, RequiredMinutes = 300, UpdatedByUserId = admin },
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = ids[1], PsaConnectionId = connection.Id, ExternalTechnicianId = "tech-9" },
            new RecurringTicket { MspOrganizationId = Org, BoardId = boardId, Title = "Weekly check", Frequency = RecurrenceFrequency.Weekly, DayOfWeek = 1, Hour = 9, CreatedByUserId = admin, AssignedAppUserId = ids[0] },
            new TicketTimeEntry
            {
                MspOrganizationId = Org, TicketId = psaTicket.Id, AppUserId = ids[1], Hours = 1m, Billable = true, Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Failed,
                EntryDate = Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(1).ToDateTime(new TimeOnly(18, 0)), zone, true),
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var insights = Analytics(db, admin);
        var week = new InsightsQuery("custom", Monday, Monday.AddDays(6));
        var month = new InsightsQuery("custom", Monday, Monday.AddDays(27));

        var forecastWeek = await MeasureAsync(() => insights.ForecastAsync(admin, week));
        var (slowest, databaseMs) = (_counter.Slowest, _counter.DatabaseMs);
        var forecastMonth = await MeasureAsync(() => insights.ForecastAsync(admin, month));
        var forecastTeam = await MeasureAsync(() => insights.ForecastAsync(admin, week with { TeamId = teamId }));
        var drill = await MeasureAsync(() => insights.ForecastWorkAsync(admin, week, ForecastWorkKind.Unscheduled, 0, 50));
        var skillList = await MeasureAsync(() => insights.ForecastWorkAsync(admin, week with { SkillId = skillId }, ForecastWorkKind.Skill, 0, 50));
        var health = await MeasureAsync(() => insights.HealthAsync(admin));
        var catalogue = await MeasureAsync(() => insights.CatalogueAsync(admin));
        var preview = await MeasureAsync(() => insights.ReportAsync(admin, "future-capacity", month));
        var export = await MeasureAsync(() => insights.ExportReportAsync(admin, "future-capacity", month, "xlsx"));
        var filtered = await MeasureAsync(() => insights.ForecastAsync(admin, week with { ClientId = client.Id, Source = "psa:" + connection.Id, Priority = "high" }));

        output.WriteLine($"{people} people, {allocations} allocations, {entries} time entries | forecast week: {forecastWeek.Commands} queries, {forecastWeek.Ms} ms | forecast four weeks: {forecastMonth.Commands} queries, {forecastMonth.Ms} ms | team week: {forecastTeam.Commands} queries, {forecastTeam.Ms} ms ({forecastTeam.Result.People.Count} people)");
        output.WriteLine($"{people} people | the week's forecast spent {databaseMs:0} ms waiting for the database | slowest query: {slowest.Ms:0} ms: {OneLine(slowest.Sql, 160)}");
        output.WriteLine($"{people} people | drill-down: {drill.Commands} queries, {drill.Ms} ms ({drill.Result.Total} rows) | skill list: {skillList.Commands} queries, {skillList.Ms} ms | health: {health.Commands} queries, {health.Ms} ms | catalogue: {catalogue.Commands} queries | report preview: {preview.Commands} queries, {preview.Ms} ms | report xlsx: {export.Commands} queries, {export.Ms} ms ({export.Result.Content.Length / 1024} KB) | filtered forecast: {filtered.Commands} queries, {filtered.Ms} ms");

        // Right at scale. Every weekday holds perPerson planned hours for every person; every ticket is 100 hours less the four weeks planned on it.
        var remaining = 6000 - perPerson * 60 * 20;
        var fc = forecastWeek.Result;
        fc.People.Should().HaveCount(people + 1);
        (fc.Totals.ConfirmedMinutes, fc.Totals.TentativeMinutes).Should().Be((people * perPerson * 60 * 5, 0));
        (fc.Totals.UnscheduledMinutes, fc.Totals.UnscheduledItems, fc.Totals.UnestimatedItems).Should().Be((people * remaining + 300, people + 1, 1), "everyone's ticket, the one routed to a team, and the client's unsized one");
        fc.Totals.ProjectedMinutes.Should().Be(fc.Totals.ConfirmedMinutes + fc.Totals.UnscheduledMinutes);
        fc.Totals.GapMinutes.Should().Be(fc.Totals.CapacityMinutes - fc.Totals.ProjectedMinutes);
        fc.People.Sum(p => p.Figures.ConfirmedMinutes).Should().Be(fc.Totals.ConfirmedMinutes, "the rows add up to the total");
        (fc.People.Sum(p => p.Figures.UnscheduledMinutes) + fc.Unassigned!.UnscheduledMinutes).Should().Be(fc.Totals.UnscheduledMinutes);
        fc.Daily.Sum(d => d.ConfirmedMinutes).Should().Be(fc.Totals.ConfirmedMinutes);
        fc.Daily.Sum(d => d.CapacityMinutes ?? 0).Should().Be(fc.Totals.CapacityMinutes);
        // The same capacity and confirmed demand as the Phase 7 dashboard for the same days.
        var phase7Week = await MeasureAsync(() => insights.OverviewAsync(admin, new AnalyticsQuery("custom", Monday, Monday.AddDays(6))));
        output.WriteLine($"{people} people | for comparison, the Phase 7 dashboard for the same week: {phase7Week.Commands} queries, {phase7Week.Ms} ms, {_counter.DatabaseMs:0} ms of it waiting for the database");
        var phase7 = phase7Week.Result;
        (fc.Totals.CapacityMinutes, fc.Totals.ConfirmedMinutes).Should().Be((phase7.Demand.AvailableMinutes, phase7.Demand.ConfirmedMinutes));
        forecastMonth.Result.Totals.ConfirmedMinutes.Should().Be(people * perPerson * 60 * 20);
        forecastMonth.Result.Totals.UnscheduledMinutes.Should().Be(fc.Totals.UnscheduledMinutes, "what is left to allocate does not depend on the window");
        fc.Skills.Should().ContainSingle().Which.Should().Match<SkillCapacityDto>(k => k.DemandMinutes == people * remaining && k.DemandItems == people && k.SkilledPeople == (people + 3) / 4);
        (fc.Recurring!.Definitions, fc.Recurring.Occurrences, forecastMonth.Result.Recurring!.Occurrences).Should().Be((1, 1, 4));
        fc.AtRisk.Should().Be(new WorkAtRiskDto(0, 0, 1), "the client's ticket is due on Tuesday and has no estimate, so nothing says it cannot fit");
        (fc.CanSeeHealth, fc.Attention.Any(a => a.Key == "time-not-in-psa"), fc.Attention.Any(a => a.Key.StartsWith("sync:"))).Should().Be((true, true, true), "the connection has never synced");
        forecastTeam.Result.People.Should().HaveCountLessThan(people + 1);
        forecastTeam.Result.Unassigned!.UnscheduledMinutes.Should().Be(300);
        (drill.Result.Total, drill.Result.TotalMinutes, drill.Result.Rows.Count).Should().Be((people + 1, fc.Totals.UnscheduledMinutes, 50));
        skillList.Result.TotalMinutes.Should().Be(people * remaining);
        (filtered.Result.Totals.UnestimatedItems, filtered.Result.Totals.ConfirmedMinutes, filtered.Result.Clients.Single().Name).Should().Be((1, 0, "Unknown"));
        var row = health.Result.Connections.Should().ContainSingle().Subject;
        (row.Tickets, row.StatusMapping.Mapped, row.StatusMapping.Unmapped.Single(), row.PriorityMapping.Percent, row.TechnicianLinks.Mapped, row.TechnicianLinks.Total, row.PlaceholderClientTickets, row.TicketsInSyncError, row.TimeEntriesFailed, row.Stale)
            .Should().Be((1, 0, "Waiting Vendor", 100d, 1, 1, 1, 1, 1, true));
        catalogue.Result.Should().HaveCount(12);
        (preview.Result.TotalRows, preview.Result.Truncated, preview.Result.Rows.Count).Should().Be((people + 2, people + 2 > WorkforceAnalyticsService.ReportPreviewRows, Math.Min(people + 2, WorkforceAnalyticsService.ReportPreviewRows)));
        export.Result.Rows.Should().Be(people + 2, "the export holds every row, whatever the preview shows");

        // History, read on the Monday after the four weeks: last week against the one before, eight weeks, quality signals.
        _clock.Advance(Desk.Domain.Common.TimeZones.WallToUtc(Monday.AddDays(28).ToDateTime(new TimeOnly(9, 0)), zone, true) - _clock.GetUtcNow());
        var trends = await MeasureAsync(() => insights.TrendsAsync(admin, new InsightsQuery(Compare: "last-week")));
        var trendsMonth = await MeasureAsync(() => insights.TrendsAsync(admin, new InsightsQuery(Compare: "last-30")));
        var quality = await MeasureAsync(() => insights.ExportReportAsync(admin, "operational-quality", new InsightsQuery(Compare: "last-30"), "csv"));
        output.WriteLine($"{people} people | trends last week: {trends.Commands} queries, {trends.Ms} ms | trends last 30 days: {trendsMonth.Commands} queries, {trendsMonth.Ms} ms | quality report csv: {quality.Commands} queries, {quality.Ms} ms");
        var actualWeek = (double)people * perPerson * 45 * 60 * 5;
        trends.Result.Totals.Single(x => x.Key == "actual").Should().Match<ComparisonDto>(x => x.Current == actualWeek && x.Previous == actualWeek && x.Change == 0 && x.ChangePercent == 0);
        trends.Result.Weeks.Should().HaveCount(8);
        trends.Result.Weeks.Sum(x => x.ActualSeconds).Should().Be((long)actualWeek * 4 + 3600, "four weeks of work and the hour on the client's ticket");
        trends.Result.Quality.Should().HaveCount(7);

        // Constant whatever the size: the Phase 7 load, the open work, the tickets it points at, skills, recurring work,
        // failed pushes, connections and permissions, then memory. Measured at 50, 100 and 500 people alike.
        forecastWeek.Commands.Should().BeLessThanOrEqualTo(MaxForecast);
        forecastMonth.Commands.Should().BeLessThanOrEqualTo(MaxForecast);
        forecastTeam.Commands.Should().BeLessThanOrEqualTo(MaxForecast);
        drill.Commands.Should().BeLessThanOrEqualTo(MaxForecast);
        skillList.Commands.Should().BeLessThanOrEqualTo(MaxForecast);
        filtered.Commands.Should().BeLessThanOrEqualTo(MaxForecast + 6);
        health.Commands.Should().BeLessThanOrEqualTo(MaxHealth);
        catalogue.Commands.Should().BeLessThanOrEqualTo(2);
        preview.Commands.Should().BeLessThanOrEqualTo(MaxForecast + 4);
        export.Commands.Should().BeLessThanOrEqualTo(MaxForecast + 6);
        trends.Commands.Should().BeLessThanOrEqualTo(MaxTrends);
        trendsMonth.Commands.Should().BeLessThanOrEqualTo(MaxTrends);
        quality.Commands.Should().BeLessThanOrEqualTo(MaxTrends + 6);
        foreach (var ms in new[] { forecastWeek.Ms, forecastMonth.Ms, forecastTeam.Ms, drill.Ms, health.Ms, preview.Ms, export.Ms, trends.Ms, trendsMonth.Ms, quality.Ms }) ms.Should().BeLessThan(30_000);
    }

    /// <summary>A command's text on one line, shortened: enough to recognise the read in a test report.</summary>
    private static string OneLine(string sql, int max)
    {
        var line = string.Join(' ', (sql ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..max] + "…";
    }

    // Two more than before PSA-entered time was counted: the period and the one it is compared with each read the people's PSA logins.
    private const int MaxForecast = 55;
    private const int MaxHealth = 12;
    private const int MaxTrends = 30;

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
        // One more each for a day and a team's day: the PSA logins of the people shown.
        myDay.Commands.Should().BeLessThanOrEqualTo(28);
        // The active lookup reads the day the clock started on once (the outside-the-schedule fact): the session plus one capacity day.
        active.Commands.Should().BeLessThanOrEqualTo(17);
        teamToday.Commands.Should().BeLessThanOrEqualTo(28);

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
    public async Task Many_sync_runs_started_at_once_on_a_real_database_end_with_one()
    {
        // The worker and an administrator's "Sync now", in two processes, at the same instant. Each
        // reads "nothing is running" and each tries to record its run; the index lets one through.
        // Needs PostgreSQL for the same reason as the booking test above: one shared SQLite
        // connection cannot carry two requests at once.
        if (Postgres is null) return;
        var (db, _, _, _, _) = await SeedAsync(1);
        var connection = new Desk.Domain.Tenancy.PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://x", CredentialSecretRef = "m",
        };
        db.Add(connection);
        await db.SaveChangesAsync();

        var contexts = Enumerable.Range(0, 8).Select(_ => NewContext()).ToList();
        try
        {
            var started = await Task.WhenAll(contexts.Select(c => Task.Run(async () =>
            {
                var mine = await c.PsaConnections.FirstAsync(x => x.Id == connection.Id);
                return await new Desk.Infrastructure.Sync.SyncRunCoordinator(c, _clock, Desk.Infrastructure.Sync.SyncOptions.Default)
                    .TryStartAsync(mine, Desk.Domain.Sync.SyncRunTrigger.Scheduled, null, default);
            })));

            started.Count(r => r is not null).Should().Be(1, "one run holds the connection; the other seven stand down");
            (await db.SyncRuns.AsNoTracking().CountAsync(r => r.PsaConnectionId == connection.Id && r.Status == Desk.Domain.Sync.SyncRunStatus.Running)).Should().Be(1);
            output.WriteLine("sync lock: 8 simultaneous starts on PostgreSQL, 1 run");
        }
        finally
        {
            foreach (var c in contexts) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task Many_workers_taking_the_same_jobs_on_a_real_database_run_each_one_once()
    {
        // Six workers, each with all thirty jobs in hand as it read them: queued. Every one of them
        // then tries to take every job. A job's version is checked by the save that takes it, so
        // each job has exactly one taker and the other five saves are refused - a hundred and fifty
        // refusals, whatever the order the workers happen to run in.
        // Needs PostgreSQL for the same reason as the tests above: one shared SQLite connection
        // cannot carry six workers at once.
        if (Postgres is null) return;
        var (db, _, _, _, _) = await SeedAsync(1);
        var jobs = Enumerable.Range(0, 30)
            .Select(_ => new Desk.Domain.Sync.BackgroundJob { MspOrganizationId = Org, JobType = "t", PayloadJson = "{}", Status = BackgroundJobStatus.Queued })
            .ToList();
        db.AddRange(jobs);
        await db.SaveChangesAsync();
        var ids = jobs.Select(j => j.Id).ToList();

        var workers = Enumerable.Range(0, 6).Select(_ => NewContext()).ToList();
        try
        {
            // What each worker read before any of them saved: the moment that mattered.
            foreach (var w in workers)
                (await w.BackgroundJobs.ToListAsync()).Should().HaveCount(30).And.OnlyContain(j => j.Status == BackgroundJobStatus.Queued);

            var taken = await Task.WhenAll(workers.Select((w, k) => Task.Run(async () =>
            {
                var queue = new Desk.Infrastructure.Jobs.JobQueue(w, _clock);
                var mine = new List<Guid>();
                // Each starts somewhere else in the list, so they meet from the first job on.
                foreach (var id in ids.Skip(k * 5).Concat(ids.Take(k * 5)))
                    if (await queue.ClaimAsync(id) is { } job) mine.Add(job.Id);
                return mine;
            })));

            taken.SelectMany(t => t).Should().OnlyHaveUniqueItems("no job was taken by two workers").And.HaveCount(30, "and every job was taken by one");
            var rows = await db.BackgroundJobs.AsNoTracking().Select(j => new { j.Status, j.Attempts, j.Version }).ToListAsync();
            rows.Should().OnlyContain(j => j.Status == BackgroundJobStatus.Running && j.Attempts == 1 && j.Version == 1);
            output.WriteLine($"job queue: 6 workers each tried all 30 jobs on PostgreSQL; taken {string.Join(" + ", taken.Select(t => t.Count))} = 30, each once, {6 * 30 - 30} saves refused");
        }
        finally
        {
            foreach (var w in workers) await w.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ticket_visibility_through_PSA_links_runs_on_a_real_database()
    {
        // The predicate that decides which tickets a person may see is assembled by hand, one clause
        // per PSA connection. SQLite translating it does not prove PostgreSQL does, and this is the
        // query every ticket list, count and detail goes through.
        var (db, _, teamId, _, ids) = await SeedAsync(6);
        var (me, teammate) = (ids[0], ids[5]);   // people 0 and 5 share Team 0
        Desk.Domain.Tenancy.PsaConnection Account(string name) => new()
        {
            MspOrganizationId = Org, Name = name, Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "m", DefaultTimeEntryResourceId = "api-user",
        };
        var (first, second) = (Account("Autotask - A"), Account("Autotask - B"));
        Ticket Psa(Guid connection, string external, string login) => new()
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, Provider = ProviderType.AutotaskPsa, PsaConnectionId = connection,
            ExternalTicketId = external, RequesterName = "R", RequesterEmail = "r@x.test", Title = external,
            PortalStatus = "NEW", PortalPriority = "NORMAL", AssignedTechnicianExternalId = login, SyncStatus = TicketSyncStatus.Synced,
        };
        var assigned = new Role { MspOrganizationId = Org, Name = "Technician" };
        assigned.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAssigned, Scope = PermissionScope.Assigned });
        var byTeam = new Role { MspOrganizationId = Org, Name = "Team lead" };
        byTeam.Permissions.Add(new RolePermission { PermissionKey = Permissions.TicketsViewAll, Scope = PermissionScope.Team });
        db.AddRange(first, second, assigned, byTeam,
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = me, PsaConnectionId = first.Id, ExternalTechnicianId = "5" },
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = me, PsaConnectionId = second.Id, ExternalTechnicianId = "812" },
            new UserPsaIdentity { MspOrganizationId = Org, AppUserId = teammate, PsaConnectionId = first.Id, ExternalTechnicianId = "7" },
            Psa(first.Id, "mine-on-A", "5"), Psa(second.Id, "mine-on-B", "812"), Psa(second.Id, "someone-elses-5-on-B", "5"),
            Psa(first.Id, "teammates-on-A", "7"), Psa(first.Id, "the-integrations", "api-user"));
        await db.SaveChangesAsync();

        async Task<List<string>> SeenAsync(Role role)
        {
            db.UserRoles.RemoveRange(await db.UserRoles.Where(r => r.AppUserId == me).ToListAsync());
            db.UserRoles.Add(new UserRole { AppUserId = me, RoleId = role.Id });
            await db.SaveChangesAsync();
            var visible = await new Desk.Infrastructure.Tickets.TicketScopeQuery(db, new EffectivePermissionService(db))
                .VisibleAsync(db.Tickets.AsNoTracking(), me, Permissions.TicketsViewAll);
            return await visible.Where(t => t.Origin == TicketOrigin.Psa).Select(t => t.Title).OrderBy(t => t).ToListAsync();
        }

        (await SeenAsync(assigned)).Should().Equal("mine-on-A", "mine-on-B");
        (await SeenAsync(byTeam)).Should().Equal("mine-on-A", "mine-on-B", "teammates-on-A");
        output.WriteLine($"ticket visibility through PSA links: {(Postgres is null ? "SQLite" : "PostgreSQL")}, team {teamId}");
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
