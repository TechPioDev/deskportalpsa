using System.Data.Common;
using System.Diagnostics;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// </summary>
public sealed class CapacityPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 1, 5);
    private const string Zone = "Asia/Kolkata";
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

    private DeskDbContext NewContext() => new(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection)
        .AddInterceptors(_counter)
        .ConfigureWarnings(w => w.Throw(
            RelationalEventId.MultipleCollectionIncludeWarning,
            CoreEventId.RowLimitingOperationWithoutOrderByWarning,
            CoreEventId.FirstWithoutOrderByAndFilterWarning)).Options, _tenant, _clock);

    /// <summary>An organization of <paramref name="people"/> technicians: schedules, teams, skills and some time away.</summary>
    private async Task<(DeskDbContext Db, Guid Admin, Guid TeamId, Guid SkillId, List<Guid> People)> SeedAsync(int people)
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        var role = new Role { MspOrganizationId = Org, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        foreach (var key in new[] { Permissions.ScheduleView, Permissions.WorkforceManage, Permissions.AvailabilityManage })
            role.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        var admin = new AppUser { MspOrganizationId = Org, DisplayName = "Admin", Email = "admin@techpio.test", IsActive = true };
        admin.Roles.Add(new UserRole { RoleId = role.Id });
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
        // Measured: 9 for a team day, 10 with a skill filter, 9 for a search, 10 for one person's month,
        // 3 for the calendar itself - at 50, 100 and 500 people alike.
        team.Commands.Should().BeLessThanOrEqualTo(9);
        filtered.Commands.Should().BeLessThanOrEqualTo(10);
        oneDay.Commands.Should().BeLessThanOrEqualTo(9);
        sevenDays.Commands.Should().Be(oneDay.Commands + 1, "seven days cost what one does, plus the one skill-catalogue check this search asked for");
        fourteenDays.Commands.Should().Be(oneDay.Commands);
        month.Commands.Should().BeLessThanOrEqualTo(10);
        everyone.Commands.Should().BeLessThanOrEqualTo(3);

        // Right answers, not just fast ones. Everyone works Monday; a fifth also has an appointment.
        team.Result.People.Should().HaveCount(people + 1);
        team.Result.People.Count(p => p.Day.UsableMinutes == 480).Should().Be(people - (people + 4) / 5);
        team.Result.People.Count(p => p.Day.UnavailableMinutes == 60).Should().Be((people + 4) / 5, "09:30-10:30 UTC is working time wherever this host puts the zone");
        oneDay.Result.Matches.Should().HaveCount(Math.Min(people, 200), "everyone is free 15:00-17:30, and the answer is capped at 200");
        oneDay.Result.PeopleConsidered.Should().Be(people + 1);
        month.Result.Days.Should().HaveCount(30);

        foreach (var ms in new[] { team.Ms, filtered.Ms, oneDay.Ms, sevenDays.Ms, fourteenDays.Ms, month.Ms })
            ms.Should().BeLessThan(15_000);
        everyone.Ms.Should().BeLessThan(30_000);
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
        (await capacity.FindAsync(admin, new AvailabilitySearch(Monday, Monday.AddDays(2), 60, TeamId: teamId, SkillIds: [skillId], AppUserIds: ids))).PeopleConsidered.Should().BeGreaterThan(0);
        var check = await capacity.EvaluateAsync(admin, person, new ProposedWork(
            new DateTimeOffset(Monday.ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero), new DateTimeOffset(Monday.ToDateTime(new TimeOnly(10, 30)), TimeSpan.Zero), SkillIds: [skillId]));
        check.Conflicts.Should().NotBeEmpty();

        await exceptions.RemoveAsync(admin, person, timed.Id);
        (await db.CapacityExceptions.CountAsync(e => e.AppUserId == person)).Should().Be(2);
    }

    public void Dispose() => _connection.Dispose();
}
