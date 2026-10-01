using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Organization;
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
/// Working schedules and skills through the services, with permissions resolved from the database
/// exactly as in production: who may see whom, who may change anything, and that another
/// organization's people and skills are never reachable.
/// </summary>
public class WorkforceFoundationTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class Correlation(string id) : ICorrelationContext { public string? CorrelationId => id; }

    private sealed record World(DeskDbContext Db, string DbName, AppUser Admin, AppUser Lead, AppUser Jason, AppUser Abbie, AppUser Sam, AppUser Outsider, Team Noc, TestClock Clock)
    {
        public (WorkScheduleService Schedules, SkillService Skills) As(AppUser who)
        {
            var tenant = new Desk.Infrastructure.Tenancy.TenantContext();
            tenant.SetTenant(OrgA);
            var user = new TestCurrentUser(OrgA, userId: who.Id, name: who.DisplayName);
            var access = new WorkforceAccess(Db, tenant, new EffectivePermissionService(Db));
            var audit = new AuditWriter(Db, user, tenant, Clock, new Correlation("corr-p1"));
            return (new WorkScheduleService(Db, access, audit, Clock), new SkillService(Db, access, tenant, user, audit));
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
        var admin = R("Administrator", RoleType.MspAdministrator, (Permissions.ScheduleView, PermissionScope.All), (Permissions.WorkforceManage, PermissionScope.All));
        var lead = R("Team lead", RoleType.Manager, (Permissions.ScheduleView, PermissionScope.Team));
        var tech = R("Technician", RoleType.Technician, (Permissions.ScheduleView, PermissionScope.Own));
        AppUser U(string name, Role role, Guid? org = null)
        {
            var u = new AppUser { MspOrganizationId = org ?? OrgA, DisplayName = name, Email = $"{name.Split(' ')[0].ToLowerInvariant()}@techpio.test", IsActive = true };
            u.Roles.Add(new UserRole { RoleId = role.Id });
            db.AppUsers.Add(u);
            return u;
        }
        var w = new World(db, dbName, U("Harpal Admin", admin), U("Lena Lead", lead), U("Jason Carter", tech), U("Abbie Noor", tech), U("Sam Shah", tech),
            U("Other Org", tech, OrgB), Noc: null!, h.Clock);
        var dept = new Department { MspOrganizationId = OrgA, Name = "Operations" };
        var noc = new Team { MspOrganizationId = OrgA, Department = dept, Name = "NOC" };
        var security = new Team { MspOrganizationId = OrgA, Department = dept, Name = "Security" };
        db.AddRange(dept, noc, security);
        db.UserTeams.AddRange(
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Lead.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Abbie.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Sam.Id, TeamId = security.Id });
        await db.SaveChangesAsync();
        return w with { Noc = noc };
    }

    private static WorkScheduleInput DayShift(DateOnly? from = null) => new(from, "Asia/Kolkata",
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(d => new WorkDayInput(d, "08:30", "17:30", [new WorkBreakDto("12:30", "13:30")])).ToList());

    private static WorkScheduleInput NightShift() => new(null, "Asia/Kolkata",
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(d => new WorkDayInput(d, "18:00", "03:00", [new WorkBreakDto("00:00", "00:30")])).ToList());

    [Fact]
    public async Task An_administrator_sets_a_schedule_and_it_reads_back_exactly()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);

        var saved = await schedules.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift());

        saved.Current!.Days.Should().HaveCount(5).And.OnlyContain(d => d.UsableMinutes == 480 && d.GrossMinutes == 540 && !d.CrossesMidnight);
        saved.Current.WeeklyUsableMinutes.Should().Be(2400);
        saved.Current.TimeZone.Should().Be("Asia/Kolkata");
        var reread = await schedules.GetAsync(w.Admin.Id, w.Jason.Id);
        reread.Current!.Days.Select(d => (d.Day, d.Start, d.End, d.Breaks.Single().Start)).Should().Equal(
            saved.Current.Days.Select(d => (d.Day, d.Start, d.End, d.Breaks.Single().Start)));

        var entry = await w.Db.AuditLog.SingleAsync(a => a.Action == "workforce.schedule.saved");
        entry.CorrelationId.Should().Be("corr-p1");
        System.Text.Json.JsonDocument.Parse(entry.DetailJson!).RootElement.GetProperty("after").GetString()
            .Should().Be("Mon–Fri 08:30–17:30 (break 12:30–13:30) · Asia/Kolkata");
    }

    [Fact]
    public async Task A_night_technicians_window_runs_past_midnight()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        var night = await schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, NightShift());
        night.Current!.Days.Should().OnlyContain(d => d.CrossesMidnight && d.GrossMinutes == 540 && d.UsableMinutes == 510);
    }

    [Fact]
    public async Task A_change_from_a_later_day_leaves_today_alone_and_can_be_withdrawn()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        await schedules.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift());
        var later = new DateOnly(2026, 2, 2);

        var both = await schedules.SaveAsync(w.Admin.Id, w.Jason.Id, NightShift() with { EffectiveFrom = later });

        both.Current!.Days.Should().OnlyContain(d => !d.CrossesMidnight);
        both.Upcoming!.EffectiveFrom.Should().Be(later);
        both.Versions.Should().HaveCount(2);
        (await schedules.RemoveUpcomingAsync(w.Admin.Id, w.Jason.Id, later)).Upcoming.Should().BeNull();
        await schedules.Invoking(s => s.RemoveUpcomingAsync(w.Admin.Id, w.Jason.Id, both.Current.EffectiveFrom))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*has not started*");
    }

    [Fact]
    public async Task Days_already_past_keep_the_schedule_they_had()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        await schedules.Invoking(s => s.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift(new DateOnly(2025, 12, 1))))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*today*or later*");
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once_and_nothing_is_saved()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        var bad = new WorkScheduleInput(null, "not a zone!", [
            new WorkDayInput(DayOfWeek.Monday, "08:30", "17:30", [new WorkBreakDto("12:00", "13:00"), new WorkBreakDto("12:30", "13:30")]),
            new WorkDayInput(DayOfWeek.Tuesday, "08:30", "17:30", [new WorkBreakDto("18:00", "19:00")]),
            new WorkDayInput(DayOfWeek.Wednesday, "09:00", "09:00", null),
            new WorkDayInput(DayOfWeek.Thursday, "8.30am", "17:30", null),
        ]);

        var ex = await schedules.Invoking(s => s.SaveAsync(w.Admin.Id, w.Jason.Id, bad)).Should().ThrowAsync<ValidationFailedException>();
        ex.Which.Message.Should().ContainAll("time zone", "overlap", "outside the working window", "same time", "like 08:30");
        (await w.Db.WorkSchedules.CountAsync()).Should().Be(0);

        await schedules.Invoking(s => s.SaveAsync(w.Admin.Id, w.Jason.Id, new WorkScheduleInput(null, "Asia/Kolkata", [])))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*at least one working day*");
    }

    [Fact]
    public async Task A_technician_sees_their_own_schedule_but_no_one_elses_and_changes_nothing()
    {
        var w = await WorldAsync();
        await w.As(w.Admin).Schedules.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift());
        await w.As(w.Admin).Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, DayShift());
        var (asJason, skillsAsJason) = w.As(w.Jason);

        (await asJason.GetAsync(w.Jason.Id, w.Jason.Id)).CanManage.Should().BeFalse();
        await asJason.Invoking(s => s.GetAsync(w.Jason.Id, w.Abbie.Id)).Should().ThrowAsync<NotFoundException>();
        await asJason.Invoking(s => s.SaveAsync(w.Jason.Id, w.Jason.Id, NightShift())).Should().ThrowAsync<ForbiddenException>();
        await asJason.Invoking(s => s.SaveAsync(w.Jason.Id, w.Abbie.Id, NightShift())).Should().ThrowAsync<ForbiddenException>();
        await skillsAsJason.Invoking(s => s.CreateAsync("Azure", null)).Should().ThrowAsync<ForbiddenException>();
        (await asJason.PeopleAsync(w.Jason.Id, new WorkforceQuery())).Select(p => p.DisplayName).Should().Equal("Jason Carter");
    }

    [Fact]
    public async Task A_team_lead_sees_their_team_and_not_other_teams()
    {
        var w = await WorldAsync();
        var (asLead, _) = w.As(w.Lead);
        (await asLead.PeopleAsync(w.Lead.Id, new WorkforceQuery())).Select(p => p.DisplayName)
            .Should().BeEquivalentTo(["Lena Lead", "Jason Carter", "Abbie Noor"]);
        await asLead.Invoking(s => s.GetAsync(w.Lead.Id, w.Sam.Id)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organizations_people_and_skills_are_never_reachable()
    {
        var w = await WorldAsync();
        var (schedules, skills) = w.As(w.Admin);
        await schedules.Invoking(s => s.GetAsync(w.Admin.Id, w.Outsider.Id)).Should().ThrowAsync<NotFoundException>();
        await schedules.Invoking(s => s.SaveAsync(w.Admin.Id, w.Outsider.Id, DayShift())).Should().ThrowAsync<NotFoundException>();
        await skills.Invoking(s => s.AssignAsync(w.Admin.Id, w.Outsider.Id, Guid.NewGuid(), SkillLevel.Expert)).Should().ThrowAsync<NotFoundException>();

        // A skill that exists in the other organization is simply not found from this one.
        var theirs = new Skill { MspOrganizationId = OrgB, Name = "Fortinet", NormalizedName = "FORTINET" };
        var orgB = AdminHarness.Create(OrgB, w.DbName).Db;
        orgB.Skills.Add(theirs);
        await orgB.SaveChangesAsync();
        await skills.Invoking(s => s.AssignAsync(w.Admin.Id, w.Jason.Id, theirs.Id, SkillLevel.Expert)).Should().ThrowAsync<NotFoundException>();
        (await skills.ListAsync(includeInactive: true)).Should().NotContain(s => s.Name == "Fortinet");
        (await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery())).Should().NotContain(p => p.DisplayName == "Other Org");
    }

    [Fact]
    public async Task A_schedule_is_copied_to_others_from_a_day()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        await schedules.SaveAsync(w.Admin.Id, w.Jason.Id, NightShift());
        (await schedules.CopyAsync(w.Admin.Id, w.Jason.Id, [w.Abbie.Id, w.Sam.Id], null)).Should().Be(2);
        (await schedules.GetAsync(w.Admin.Id, w.Sam.Id)).Current!.Days.Should().OnlyContain(d => d.CrossesMidnight && d.UsableMinutes == 510);
        await schedules.Invoking(s => s.CopyAsync(w.Admin.Id, w.Jason.Id, [w.Outsider.Id], null)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Someone_taken_out_of_planned_work_keeps_their_schedule_and_inactive_people_drop_out()
    {
        var w = await WorldAsync();
        var (schedules, _) = w.As(w.Admin);
        await schedules.SaveAsync(w.Admin.Id, w.Jason.Id, DayShift());
        var off = await schedules.SetSchedulableAsync(w.Admin.Id, w.Jason.Id, false);
        (off.IsSchedulable, off.Current is not null).Should().Be((false, true));
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.schedulable.changed")).Should().Be(1);

        w.Abbie.IsActive = false;
        await w.Db.SaveChangesAsync();
        (await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery())).Should().NotContain(p => p.DisplayName == "Abbie Noor");
        (await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery(IncludeInactive: true))).Should().Contain(p => p.DisplayName == "Abbie Noor");
    }

    [Fact]
    public async Task Skills_are_administered_assigned_and_filtered()
    {
        var w = await WorldAsync();
        var (schedules, skills) = w.As(w.Admin);
        var m365 = await skills.CreateAsync("Microsoft 365", "Exchange, Teams, Intune");
        var sonic = await skills.CreateAsync("SonicWall", null);
        await skills.Invoking(s => s.CreateAsync("  sonicwall ", null)).Should().ThrowAsync<ValidationFailedException>().WithMessage("*already exists*");
        await skills.Invoking(s => s.CreateAsync("x", null)).Should().ThrowAsync<ValidationFailedException>();

        await skills.AssignAsync(w.Admin.Id, w.Jason.Id, m365.Id, SkillLevel.Expert);
        await skills.AssignAsync(w.Admin.Id, w.Jason.Id, sonic.Id, SkillLevel.Basic);
        await skills.AssignAsync(w.Admin.Id, w.Abbie.Id, sonic.Id, SkillLevel.Proficient);
        await skills.Invoking(s => s.AssignAsync(w.Admin.Id, w.Abbie.Id, sonic.Id, SkillLevel.Proficient))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*already holds*");
        (await skills.AssignAsync(w.Admin.Id, w.Abbie.Id, sonic.Id, SkillLevel.Expert)).Single().Level.Should().Be(SkillLevel.Expert);

        var any = await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery(SkillIds: [m365.Id, sonic.Id]));
        any.Select(p => p.DisplayName).Should().BeEquivalentTo(["Jason Carter", "Abbie Noor"]);
        var all = await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery(SkillIds: [m365.Id, sonic.Id], MatchAllSkills: true));
        all.Select(p => p.DisplayName).Should().Equal("Jason Carter");
        var nocWithSonic = await schedules.PeopleAsync(w.Admin.Id, new WorkforceQuery(TeamId: w.Noc.Id, SkillIds: [sonic.Id]));
        nocWithSonic.Select(p => p.DisplayName).Should().BeEquivalentTo(["Jason Carter", "Abbie Noor"]);

        // Renamed and retired: holders keep it, nobody new gets it.
        var retired = await skills.UpdateAsync(sonic.Id, "SonicWall NSA", null, isActive: false);
        retired.HolderCount.Should().Be(2);
        await skills.Invoking(s => s.AssignAsync(w.Admin.Id, w.Sam.Id, sonic.Id, SkillLevel.Basic))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*retired*");
        (await skills.RemoveAsync(w.Admin.Id, w.Jason.Id, sonic.Id)).Select(s => s.Name).Should().Equal("Microsoft 365");

        var actions = await w.Db.AuditLog.Select(a => a.Action).ToListAsync();
        actions.Should().Contain(["skill.created", "skill.assigned", "skill.level_changed", "skill.updated", "skill.removed"]);
    }

    [Fact]
    public async Task A_skill_name_is_kept_as_plain_text_and_control_characters_are_refused()
    {
        var w = await WorldAsync();
        var (_, skills) = w.As(w.Admin);
        (await skills.CreateAsync("<b>Backup</b>", null)).Name.Should().Be("<b>Backup</b>");
        await skills.Invoking(s => s.CreateAsync("Linux\u0007", null)).Should().ThrowAsync<ValidationFailedException>();
    }
}
