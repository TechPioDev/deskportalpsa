using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Capacity, exceptions, the availability search and conflict checks through the services, with
/// permissions resolved from the database exactly as in production: who may see whose capacity, who
/// may record time away, and that another organization's people are never reachable or even
/// detectable.
/// </summary>
public class WorkforceCapacityTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    // The test clock starts on Thursday 1 Jan 2026 (UTC); the first Monday is the 5th.
    private static readonly DateOnly Today = new(2026, 1, 1);
    private static readonly DateOnly Monday = new(2026, 1, 5);
    private static readonly DateOnly Tuesday = new(2026, 1, 6);
    private static readonly DateOnly Saturday = new(2026, 1, 10);
    private const string Zone = "Asia/Kolkata";
    // Whatever this host resolves the stored zone to (IST on the servers and CI; UTC on a Windows
    // machine in invariant mode) - expected instants are built the same way, so they agree everywhere.
    private static readonly TimeZoneInfo Tz = TimeZones.Resolve(Zone);

    private static DateTimeOffset At(DateOnly date, string hm) => TimeZones.WallToUtc(date.ToDateTime(TimeOnly.Parse(hm)), Tz, true);
    private static Interval Span(DateOnly date, string from, string to) => new(At(date, from), At(date, to));

    /// <summary>Planned work, as the allocation phase will supply it.</summary>
    private sealed class FakeAllocations : IWorkAllocationReader
    {
        public List<AllocatedSpan> Spans { get; } = [];
        public int Calls { get; private set; }

        public void Book(AppUser who, DateOnly date, string from, string to, bool confirmed = true, Guid? workId = null)
            => Spans.Add(new AllocatedSpan(who.Id, Span(date, from, to), confirmed, workId));

        public Task<IReadOnlyList<AllocatedSpan>> ForAsync(Guid callerId, IReadOnlyCollection<Guid> appUserIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AllocatedSpan>>(Spans.Where(s => appUserIds.Contains(s.AppUserId) && s.When.Start < to && s.When.End > from).ToList());
        }
    }

    private sealed record Services(CapacityService Capacity, CapacityExceptionService Exceptions, WorkScheduleService Schedules, SkillService Skills);

    private sealed record World(DeskDbContext Db, string DbName, AppUser Admin, AppUser Lead, AppUser Jason, AppUser Abbie, AppUser Sam, AppUser Outsider,
        Team Noc, Team Security, Department Operations, Role TechRole, TestClock Clock, FakeAllocations Work)
    {
        public Services As(AppUser who) => For(Db, OrgA, who);

        public Services For(DeskDbContext db, Guid org, AppUser who)
        {
            var tenant = new Desk.Infrastructure.Tenancy.TenantContext();
            tenant.SetTenant(org);
            var user = new TestCurrentUser(org, userId: who.Id, name: who.DisplayName);
            var access = new WorkforceAccess(db, tenant, new EffectivePermissionService(db));
            var audit = new AuditWriter(db, user, tenant, Clock);
            return new Services(new CapacityService(db, access, Work, Clock), new CapacityExceptionService(db, access, audit, Clock),
                new WorkScheduleService(db, access, audit, Clock), new SkillService(db, access, tenant, user, audit));
        }
    }

    private static async Task<World> WorldAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = AdminHarness.Create(OrgA, dbName);
        var db = h.Db;
        Role R(string name, RoleType type, params (string Key, PermissionScope Scope)[] perms)
        {
            var role = new Role { MspOrganizationId = OrgA, Name = name, BuiltInType = type };
            foreach (var (k, s) in perms) role.Permissions.Add(new RolePermission { PermissionKey = k, Scope = s });
            db.Roles.Add(role);
            return role;
        }
        var admin = R("Administrator", RoleType.MspAdministrator, (Permissions.ScheduleView, PermissionScope.All),
            (Permissions.WorkforceManage, PermissionScope.All), (Permissions.AvailabilityManage, PermissionScope.All));
        var lead = R("Team lead", RoleType.Manager, (Permissions.ScheduleView, PermissionScope.Team), (Permissions.AvailabilityManage, PermissionScope.Team));
        var tech = R("Technician", RoleType.Technician, (Permissions.ScheduleView, PermissionScope.Own));
        AppUser U(string name, Role role, Guid? org = null)
        {
            var u = new AppUser { MspOrganizationId = org ?? OrgA, DisplayName = name, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@techpio.test", IsActive = true };
            u.Roles.Add(new UserRole { RoleId = role.Id });
            db.AppUsers.Add(u);
            return u;
        }
        var dept = new Department { MspOrganizationId = OrgA, Name = "Operations" };
        var noc = new Team { MspOrganizationId = OrgA, Department = dept, Name = "NOC" };
        var security = new Team { MspOrganizationId = OrgA, Department = dept, Name = "Security" };
        var w = new World(db, dbName, U("Harpal Admin", admin), U("Lena Lead", lead), U("Jason Carter", tech), U("Abbie Noor", tech), U("Sam Shah", tech),
            U("Other Org", tech, OrgB), noc, security, dept, tech, h.Clock, new FakeAllocations());
        db.AddRange(dept, noc, security);
        db.MspOrganizations.Add(new MspOrganization { Id = OrgA, Name = "TechPio", Slug = "techpio", TimeZone = Zone });
        db.UserTeams.AddRange(
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Lead.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Abbie.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Sam.Id, TeamId = security.Id });
        await db.SaveChangesAsync();
        return w;
    }

    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static WorkScheduleInput DayShift(DateOnly? from = null, string start = "08:30", string end = "17:30") => new(from, Zone,
        Weekdays.Select(d => new WorkDayInput(d, start, end, [new WorkBreakDto("12:30", "13:30")])).ToList());

    private static WorkScheduleInput NightShift() => new(null, Zone,
        Weekdays.Select(d => new WorkDayInput(d, "18:00", "03:00", [new WorkBreakDto("22:00", "22:30")])).ToList());

    /// <summary>Jason, Abbie and Sam on the day shift - the usual starting point.</summary>
    private static async Task<World> WorldWithDayShiftsAsync()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        foreach (var p in new[] { w.Jason, w.Abbie, w.Sam }) await admin.Schedules.SaveAsync(w.Admin.Id, p.Id, DayShift());
        return w;
    }

    private static CapacityExceptionInput Away(DateOnly date, string from, string to, CapacityExceptionReason reason = CapacityExceptionReason.Appointment, string? note = null)
        => new(CapacityExceptionKind.Unavailable, false, date, null, from, to, reason, note);

    private static CapacityExceptionInput AwayAllDay(DateOnly from, DateOnly? to = null)
        => new(CapacityExceptionKind.Unavailable, true, from, to, null, null, CapacityExceptionReason.TimeOff, null);

    private static CapacityExceptionInput Extra(DateOnly date, string from, string to)
        => new(CapacityExceptionKind.AdditionalAvailability, false, date, null, from, to, CapacityExceptionReason.Other, null);

    private static async Task<DayCapacityDto> DayAsync(World w, AppUser viewer, AppUser person, DateOnly date)
        => (await w.As(viewer).Capacity.ForPersonAsync(viewer.Id, person.Id, date, date)).Days.Single();

    // ---- FLOW 1: own capacity ------------------------------------------------------------------

    [Fact]
    public async Task A_technician_reads_their_own_capacity_for_today()
    {
        var w = await WorldWithDayShiftsAsync();

        var mine = await w.As(w.Jason).Capacity.ForPersonAsync(w.Jason.Id, w.Jason.Id, null, null);

        mine.Today.Should().Be(Today);
        mine.TimeZone.Should().Be(Zone);
        mine.HasSchedule.Should().BeTrue();
        mine.CanManageExceptions.Should().BeFalse("a technician does not record time away unless their role is given that");
        var day = mine.Days.Should().ContainSingle().Subject;
        (day.Date, day.IsWorkingDay, day.GrossMinutes, day.BreakMinutes, day.UsableMinutes).Should().Be((Today, true, 540, 60, 480));
        (day.ConfirmedMinutes, day.TentativeMinutes, day.RemainingConfirmedMinutes, day.ProjectedRemainingMinutes).Should().Be((0, 0, 480, 480));
        day.FreeSlots.Select(s => (s.Start, s.End, s.Minutes)).Should().Equal(
            (At(Today, "08:30"), At(Today, "12:30"), 240), (At(Today, "13:30"), At(Today, "17:30"), 240));
        (day.WindowStart, day.WindowEnd).Should().Be((At(Today, "08:30"), At(Today, "17:30")));
    }

    [Fact]
    public async Task A_technician_cannot_read_a_colleagues_capacity()
    {
        var w = await WorldWithDayShiftsAsync();
        var act = () => w.As(w.Jason).Capacity.ForPersonAsync(w.Jason.Id, w.Abbie.Id, null, null);
        (await act.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
    }

    [Fact]
    public async Task A_week_shows_each_working_day_and_nothing_at_the_weekend()
    {
        var w = await WorldWithDayShiftsAsync();
        var week = await w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Monday, Monday.AddDays(6));
        week.Days.Select(d => d.UsableMinutes).Should().Equal(480, 480, 480, 480, 480, 0, 0);
        week.Days.Select(d => d.IsWorkingDay).Should().Equal(true, true, true, true, true, false, false);
    }

    [Fact]
    public async Task Someone_with_no_schedule_has_no_capacity_and_says_so()
    {
        var w = await WorldAsync();
        var none = await w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Monday, Monday);
        none.HasSchedule.Should().BeFalse();
        none.TimeZone.Should().Be(Zone, "without a schedule their dates are read in the organization's zone");
        none.Days.Single().UsableMinutes.Should().Be(0);
    }

    [Fact]
    public async Task A_schedule_change_from_a_later_day_shows_on_the_days_it_applies_to()
    {
        var w = await WorldWithDayShiftsAsync();
        await w.As(w.Admin).Schedules.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift(from: Tuesday, start: "10:00", end: "19:00"));

        var days = (await w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Monday, Tuesday)).Days;
        (days[0].WindowStart, days[0].WindowEnd).Should().Be((At(Monday, "08:30"), At(Monday, "17:30")));
        (days[1].WindowStart, days[1].WindowEnd).Should().Be((At(Tuesday, "10:00"), At(Tuesday, "19:00")));
    }

    [Theory]
    [InlineData(0, 31, "Ask for at most 31 days at a time.")]
    [InlineData(3, -1, "The last date is before the first.")]
    [InlineData(900, 1, "Choose dates within a year of today.")]
    public async Task A_capacity_request_is_limited_to_a_sensible_range(int fromOffset, int length, string message)
    {
        var w = await WorldWithDayShiftsAsync();
        var act = () => w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Today.AddDays(fromOffset), Today.AddDays(fromOffset + length));
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage(message);
    }

    // ---- FLOWS 2 and 3: exceptions -------------------------------------------------------------

    [Fact]
    public async Task A_part_day_exception_takes_exactly_its_working_time_off_and_removing_it_gives_it_back()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);

        var added = await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "15:00", "16:00", note: "Dentist"));

        (added.StartTime, added.EndTime, added.TimeZone, added.AllDay, added.UpdatedBy).Should().Be(("15:00", "16:00", Zone, false, "Harpal Admin"));
        (added.StartsAt, added.EndsAt).Should().Be((At(Monday, "15:00"), At(Monday, "16:00")));
        var day = await DayAsync(w, w.Admin, w.Jason, Monday);
        (day.UnavailableMinutes, day.UsableMinutes, day.RemainingConfirmedMinutes).Should().Be((60, 420, 420));
        day.FreeSlots.Select(s => s.Minutes).Should().Equal(240, 90, 90);
        day.Exceptions.Should().ContainSingle().Which.Note.Should().Be("Dentist");

        await admin.Exceptions.RemoveAsync(w.Admin.Id, w.Jason.Id, added.Id);
        (await DayAsync(w, w.Admin, w.Jason, Monday)).UsableMinutes.Should().Be(480);

        (await w.Db.AuditLog.Where(a => a.Action.StartsWith("workforce.exception.")).Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(["workforce.exception.added", "workforce.exception.removed"]);
        var detail = System.Text.Json.JsonDocument.Parse((await w.Db.AuditLog.SingleAsync(a => a.Action == "workforce.exception.added")).DetailJson!).RootElement;
        detail.GetProperty("exception").GetString().Should().Be($"Unavailable · 5 Jan 2026 15:00–16:00 ({Zone})");
        // The audit trail says when, never why: anyone who may read audit entries would otherwise
        // read "Dentist" too.
        (await w.Db.AuditLog.Where(a => a.Action.StartsWith("workforce.exception.")).Select(a => a.DetailJson).ToListAsync())
            .Should().OnlyContain(json => !json!.Contains("Dentist") && !json.Contains("Appointment"));
    }

    [Fact]
    public async Task A_full_day_exception_leaves_no_capacity_and_no_free_slots()
    {
        var w = await WorldWithDayShiftsAsync();
        await w.As(w.Admin).Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Monday, Tuesday));

        var days = (await w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Monday, Tuesday.AddDays(1))).Days;
        days.Take(2).Should().OnlyContain(d => d.UnavailableAllDay && d.UsableMinutes == 0 && d.RemainingConfirmedMinutes == 0 && d.FreeSlots.Count == 0);
        days[2].UsableMinutes.Should().Be(480, "Wednesday is not part of the time off");
    }

    [Fact]
    public async Task Overlapping_exceptions_are_taken_off_once()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:30"));
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "11:00", "12:00"));

        var day = await DayAsync(w, w.Admin, w.Jason, Monday);
        (day.UnavailableMinutes, day.UsableMinutes).Should().Be((120, 360));
    }

    [Fact]
    public async Task Extra_availability_adds_capacity_even_on_a_day_off()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Extra(Saturday, "10:00", "14:00"));
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Extra(Monday, "17:00", "19:30"));

        var saturday = await DayAsync(w, w.Admin, w.Jason, Saturday);
        (saturday.IsWorkingDay, saturday.AdditionalMinutes, saturday.UsableMinutes).Should().Be((false, 240, 240));
        // 17:00-17:30 is already working time: only the two hours after the window are added.
        var monday = await DayAsync(w, w.Admin, w.Jason, Monday);
        (monday.AdditionalMinutes, monday.UsableMinutes).Should().Be((120, 600));
        monday.FreeSlots.Last().Should().Match<SlotDto>(s => s.Start == At(Monday, "13:30") && s.End == At(Monday, "19:30"));
    }

    [Fact]
    public async Task An_exception_is_checked_completely_and_every_problem_is_reported_at_once()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);

        var act = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, new CapacityExceptionInput(
            CapacityExceptionKind.AdditionalAvailability, true, Today.AddDays(800), null, null, null, (CapacityExceptionReason)99, new string('x', 201)));
        var error = (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message;
        error.Should().Contain("Choose a reason from the list.").And.Contain("at most a year ahead").And.Contain("Keep the note to 200 characters.")
            .And.Contain("Extra availability needs a start and an end time.");

        var sameTime = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "10:00"));
        (await sameTime.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*The start and end times are the same*");
        var badTime = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "25:00", ""));
        (await badTime.Should().ThrowAsync<ValidationFailedException>()).WithMessage("The start time \"25:00\" is not a time like 15:00. The end time is missing.");
        var tooOld = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Today.AddDays(-40), "10:00", "11:00"));
        (await tooOld.Should().ThrowAsync<ValidationFailedException>()).WithMessage("The date can be at most 31 days in the past.");
        var backwards = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Tuesday, Monday));
        (await backwards.Should().ThrowAsync<ValidationFailedException>()).WithMessage("The last day is before the first.");
        (await w.Db.CapacityExceptions.CountAsync()).Should().Be(0, "nothing is stored when anything is wrong");
    }

    [Fact]
    public async Task A_note_is_plain_text_stored_as_typed_and_control_characters_are_refused()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        const string script = "<script>alert('x')</script> & \"quotes\"";

        var saved = await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00", note: $"  {script}  "));
        saved.Note.Should().Be(script, "it is data, not markup: stored and returned as typed, and shown as text");

        var control = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Tuesday, "10:00", "11:00", note: "line\u0000break"));
        (await control.Should().ThrowAsync<ValidationFailedException>()).WithMessage("The note contains characters that can't be shown.");
    }

    [Fact]
    public async Task The_same_exception_twice_is_refused_and_an_update_is_audited_with_before_and_after()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var first = await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));

        var again = () => admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));
        (await again.Should().ThrowAsync<ValidationFailedException>()).WithMessage("That is already recorded for this person.");

        var updated = await admin.Exceptions.UpdateAsync(w.Admin.Id, w.Jason.Id, first.Id, Away(Monday, "14:00", "16:00", CapacityExceptionReason.Training));
        (updated.Id, updated.StartTime, updated.EndTime, updated.Reason).Should().Be((first.Id, "14:00", "16:00", CapacityExceptionReason.Training));
        (await DayAsync(w, w.Admin, w.Jason, Monday)).UnavailableMinutes.Should().Be(120);

        var detail = System.Text.Json.JsonDocument.Parse((await w.Db.AuditLog.SingleAsync(a => a.Action == "workforce.exception.updated")).DetailJson!).RootElement;
        detail.GetProperty("before").GetString().Should().Contain("10:00–11:00");
        detail.GetProperty("after").GetString().Should().Contain("14:00–16:00");
    }

    [Fact]
    public async Task A_long_absence_that_began_weeks_ago_can_still_be_shortened()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var leave = await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Today, Today.AddDays(60)));

        // Forty days on, they are coming back a week early. The start is long past the 31-day limit
        // for NEW dates, but it has not changed - only the end has.
        w.Clock.Advance(TimeSpan.FromDays(40));
        var shortened = await admin.Exceptions.UpdateAsync(w.Admin.Id, w.Jason.Id, leave.Id, AwayAllDay(Today, Today.AddDays(53)));
        (shortened.FromDate, shortened.ToDate).Should().Be((Today, Today.AddDays(53)));

        // Moving the start to another long-past date is still refused.
        var moved = () => admin.Exceptions.UpdateAsync(w.Admin.Id, w.Jason.Id, leave.Id, AwayAllDay(Today.AddDays(2), Today.AddDays(53)));
        (await moved.Should().ThrowAsync<ValidationFailedException>()).WithMessage("The date can be at most 31 days in the past.");
    }

    [Fact]
    public async Task An_exception_can_only_be_changed_through_the_person_it_belongs_to()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var jasons = await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));

        // Abbie's URL with Jason's exception id: not found, and Jason's exception is untouched.
        var update = () => admin.Exceptions.UpdateAsync(w.Admin.Id, w.Abbie.Id, jasons.Id, Away(Monday, "12:00", "13:00"));
        (await update.Should().ThrowAsync<NotFoundException>()).WithMessage("Exception was not found.");
        var remove = () => admin.Exceptions.RemoveAsync(w.Admin.Id, w.Abbie.Id, jasons.Id);
        await remove.Should().ThrowAsync<NotFoundException>();
        (await w.Db.CapacityExceptions.SingleAsync()).StartsAt.Should().Be(At(Monday, "10:00"));
    }

    // ---- who may record time away --------------------------------------------------------------

    [Fact]
    public async Task Recording_time_away_follows_the_scope_of_availability_manage()
    {
        var w = await WorldWithDayShiftsAsync();

        // A technician has no such claim by default: not even for themselves.
        var own = () => w.As(w.Jason).Exceptions.AddAsync(w.Jason.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));
        (await own.Should().ThrowAsync<ForbiddenException>()).WithMessage("You can't change this person's availability.");

        // A team lead (scope Team) records it for their own team...
        var lead = w.As(w.Lead);
        (await lead.Exceptions.AddAsync(w.Lead.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"))).UpdatedBy.Should().Be("Lena Lead");
        (await lead.Capacity.ForPersonAsync(w.Lead.Id, w.Jason.Id, Monday, Monday)).CanManageExceptions.Should().BeTrue();
        // ...and someone in another team does not exist as far as the lead can tell.
        var otherTeam = () => lead.Exceptions.AddAsync(w.Lead.Id, w.Sam.Id, Away(Monday, "10:00", "11:00"));
        (await otherTeam.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
    }

    [Fact]
    public async Task With_scope_own_a_technician_records_their_own_time_away_and_nobody_elses()
    {
        var w = await WorldWithDayShiftsAsync();
        // The owner's switch: give the Technician role availability.manage at scope Own, and let them see their team.
        w.Db.Add(new RolePermission { RoleId = w.TechRole.Id, PermissionKey = Permissions.AvailabilityManage, Scope = PermissionScope.Own });
        w.TechRole.Permissions.Single(p => p.PermissionKey == Permissions.ScheduleView).Scope = PermissionScope.Team;
        await w.Db.SaveChangesAsync();
        var jason = w.As(w.Jason);

        (await jason.Exceptions.AddAsync(w.Jason.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"))).AppUserId.Should().Be(w.Jason.Id);
        (await jason.Capacity.ForPersonAsync(w.Jason.Id, w.Jason.Id, Monday, Monday)).CanManageExceptions.Should().BeTrue();

        // Abbie is in his team, so he can see her capacity - but not change her availability.
        await w.As(w.Admin).Exceptions.AddAsync(w.Admin.Id, w.Abbie.Id, Away(Monday, "14:00", "15:00", CapacityExceptionReason.Sick, "Doctor at 14:00"));
        var colleague = await jason.Capacity.ForPersonAsync(w.Jason.Id, w.Abbie.Id, Monday, Monday);
        colleague.CanManageExceptions.Should().BeFalse();

        // He learns WHEN she is unavailable - planning needs that - and not WHY: the reason, the note
        // and who recorded it are hers and her managers' alone, on every route that returns them.
        colleague.Days.Single().UnavailableMinutes.Should().Be(60);
        colleague.Days.Single().Exceptions.Should().ContainSingle().Which.Should().Match<CapacityExceptionDto>(e =>
            e.StartTime == "14:00" && e.EndTime == "15:00" && e.Reason == null && e.Note == null && e.UpdatedBy == null);
        (await jason.Exceptions.ListAsync(w.Jason.Id, w.Abbie.Id, Monday, Monday)).Should().ContainSingle()
            .Which.Should().Match<CapacityExceptionDto>(e => e.Reason == null && e.Note == null && e.UpdatedBy == null);
        var team = await jason.Capacity.ForTeamAsync(w.Jason.Id, new TeamCapacityQuery(Monday));
        team.People.Single(p => p.AppUserId == w.Abbie.Id).Day.Exceptions.Single().Should().Match<CapacityExceptionDto>(e => e.Reason == null && e.Note == null);
        // His own he reads in full, and so does whoever manages her availability.
        team.People.Single(p => p.AppUserId == w.Jason.Id).Day.Exceptions.Single().Reason.Should().Be(CapacityExceptionReason.Appointment);
        (await w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Abbie.Id, Monday, Monday)).Days.Single().Exceptions.Single()
            .Should().Match<CapacityExceptionDto>(e => e.Reason == CapacityExceptionReason.Sick && e.Note == "Doctor at 14:00" && e.UpdatedBy == "Harpal Admin");
        var theirs = () => jason.Exceptions.AddAsync(w.Jason.Id, w.Abbie.Id, Away(Monday, "10:00", "11:00"));
        (await theirs.Should().ThrowAsync<ForbiddenException>()).WithMessage("You can't change this person's availability.");
    }

    [Fact]
    public async Task Listing_exceptions_follows_visibility_and_the_dates_asked_for()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Monday.AddDays(30), Monday.AddDays(34)));

        (await w.As(w.Jason).Exceptions.ListAsync(w.Jason.Id, w.Jason.Id, null, null)).Should().HaveCount(2, "a technician sees their own");
        (await admin.Exceptions.ListAsync(w.Admin.Id, w.Jason.Id, Monday, Monday.AddDays(6))).Should().ContainSingle();
        (await admin.Exceptions.ListAsync(w.Admin.Id, w.Jason.Id, Monday.AddDays(32), Monday.AddDays(32))).Should().ContainSingle("a date inside a run of days off finds that run");
        var colleague = () => w.As(w.Jason).Exceptions.ListAsync(w.Jason.Id, w.Abbie.Id, null, null);
        await colleague.Should().ThrowAsync<NotFoundException>();
    }

    // ---- FLOW 10: overnight --------------------------------------------------------------------

    [Fact]
    public async Task A_night_shift_is_one_day_of_capacity_and_exceptions_after_midnight_come_off_it()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());

        var night = await DayAsync(w, w.Admin, w.Abbie, Monday);
        (night.GrossMinutes, night.BreakMinutes, night.UsableMinutes).Should().Be((540, 30, 510));
        (night.WindowStart, night.WindowEnd).Should().Be((At(Monday, "18:00"), At(Tuesday, "03:00")));
        night.FreeSlots.Select(s => (s.Start, s.End)).Should().Equal((At(Monday, "18:00"), At(Monday, "22:00")), (At(Monday, "22:30"), At(Tuesday, "03:00")));

        // An appointment at 01:00 on Tuesday falls inside Monday's night shift, not Tuesday's.
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Abbie.Id, Away(Tuesday, "01:00", "02:00"));
        var days = (await admin.Capacity.ForPersonAsync(w.Admin.Id, w.Abbie.Id, Monday, Tuesday)).Days;
        (days[0].UnavailableMinutes, days[0].UsableMinutes).Should().Be((60, 450));
        days[0].Exceptions.Should().ContainSingle("it reaches into Monday's working window");
        (days[1].UnavailableMinutes, days[1].UsableMinutes).Should().Be((0, 510));
    }

    [Fact]
    public async Task A_day_off_cancels_the_whole_night_that_starts_on_it_and_no_other()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Abbie.Id, AwayAllDay(Monday));

        var days = (await admin.Capacity.ForPersonAsync(w.Admin.Id, w.Abbie.Id, Monday, Tuesday)).Days;
        (days[0].UsableMinutes, days[0].FreeSlots.Count).Should().Be((0, 0), "including its hours after midnight");
        days[1].UsableMinutes.Should().Be(510, "Tuesday night is a different shift");
    }

    [Fact]
    public async Task Extra_availability_never_offers_time_inside_a_day_taken_off()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        // Tuesday is off in full. Extra availability from Monday 20:00 runs on to Tuesday 10:00.
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Tuesday));
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Extra(Monday, "20:00", "10:00"));

        var days = (await admin.Capacity.ForPersonAsync(w.Admin.Id, w.Jason.Id, Monday, Tuesday)).Days;
        // Monday gains 20:00 to 08:30 - and stops where Tuesday's (cancelled) working window begins.
        days[0].FreeSlots.Last().Should().Match<SlotDto>(s => s.Start == At(Monday, "20:00") && s.End == At(Tuesday, "08:30"));
        days[1].UsableMinutes.Should().Be(0);
        // The search and the conflict check agree: 09:00 on the day off is not on offer, and is blocked.
        var found = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Tuesday, null, 30, EarliestTime: "08:30", LatestTime: "10:00", AppUserIds: [w.Jason.Id]));
        found.Matches.Should().BeEmpty();
        (await admin.Capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Tuesday, "09:00"), At(Tuesday, "09:30"))))
            .Conflicts.Select(c => c.Type).Should().Equal(ConflictType.UnavailableConflict);
    }

    [Fact]
    public async Task Extra_availability_next_to_a_night_shift_is_never_offered_twice()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());
        // 02:00-05:00 on Tuesday: the first hour is already Monday night's working time.
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Abbie.Id, Extra(Tuesday, "02:00", "05:00"));

        var days = (await admin.Capacity.ForPersonAsync(w.Admin.Id, w.Abbie.Id, Monday, Tuesday)).Days;
        (days[0].AdditionalMinutes, days[0].UsableMinutes).Should().Be((0, 510));
        (days[1].AdditionalMinutes, days[1].UsableMinutes).Should().Be((120, 630), "only 03:00-05:00 is new time, and it belongs to Tuesday");
        var everything = Intervals.Normalize(days.SelectMany(d => d.FreeSlots).Select(s => new Interval(s.Start, s.End)));
        Intervals.Minutes(everything).Should().Be(days.Sum(d => d.RemainingConfirmedMinutes), "no minute appears in two days' free slots");
    }

    // ---- planned work (supplied by the allocation phase) ---------------------------------------

    [Fact]
    public async Task Confirmed_and_tentative_work_are_reported_separately()
    {
        var w = await WorldWithDayShiftsAsync();
        w.Work.Book(w.Jason, Monday, "08:30", "12:30");
        w.Work.Book(w.Jason, Monday, "13:30", "14:30");
        w.Work.Book(w.Jason, Monday, "15:00", "16:00", confirmed: false);

        var day = await DayAsync(w, w.Jason, w.Jason, Monday);
        (day.UsableMinutes, day.ConfirmedMinutes, day.TentativeMinutes, day.RemainingConfirmedMinutes, day.ProjectedRemainingMinutes)
            .Should().Be((480, 300, 60, 180, 120));
        day.FreeSlots.Select(s => (s.Start, s.End)).Should().Equal((At(Monday, "14:30"), At(Monday, "17:30")));
        day.ProjectedFreeSlots.Select(s => s.Minutes).Should().Equal(30, 90);
    }

    // ---- authorized team capacity --------------------------------------------------------------

    [Fact]
    public async Task Team_capacity_lists_the_people_the_caller_may_see_with_their_day()
    {
        var w = await WorldWithDayShiftsAsync();
        w.Work.Book(w.Jason, Monday, "08:30", "12:30");
        w.Work.Book(w.Jason, Monday, "13:30", "14:30");
        w.Work.Book(w.Abbie, Monday, "08:30", "12:30");
        w.Work.Book(w.Abbie, Monday, "13:30", "16:30");

        var all = await w.As(w.Admin).Capacity.ForTeamAsync(w.Admin.Id, new TeamCapacityQuery(Monday));
        all.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Harpal Admin", "Jason Carter", "Lena Lead", "Sam Shah");
        var row = all.People.Single(p => p.AppUserId == w.Jason.Id);
        (row.Day.UsableMinutes, row.Day.ConfirmedMinutes, row.Day.RemainingConfirmedMinutes).Should().Be((480, 300, 180));
        row.Teams.Should().Equal("NOC");
        all.People.Single(p => p.AppUserId == w.Admin.Id).HasSchedule.Should().BeFalse();
        (all.UsableMinutes, all.ConfirmedMinutes, all.RemainingConfirmedMinutes).Should().Be((1440, 720, 720));

        // A team lead (scope Team) sees their team only; a technician sees themselves only.
        (await w.As(w.Lead).Capacity.ForTeamAsync(w.Lead.Id, new TeamCapacityQuery(Monday))).People.Select(p => p.DisplayName)
            .Should().Equal("Abbie Noor", "Jason Carter", "Lena Lead");
        (await w.As(w.Jason).Capacity.ForTeamAsync(w.Jason.Id, new TeamCapacityQuery(Monday))).People.Select(p => p.DisplayName)
            .Should().Equal("Jason Carter");
    }

    [Fact]
    public async Task Team_capacity_filters_by_team_and_skill_and_leaves_out_capacity_not_on_offer()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var sonicwall = await admin.Skills.CreateAsync("SonicWall", null);
        await admin.Skills.AssignAsync(w.Admin.Id, w.Jason.Id, sonicwall.Id, SkillLevel.Expert);
        await admin.Skills.AssignAsync(w.Admin.Id, w.Sam.Id, sonicwall.Id, SkillLevel.Basic);
        await admin.Schedules.SetSchedulableAsync(w.Admin.Id, w.Abbie.Id, false);

        var noc = await admin.Capacity.ForTeamAsync(w.Admin.Id, new TeamCapacityQuery(Monday, TeamId: w.Noc.Id));
        noc.People.Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Jason Carter", "Lena Lead");
        noc.People.Single(p => p.AppUserId == w.Abbie.Id).IsSchedulable.Should().BeFalse();
        noc.UsableMinutes.Should().Be(480, "Abbie is listed but not offered for planned work, so her hours are not in the total");

        var skilled = await admin.Capacity.ForTeamAsync(w.Admin.Id, new TeamCapacityQuery(Monday, SkillIds: [sonicwall.Id]));
        skilled.People.Select(p => p.DisplayName).Should().Equal("Jason Carter", "Sam Shah");
        skilled.People.First().Skills.Should().ContainSingle().Which.Level.Should().Be(SkillLevel.Expert);

        var both = await admin.Capacity.ForTeamAsync(w.Admin.Id, new TeamCapacityQuery(Monday, TeamId: w.Noc.Id, SkillIds: [sonicwall.Id]));
        both.People.Select(p => p.DisplayName).Should().Equal("Jason Carter");
    }

    [Fact]
    public async Task The_groups_offered_as_filters_are_those_with_someone_the_caller_may_see()
    {
        var w = await WorldWithDayShiftsAsync();
        (await w.As(w.Admin).Capacity.GroupsAsync(w.Admin.Id)).Teams.Select(t => t.Name).Should().Equal("NOC", "Security");
        (await w.As(w.Lead).Capacity.GroupsAsync(w.Lead.Id)).Teams.Select(t => t.Name).Should().Equal("NOC");
        var own = await w.As(w.Sam).Capacity.GroupsAsync(w.Sam.Id);
        own.Teams.Select(t => t.Name).Should().Equal("Security");
        own.Departments.Should().BeEmpty("nobody in this test is a member of a department directly");
    }

    [Fact]
    public async Task A_holiday_is_shown_on_the_day_and_takes_no_capacity_by_itself()
    {
        var w = await WorldWithDayShiftsAsync();
        w.Db.DeskHolidays.Add(new DeskHoliday { MspOrganizationId = OrgA, Date = Monday, Name = "Founders' Day" });
        await w.Db.SaveChangesAsync();

        var day = await DayAsync(w, w.Admin, w.Jason, Monday);
        day.Holiday.Should().Be("Founders' Day");
        day.UsableMinutes.Should().Be(480, "a desk that works its holidays keeps its capacity; time off is recorded per person");
    }

    // ---- FLOWS 4, 5, 6: find an available technician ------------------------------------------

    private static async Task<(World W, SkillDto SonicWall, SkillDto M365)> SearchWorldAsync()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var sonicwall = await admin.Skills.CreateAsync("SonicWall", null);
        var m365 = await admin.Skills.CreateAsync("Microsoft 365", null);
        await admin.Skills.AssignAsync(w.Admin.Id, w.Jason.Id, sonicwall.Id, SkillLevel.Expert);
        await admin.Skills.AssignAsync(w.Admin.Id, w.Jason.Id, m365.Id, SkillLevel.Proficient);
        await admin.Skills.AssignAsync(w.Admin.Id, w.Abbie.Id, sonicwall.Id, SkillLevel.Basic);
        return (w, sonicwall, m365);
    }

    [Fact]
    public async Task Finding_who_can_take_ninety_minutes_this_afternoon_with_a_skill()
    {
        var (w, sonicwall, _) = await SearchWorldAsync();
        w.Work.Book(w.Jason, Monday, "13:30", "14:00");
        w.Work.Book(w.Abbie, Monday, "13:30", "15:30");

        var found = await w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 90,
            EarliestTime: "13:00", LatestTime: "17:30", SkillIds: [sonicwall.Id]));

        (found.TimeZone, found.DurationMinutes, found.MatchAllSkills).Should().Be((Zone, 90, true));
        found.Matches.Select(m => m.DisplayName).Should().Equal("Jason Carter", "Abbie Noor");
        var jason = found.Matches[0];
        (jason.Date, jason.Recommended.Start, jason.Recommended.End, jason.Recommended.Minutes).Should().Be((Monday, At(Monday, "14:00"), At(Monday, "15:30"), 90));
        jason.Windows.Select(s => (s.Start, s.End)).Should().Equal((At(Monday, "14:00"), At(Monday, "17:30")));
        jason.FreeMinutes.Should().Be(210);
        jason.MatchingSkills.Should().ContainSingle().Which.Should().Match<StaffSkillDto>(s => s.Name == "SonicWall" && s.Level == SkillLevel.Expert);
        jason.Teams.Should().Equal("NOC");
        var abbie = found.Matches[1];
        (abbie.Recommended.Start, abbie.Recommended.End, abbie.FreeMinutes).Should().Be((At(Monday, "15:30"), At(Monday, "17:00"), 120));
        // Facts about everyone else, not a judgement: three people looked at do not hold the skill.
        (found.PeopleConsidered, found.WithoutRequiredSkills, found.NotOfferedForWork, found.WithoutASchedule, found.WithNoFittingSlot).Should().Be((5, 3, 0, 0, 0));
    }

    [Fact]
    public async Task Several_skills_mean_all_of_them_unless_any_is_asked_for()
    {
        var (w, sonicwall, m365) = await SearchWorldAsync();
        var capacity = w.As(w.Admin).Capacity;

        var all = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, SkillIds: [sonicwall.Id, m365.Id]));
        all.MatchAllSkills.Should().BeTrue("ALL is the default, and the answer says which rule was used");
        all.Matches.Select(m => m.DisplayName).Should().Equal("Jason Carter");

        var any = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, SkillIds: [sonicwall.Id, m365.Id], MatchAllSkills: false));
        any.Matches.Select(m => m.DisplayName).Should().BeEquivalentTo(["Jason Carter", "Abbie Noor"]);
        any.Matches.Single(m => m.DisplayName == "Abbie Noor").MatchingSkills.Select(s => s.Name).Should().Equal("SonicWall");
    }

    [Fact]
    public async Task A_skill_that_is_not_in_the_catalogue_is_refused_the_same_way_whoever_it_belongs_to()
    {
        var (w, _, _) = await SearchWorldAsync();
        // A real skill, but another organization's.
        var foreign = new Skill { MspOrganizationId = OrgB, Name = "Fortinet", NormalizedName = "FORTINET" };
        var other = AdminHarness.Create(OrgB, w.DbName);
        other.Db.Skills.Add(foreign);
        await other.Db.SaveChangesAsync();

        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            var act = () => w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, SkillIds: [id]));
            (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("One of the skills asked for is not in the skill catalogue.");
        }
    }

    [Fact]
    public async Task The_search_can_be_limited_to_a_team_and_to_named_people()
    {
        var (w, _, _) = await SearchWorldAsync();
        var capacity = w.As(w.Admin).Capacity;

        var noc = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 90, TeamId: w.Noc.Id));
        noc.Matches.Select(m => m.DisplayName).Should().BeEquivalentTo(["Jason Carter", "Abbie Noor"]);
        (noc.PeopleConsidered, noc.WithoutASchedule).Should().Be((3, 1), "the lead is in the NOC but has no working schedule; Sam is in another team");

        var one = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 90, AppUserIds: [w.Sam.Id]));
        one.Matches.Select(m => m.DisplayName).Should().Equal("Sam Shah");
        one.PeopleConsidered.Should().Be(1);
    }

    [Fact]
    public async Task Two_free_hours_apart_are_not_a_two_hour_slot()
    {
        var (w, _, _) = await SearchWorldAsync();
        // Jason: free 09:30-10:30 and 11:30-12:30 only.
        w.Work.Book(w.Jason, Monday, "08:30", "09:30");
        w.Work.Book(w.Jason, Monday, "10:30", "11:30");
        w.Work.Book(w.Jason, Monday, "13:30", "17:30");
        var capacity = w.As(w.Admin).Capacity;

        var two = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 120, AppUserIds: [w.Jason.Id]));
        two.Matches.Should().BeEmpty();
        two.WithNoFittingSlot.Should().Be(1);

        var one = await capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, AppUserIds: [w.Jason.Id]));
        one.Matches.Single().Windows.Should().HaveCount(2);
        one.Matches.Single().FreeMinutes.Should().Be(120);
    }

    [Fact]
    public async Task People_not_offered_for_planned_work_are_counted_and_never_returned()
    {
        var (w, sonicwall, _) = await SearchWorldAsync();
        await w.As(w.Admin).Schedules.SetSchedulableAsync(w.Admin.Id, w.Jason.Id, false);

        var found = await w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, SkillIds: [sonicwall.Id]));
        found.Matches.Select(m => m.DisplayName).Should().Equal("Abbie Noor");
        found.NotOfferedForWork.Should().Be(1);
    }

    [Fact]
    public async Task A_search_over_several_days_returns_the_first_day_each_person_fits()
    {
        var (w, _, _) = await SearchWorldAsync();
        await w.As(w.Admin).Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, AwayAllDay(Monday));

        var found = await w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, Monday.AddDays(2), 240, AppUserIds: [w.Jason.Id, w.Abbie.Id]));
        found.Matches.Select(m => (m.DisplayName, m.Date)).Should().Equal(("Abbie Noor", Monday), ("Jason Carter", Tuesday));
    }

    [Fact]
    public async Task Nothing_is_offered_in_the_past()
    {
        var (w, _, _) = await SearchWorldAsync();
        // It is now 15:10:30 on Monday in the organization's zone.
        w.Clock.Advance(At(Monday, "15:10").AddSeconds(30) - w.Clock.GetUtcNow());

        var found = await w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 60, AppUserIds: [w.Jason.Id]));
        var slot = found.Matches.Single().Recommended;
        (slot.Start, slot.End).Should().Be((At(Monday, "15:11"), At(Monday, "16:11")), "from the next whole minute, not from this morning");
        found.Matches.Single().FreeMinutes.Should().Be(139);

        var yesterday = () => w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday.AddDays(-1), null, 60));
        (await yesterday.Should().ThrowAsync<ValidationFailedException>()).WithMessage($"Those dates have passed in {Zone}. Search from 5 Jan 2026.");
    }

    [Fact]
    public async Task A_night_shift_is_found_by_a_search_for_the_early_hours_of_the_next_date()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());

        // 01:00-03:00 on Tuesday is free time of the shift that began on Monday evening.
        var found = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Tuesday, null, 90, EarliestTime: "01:00", LatestTime: "04:00", AppUserIds: [w.Abbie.Id]));
        var match = found.Matches.Single();
        (match.Date, match.Recommended.Start, match.Recommended.End).Should().Be((Tuesday, At(Tuesday, "01:00"), At(Tuesday, "02:30")));
        match.FreeMinutes.Should().Be(120);
    }

    [Fact]
    public async Task A_free_slot_that_crosses_midnight_is_one_slot_when_no_time_of_day_is_asked_for()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());
        // 18:00-03:00 with a break 22:00-22:30: free 18:00-22:00 (4h) and 22:30-03:00 (4h 30m).

        // 4h 30m only fits in the stretch that runs past midnight. Cut at midnight it would be 1h 30m
        // on Monday and 3h on Tuesday, and she would never be found.
        var monday = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 270, AppUserIds: [w.Abbie.Id]));
        var match = monday.Matches.Should().ContainSingle().Subject;
        (match.Date, match.Recommended.Start, match.Recommended.End).Should().Be((Monday, At(Monday, "22:30"), At(Tuesday, "03:00")));
        monday.WithNoFittingSlot.Should().Be(0);

        // A search for Tuesday alone starts on Tuesday: Monday night's last three hours, then Tuesday's own night.
        var tuesday = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Tuesday, null, 270, AppUserIds: [w.Abbie.Id]));
        (tuesday.Matches.Single().Recommended.Start, tuesday.Matches.Single().Date).Should().Be((At(Tuesday, "22:30"), Tuesday));
        // With a time of day given, the work must lie inside it: 22:00-24:00 holds no 4h 30m.
        var banded = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, null, 270, EarliestTime: "22:00", LatestTime: "23:59", AppUserIds: [w.Abbie.Id]));
        banded.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task At_one_in_the_morning_a_search_for_tonight_finds_the_night_that_is_still_running()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());
        // It is 01:00 on Tuesday; Monday's night shift runs until 03:00.
        w.Clock.Advance(At(Tuesday, "01:00") - w.Clock.GetUtcNow());

        var band = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Tuesday, null, 60, EarliestTime: "22:00", LatestTime: "06:00", AppUserIds: [w.Abbie.Id]));
        var now = band.Matches.Should().ContainSingle().Subject;
        (now.Date, now.Recommended.Start, now.Recommended.End, now.FreeMinutes).Should().Be((Tuesday, At(Tuesday, "01:00"), At(Tuesday, "02:00"), 120));

        var anyTime = await admin.Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Tuesday, null, 60, AppUserIds: [w.Abbie.Id]));
        anyTime.Matches.Single().Recommended.Start.Should().Be(At(Tuesday, "01:00"));
    }

    [Theory]
    [InlineData(0, 0, "13:00", null, "The work must take between 5 minutes and 12 hours.")]
    [InlineData(60, 14, null, null, "Search at most 14 days at a time.")]
    [InlineData(60, -1, null, null, "The last date is before the first.")]
    [InlineData(60, 0, "1pm", null, "The earliest time \"1pm\" is not a time like 13:00.")]
    [InlineData(60, 0, null, "Not/AZone!", "\"Not/AZone!\" is not a time zone this system knows.")]
    public async Task A_search_is_checked_and_bounded(int minutes, int extraDays, string? earliest, string? zone, string message)
    {
        var (w, _, _) = await SearchWorldAsync();
        var act = () => w.As(w.Admin).Capacity.FindAsync(w.Admin.Id, new AvailabilitySearch(Monday, Monday.AddDays(extraDays), minutes, EarliestTime: earliest, TimeZone: zone));
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage(message);
    }

    [Fact]
    public async Task A_technician_searching_finds_only_themselves()
    {
        var (w, _, _) = await SearchWorldAsync();
        var found = await w.As(w.Jason).Capacity.FindAsync(w.Jason.Id, new AvailabilitySearch(Monday, null, 60, AppUserIds: [w.Abbie.Id, w.Jason.Id]));
        found.Matches.Select(m => m.DisplayName).Should().Equal("Jason Carter");
        found.PeopleConsidered.Should().Be(1, "naming a colleague does not make them visible");
    }

    // ---- FLOW 9: conflicts ---------------------------------------------------------------------

    [Fact]
    public async Task A_conflict_check_reports_confirmed_work_and_hides_what_the_caller_may_not_see()
    {
        var w = await WorldWithDayShiftsAsync();
        var visible = Guid.NewGuid();
        w.Work.Book(w.Jason, Monday, "10:00", "11:00", workId: visible);
        w.Work.Book(w.Jason, Monday, "14:00", "15:00", workId: null); // work this caller has no right to see
        var capacity = w.As(w.Admin).Capacity;

        var free = await capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Monday, "11:00"), At(Monday, "12:00")));
        (free.CanSchedule, free.Conflicts.Count).Should().Be((true, 0));

        var clash = await capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Monday, "10:30"), At(Monday, "11:30")));
        (clash.CanSchedule, clash.CanOverride).Should().Be((false, true));
        clash.Conflicts.Should().ContainSingle().Which.Should().Match<ConflictDto>(c =>
            c.Type == ConflictType.HardConflict && c.Severity == ConflictSeverity.Overridable && c.BlockingWorkId == visible
            && c.Start == At(Monday, "10:30") && c.End == At(Monday, "11:00"));

        var hidden = await capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Monday, "14:30"), At(Monday, "15:30")));
        hidden.Conflicts.Should().ContainSingle().Which.Should().Match<ConflictDto>(c =>
            c.BlockingWorkId == null && c.Message == "Already has confirmed work during this period.");
    }

    [Fact]
    public async Task A_conflict_check_sees_exceptions_working_hours_and_skills()
    {
        var w = await WorldWithDayShiftsAsync();
        var admin = w.As(w.Admin);
        var sonicwall = await admin.Skills.CreateAsync("SonicWall", null);
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "15:00", "16:00"));

        var away = await admin.Capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Monday, "15:30"), At(Monday, "16:30")));
        (away.CanSchedule, away.CanOverride).Should().Be((false, false));
        away.Conflicts.Select(c => c.Type).Should().Equal(ConflictType.UnavailableConflict);

        var late = await admin.Capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Monday, "17:00"), At(Monday, "18:00"), SkillIds: [sonicwall.Id]));
        late.Conflicts.Select(c => (c.Type, c.Severity)).Should().BeEquivalentTo([
            (ConflictType.OutsideWorkingWindow, ConflictSeverity.Overridable), (ConflictType.SkillWarning, ConflictSeverity.Warning)]);

        var weekend = await admin.Capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(At(Saturday, "10:00"), At(Saturday, "11:00")));
        weekend.Conflicts.Select(c => c.Type).Should().Equal(ConflictType.OutsideWorkingWindow);
    }

    [Fact]
    public async Task Availability_is_not_a_reservation_every_check_reads_fresh_data()
    {
        var w = await WorldWithDayShiftsAsync();
        var managerA = w.As(w.Admin).Capacity;
        var managerB = w.As(w.Lead).Capacity;
        var slot = new ProposedWork(At(Monday, "15:00"), At(Monday, "16:00"));

        (await managerA.EvaluateAsync(w.Admin.Id, w.Jason.Id, slot)).CanSchedule.Should().BeTrue();
        (await managerB.EvaluateAsync(w.Lead.Id, w.Jason.Id, slot)).CanSchedule.Should().BeTrue();
        var callsBefore = w.Work.Calls;

        // Manager A books it. Manager B's check at the moment of booking must now refuse.
        w.Work.Book(w.Jason, Monday, "15:00", "16:00");
        (await managerB.EvaluateAsync(w.Lead.Id, w.Jason.Id, slot)).CanSchedule.Should().BeFalse();
        w.Work.Calls.Should().Be(callsBefore + 1, "nothing is cached: the planned work is read again on every check");
    }

    [Theory]
    [InlineData("11:00", "11:00", 0, "The work must end after it starts.")]
    [InlineData("11:00", "10:00", 0, "The work must end after it starts.")]
    [InlineData("08:00", "08:00", 2, "One piece of work can be at most 24 hours long.")]
    [InlineData("08:00", "09:00", 500, "Choose a time between a month ago and a year ahead.")]
    public async Task A_proposal_is_checked_before_anything_is_read(string from, string to, int daysLater, string message)
    {
        var w = await WorldWithDayShiftsAsync();
        var start = At(Monday.AddDays(daysLater > 100 ? daysLater : 0), from);
        var end = At(Monday.AddDays(daysLater), to);
        var act = () => w.As(w.Admin).Capacity.EvaluateAsync(w.Admin.Id, w.Jason.Id, new ProposedWork(start, end));
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage(message);
        w.Work.Calls.Should().Be(0);
    }

    // ---- FLOW 7: an account that is not staff --------------------------------------------------

    [Fact]
    public async Task A_sign_in_that_is_not_a_staff_account_is_refused_by_every_capacity_action_whatever_it_holds()
    {
        // A client login has no staff user id. Even if such a principal were somehow handed every
        // workforce claim, each action stops before any service is touched (they are null here).
        var everyClaim = new HashSet<string> { Permissions.ScheduleView, Permissions.WorkforceManage, Permissions.AvailabilityManage };
        var notStaff = new Desk.Api.Controllers.WorkforceCapacityController(null!, null!,
            new TestCurrentUser(OrgA, permissions: everyClaim, userId: null), new WorkforceFeatureOptions { Enabled = true });
        var id = Guid.NewGuid();
        Func<Task>[] calls =
        [
            () => notStaff.PersonCapacity(id, null, null, default),
            () => notStaff.TeamCapacity(new(), default),
            () => notStaff.Groups(default),
            () => notStaff.FindAvailable(new(From: Monday, Duration: 60), default),
            () => notStaff.EvaluateConflicts(id, new(At(Monday, "09:00"), At(Monday, "10:00")), default),
            () => notStaff.Exceptions(id, null, null, default),
            () => notStaff.AddException(id, Away(Monday, "09:00", "10:00"), default),
            () => notStaff.UpdateException(id, id, Away(Monday, "09:00", "10:00"), default),
            () => notStaff.RemoveException(id, id, default),
        ];
        foreach (var call in calls)
            (await call.Should().ThrowAsync<ForbiddenException>()).WithMessage("Only staff accounts can use the workforce module.");

        // And with the module switched off, nothing answers at all - even for staff.
        var off = new Desk.Api.Controllers.WorkforceCapacityController(null!, null!,
            new TestCurrentUser(OrgA, permissions: everyClaim, userId: Guid.NewGuid()), new WorkforceFeatureOptions { Enabled = false });
        var hidden = () => off.TeamCapacity(new(), default);
        (await hidden.Should().ThrowAsync<NotFoundException>()).WithMessage("Workforce was not found.");
    }

    // ---- FLOW 8: another organization ----------------------------------------------------------

    [Fact]
    public async Task Another_organizations_people_cannot_be_read_changed_searched_or_even_detected()
    {
        var w = await WorldWithDayShiftsAsync();
        await w.As(w.Admin).Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, Away(Monday, "10:00", "11:00"));
        var exceptionId = (await w.Db.CapacityExceptions.SingleAsync()).Id;

        // An administrator of organization B, on B's own tenant context.
        var other = AdminHarness.Create(OrgB, w.DbName);
        var adminB = new AppUser { MspOrganizationId = OrgB, DisplayName = "Bea Admin", Email = "bea@other.test", IsActive = true };
        var roleB = new Role { MspOrganizationId = OrgB, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        foreach (var key in new[] { Permissions.ScheduleView, Permissions.WorkforceManage, Permissions.AvailabilityManage })
            roleB.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        adminB.Roles.Add(new UserRole { RoleId = roleB.Id });
        other.Db.AddRange(roleB, adminB);
        await other.Db.SaveChangesAsync();
        var b = w.For(other.Db, OrgB, adminB);

        // A real person in A and an id that names nobody get the very same answer.
        foreach (var id in new[] { w.Jason.Id, Guid.NewGuid() })
        {
            var read = () => b.Capacity.ForPersonAsync(adminB.Id, id, Monday, Monday);
            (await read.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
            var check = () => b.Capacity.EvaluateAsync(adminB.Id, id, new ProposedWork(At(Monday, "09:00"), At(Monday, "10:00")));
            (await check.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
            var list = () => b.Exceptions.ListAsync(adminB.Id, id, null, null);
            (await list.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
            var add = () => b.Exceptions.AddAsync(adminB.Id, id, Away(Monday, "13:30", "14:30"));
            (await add.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
            var update = () => b.Exceptions.UpdateAsync(adminB.Id, id, exceptionId, Away(Monday, "13:30", "14:30"));
            (await update.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
            var remove = () => b.Exceptions.RemoveAsync(adminB.Id, id, exceptionId);
            (await remove.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        }

        var team = await b.Capacity.ForTeamAsync(adminB.Id, new TeamCapacityQuery(Monday));
        team.People.Select(p => p.DisplayName).Should().Equal("Bea Admin", "Other Org");
        var search = await b.Capacity.FindAsync(adminB.Id, new AvailabilitySearch(Monday, null, 60, AppUserIds: [w.Jason.Id, w.Abbie.Id]));
        (search.Matches.Count, search.PeopleConsidered).Should().Be((0, 0), "naming A's people finds nobody and counts nobody");

        // And organization A's administrator cannot reach B's person either.
        var reverse = () => w.As(w.Admin).Capacity.ForPersonAsync(w.Admin.Id, w.Outsider.Id, Monday, Monday);
        await reverse.Should().ThrowAsync<NotFoundException>();
        (await w.Db.CapacityExceptions.CountAsync()).Should().Be(1, "nothing was written for anyone");
    }
}
