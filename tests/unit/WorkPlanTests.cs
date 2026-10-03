using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Notifications;
using Desk.Domain.Organization;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Authorization;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.Infrastructure.Workforce;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Work in people's time, through the services with permissions and ticket visibility resolved from
/// the database exactly as in production: planning your own work, scheduling others, fixed and
/// flexible work, moving, giving away and taking out, what each does to the ticket (nothing on the
/// PSA side), the release of finished work, and that another organization's plans do not exist.
/// </summary>
public class WorkPlanTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 1, 1);
    private static readonly DateOnly Monday = new(2026, 1, 5);
    private static readonly DateOnly Tuesday = new(2026, 1, 6);
    private const string Zone = "Asia/Kolkata";
    private static readonly TimeZoneInfo Tz = TimeZones.Resolve(Zone);
    private static DateTimeOffset At(DateOnly date, string hm) => TimeZones.WallToUtc(date.ToDateTime(TimeOnly.Parse(hm)), Tz, true);

    private sealed record Services(WorkPlanService Plans, CapacityService Capacity, WorkScheduleService Schedules, CapacityExceptionService Exceptions, WorkAllocationReleaser Releaser);

    private sealed record World(DeskDbContext Db, string DbName, AppUser Admin, AppUser Lead, AppUser Jason, AppUser Abbie, AppUser Sam, AppUser Outsider,
        Team Noc, Board Board, Ticket JasonsTicket, Ticket OpenTicket, Ticket SamsTicket, Ticket Autotask, Ticket ConnectWise, TestClock Clock)
    {
        public Services As(AppUser who) => For(Db, OrgA, who, Clock);

        public static Services For(DeskDbContext db, Guid org, AppUser who, TimeProvider clock)
        {
            var tenant = new Desk.Infrastructure.Tenancy.TenantContext();
            tenant.SetTenant(org);
            var user = new TestCurrentUser(org, userId: who.Id, name: who.DisplayName);
            var permissions = new EffectivePermissionService(db);
            var access = new WorkforceAccess(db, tenant, permissions);
            var scope = new TicketScopeQuery(db, permissions);
            var audit = new AuditWriter(db, user, tenant, clock);
            var capacity = new CapacityService(db, access, new WorkAllocationReader(db, scope, clock), clock);
            var plans = new WorkPlanService(db, access, capacity, scope, new InternalTicketService(db, tenant, clock, new RecordingActivity()), new PlanningGate(db), audit, clock);
            return new Services(plans, capacity, new WorkScheduleService(db, access, audit, clock), new CapacityExceptionService(db, access, audit, clock),
                new WorkAllocationReleaser(db, audit, clock));
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
        var all = PermissionScope.All;
        var admin = R("Administrator", RoleType.MspAdministrator, (Permissions.TicketsViewAll, all), (Permissions.TicketsUpdate, all),
            (Permissions.ScheduleView, all), (Permissions.WorkforceManage, all), (Permissions.AvailabilityManage, all), (Permissions.ScheduleManage, all), (Permissions.ScheduleOverride, all));
        var lead = R("Team lead", RoleType.Manager, (Permissions.TicketsViewAll, all), (Permissions.TicketsUpdate, all),
            (Permissions.ScheduleView, PermissionScope.Team), (Permissions.AvailabilityManage, PermissionScope.Team), (Permissions.ScheduleManage, PermissionScope.Team));
        var tech = R("Technician", RoleType.Technician, (Permissions.TicketsViewAssigned, PermissionScope.Assigned), (Permissions.TicketsCreate, all), (Permissions.TicketsUpdate, all),
            (Permissions.ScheduleView, PermissionScope.Own), (Permissions.ScheduleManage, PermissionScope.Own));
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
        var board = new Board { MspOrganizationId = OrgA, Name = "Internal", Key = "INT", Kind = BoardKind.Internal, NextNumber = 10 };
        var client = new ClientCompany { MspOrganizationId = OrgA, Name = "ABC Company", ExternalCompanyId = "abc", PsaConnectionId = Guid.NewGuid() };
        var w = new World(db, dbName, U("Harpal Admin", admin), U("Lena Lead", lead), U("Jason Carter", tech), U("Abbie Noor", tech), U("Sam Shah", tech),
            U("Other Org", tech, OrgB), noc, board, null!, null!, null!, null!, null!, h.Clock);
        Ticket T(string title, Guid? assignee, TicketOrigin origin = TicketOrigin.Internal, string number = "", ProviderType? provider = null, string? external = null) => new()
        {
            MspOrganizationId = OrgA, Origin = origin, BoardId = origin == TicketOrigin.Internal ? board.Id : null, Number = origin == TicketOrigin.Internal ? number : null,
            Provider = provider, ExternalTicketId = external, PsaConnectionId = provider is null ? null : client.PsaConnectionId,
            RequesterName = "Lena Lead", RequesterEmail = "lena@techpio.test", Title = title, PortalStatus = "IN_PROGRESS", PortalPriority = "NORMAL",
            AssignedAppUserId = assignee, ClientCompanyId = client.Id, SyncStatus = TicketSyncStatus.Synced, UpdateHash = "hash-" + title,
            CreatedByUserId = w.Lead.Id, AssignedTechnicianExternalId = provider is null ? null : "",
        };
        w = w with
        {
            JasonsTicket = T("Firewall review", w.Jason.Id, number: "INT-000001"),
            OpenTicket = T("Patch the NAS", null, number: "INT-000002"),
            // A PSA ticket held by Sam: a technician with scope Assigned cannot see it. (A ticket on an
            // open team board is shared with the whole team by design, so it is no test of restriction.)
            SamsTicket = T("Rotate the vault keys", w.Sam.Id, TicketOrigin.Psa, provider: ProviderType.AutotaskPsa, external: "777"),
            Autotask = T("Microsoft 365 issue", w.Jason.Id, TicketOrigin.Psa, provider: ProviderType.AutotaskPsa, external: "43829"),
            ConnectWise = T("Server maintenance", null, TicketOrigin.Psa, provider: ProviderType.ConnectWisePsa, external: "9923"),
        };
        db.AddRange(dept, noc, security, board, client, w.JasonsTicket, w.OpenTicket, w.SamsTicket, w.Autotask, w.ConnectWise);
        db.MspOrganizations.Add(new MspOrganization { Id = OrgA, Name = "TechPio", Slug = "techpio", TimeZone = Zone });
        db.UserTeams.AddRange(
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Lead.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Abbie.Id, TeamId = noc.Id },
            new UserTeam { MspOrganizationId = OrgA, AppUserId = w.Sam.Id, TeamId = security.Id });
        await db.SaveChangesAsync();

        var admins = w.As(w.Admin);
        foreach (var p in new[] { w.Jason, w.Abbie, w.Sam })
            await admins.Schedules.SaveAsync(w.Admin.Id, p.Id, new WorkScheduleInput(null, Zone,
                new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                    .Select(d => new WorkDayInput(d, "08:30", "17:30", [new WorkBreakDto("12:30", "13:30")])).ToList()));
        db.ChangeTracker.Clear();
        return w;
    }

    private static WorkAllocationInput Place(Ticket ticket, AppUser who, DateOnly date, string from, string to, bool fixedWork = false, string? note = null, string? reason = null)
        => new(ticket.Id, who.Id, At(date, from), At(date, to), fixedWork, note, reason);

    private static async Task<DayCapacityDto> DayAsync(World w, AppUser viewer, AppUser person, DateOnly date)
        => (await w.As(viewer).Capacity.ForPersonAsync(viewer.Id, person.Id, date, date)).Days.Single();

    // ---- FLOW 1 and 2: planning your own work --------------------------------------------------

    [Fact]
    public async Task A_technician_puts_their_own_ticket_into_their_own_time()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var ticketRowsBefore = await w.Db.Tickets.CountAsync();

        var planned = await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "15:30", "16:30", note: "After the change window"));

        (planned.Method, planned.ScheduledByName, planned.PlannedMinutes, planned.IsFixed, planned.Status).Should().Be((SchedulingMethod.Self, "Jason Carter", 60, false, WorkAllocationStatus.Planned));
        (planned.Reference, planned.Title, planned.ClientName, planned.TicketVisible).Should().Be(("INT-000001", "Firewall review", "ABC Company", true));
        (planned.CanEdit, planned.CanCancel, planned.CanReassign).Should().Be((true, true, false));
        // Capacity knows at once: the hour is taken and the free windows split around it.
        var day = await DayAsync(w, w.Jason, w.Jason, Monday);
        (day.ConfirmedMinutes, day.RemainingConfirmedMinutes).Should().Be((60, 420));
        day.FreeSlots.Select(s => (s.Start, s.End)).Should().Equal((At(Monday, "08:30"), At(Monday, "12:30")), (At(Monday, "13:30"), At(Monday, "15:30")), (At(Monday, "16:30"), At(Monday, "17:30")));
        // The plan lists it; the unscheduled list no longer does.
        var plan = await jason.Plans.PlanAsync(w.Jason.Id, w.Jason.Id, Monday, Monday);
        plan.Allocations.Select(a => a.Id).Should().Equal(planned.Id);
        (plan.CanPlan, plan.CanScheduleOthers, plan.CanOverride).Should().Be((true, false, false));
        (await jason.Plans.UnscheduledAsync(w.Jason.Id)).Select(u => u.Reference).Should().NotContain("INT-000001");

        // No ticket was created or changed: planning is not assignment, and nothing reaches a PSA.
        (await w.Db.Tickets.CountAsync()).Should().Be(ticketRowsBefore);
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id);
        (ticket.AssignedAppUserId, ticket.SyncStatus, ticket.UpdateHash, ticket.PortalStatus).Should().Be((w.Jason.Id, TicketSyncStatus.Synced, "hash-Firewall review", "IN_PROGRESS"));
        var entry = await w.Db.AuditLog.SingleAsync(a => a.Action == "workforce.allocation.created");
        entry.DetailJson.Should().Contain("\"method\":\"Self\"").And.Contain("INT-000001");
        (await w.Db.PushNotifications.CountAsync()).Should().Be(0, "nobody is told about their own planning");
    }

    [Fact]
    public async Task An_Autotask_ticket_is_planned_as_itself_with_nothing_copied_and_nothing_sent()
    {
        var w = await WorldAsync();
        var before = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.Autotask.Id);

        var planned = await w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, Place(w.Autotask, w.Jason, Monday, "09:30", "11:00"));

        (planned.TicketId, planned.Reference, planned.Title, planned.PlannedMinutes).Should().Be((w.Autotask.Id, "Autotask 43829", "Microsoft 365 issue", 90));
        (await w.Db.Tickets.CountAsync(t => t.ExternalTicketId == "43829")).Should().Be(1, "no second ticket for the same work");
        var after = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.Autotask.Id);
        (after.AssignedTechnicianExternalId, after.SyncStatus, after.UpdateHash, after.SlaDueAt, after.Version)
            .Should().Be((before.AssignedTechnicianExternalId, before.SyncStatus, before.UpdateHash, before.SlaDueAt, before.Version), "the PSA's facts are untouched");
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(0, "nothing was queued for the provider");
    }

    // ---- FLOW 3: scheduled by someone else ------------------------------------------------------

    [Fact]
    public async Task A_lead_schedules_a_ConnectWise_ticket_for_a_technician_who_then_sees_it_in_their_plan()
    {
        var w = await WorldAsync();
        w.Db.PushSubscriptions.Add(new PushSubscription { MspOrganizationId = OrgA, AppUserId = w.Jason.Id, Endpoint = "https://push.test/jason", P256dh = "k", Auth = "a" });
        await w.Db.SaveChangesAsync();

        var planned = await w.As(w.Lead).Plans.CreateAsync(w.Lead.Id, Place(w.ConnectWise, w.Jason, Monday, "14:00", "16:00"));

        (planned.Method, planned.ScheduledByName, planned.PersonName, planned.Reference).Should().Be((SchedulingMethod.AuthorizedUser, "Lena Lead", "Jason Carter", "ConnectWise 9923"));
        // Nobody held the ticket in the portal, so Jason now does - the only thing planning ever does
        // to a ticket, and only on the portal side.
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.ConnectWise.Id);
        (ticket.AssignedAppUserId, ticket.AssignedTechnicianExternalId, ticket.SyncStatus).Should().Be((w.Jason.Id, "", TicketSyncStatus.Synced));
        (await w.Db.TicketAssignments.SingleAsync(a => a.TicketId == ticket.Id)).ToAppUserId.Should().Be(w.Jason.Id);
        (await w.Db.AuditLog.Where(a => a.Action == "ticket.assigned.portal").SingleAsync()).DetailJson.Should().Contain("viaPlanning");

        // Jason sees it, with the ticket's facts, and may move it (it is flexible) but not take it out.
        var mine = await w.As(w.Jason).Plans.PlanAsync(w.Jason.Id, w.Jason.Id, Monday, Monday);
        var seen = mine.Allocations.Should().ContainSingle().Subject;
        (seen.TicketVisible, seen.Title, seen.ScheduledByName, seen.CanEdit, seen.CanCancel, seen.CanReassign).Should().Be((true, "Server maintenance", "Lena Lead", true, false, false));
        (await w.As(w.Jason).Plans.UnscheduledAsync(w.Jason.Id)).Select(u => u.Reference).Should().NotContain("ConnectWise 9923");
        // And he is told, through the push device he has, in the same channel as "assigned to you".
        var note = await w.Db.PushNotifications.SingleAsync();
        (note.AppUserId, note.Kind, note.Title).Should().Be((w.Jason.Id, PushKind.WorkPlanned, "Work planned for you: ConnectWise 9923"));
    }

    [Fact]
    public async Task Work_held_by_someone_else_is_not_quietly_moved_and_a_person_who_could_not_open_it_is_not_planned_on_it()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        // Jason (scope Assigned) cannot see Sam's ticket, and Sam holds it: refused, saying what to do.
        var act = () => lead.Plans.CreateAsync(w.Lead.Id, Place(w.SamsTicket, w.Jason, Monday, "09:00", "10:00"));
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("Jason Carter cannot open this ticket: it is held by Sam Shah. Hand it over to Jason Carter first, or plan it for Sam Shah.");
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.SamsTicket.Id)).AssignedAppUserId.Should().Be(w.Sam.Id);

        // Abbie cannot see Jason's Autotask ticket (scope Assigned), and Jason holds it: refused the same way.
        var helper = () => lead.Plans.CreateAsync(w.Lead.Id, Place(w.Autotask, w.Abbie, Monday, "09:00", "10:00"));
        await helper.Should().ThrowAsync<ValidationFailedException>();
        // A ticket on an open team board is everyone's to see, so a second person may be planned on it
        // without touching who holds it.
        var shared = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Abbie, Monday, "09:00", "10:00"));
        shared.TicketVisible.Should().BeTrue();
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id)).AssignedAppUserId.Should().Be(w.Jason.Id);
        // A ticket the person can already see (held by them in the PSA sense, or open to their team) is not re-held.
        var own = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.Autotask, w.Jason, Monday, "09:00", "10:00"));
        own.Method.Should().Be(SchedulingMethod.AuthorizedUser);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "ticket.assigned.portal")).Should().Be(0);
    }

    // ---- FLOW 4: not self-assignment -----------------------------------------------------------

    [Fact]
    public async Task Self_planning_reaches_only_work_the_person_may_already_see()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);

        // Sam's Autotask ticket is not Jason's to see, so it is not his to plan - and the answer gives
        // nothing away about whether it exists.
        var restricted = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.SamsTicket, w.Jason, Monday, "09:00", "10:00"));
        (await restricted.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        var guessed = () => jason.Plans.CreateAsync(w.Jason.Id, new WorkAllocationInput(Guid.NewGuid(), w.Jason.Id, At(Monday, "09:00"), At(Monday, "10:00")));
        (await guessed.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        // Nobody else's plan either, however it is addressed.
        var colleague = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Abbie, Monday, "09:00", "10:00"));
        (await colleague.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        (await w.Db.WorkAllocations.CountAsync()).Should().Be(0);
        // Work open to the team (nobody holds it) is theirs to plan - and planning it does not make them its holder.
        var queue = await jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "10:00"));
        queue.Method.Should().Be(SchedulingMethod.Self);
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.OpenTicket.Id)).AssignedAppUserId.Should().BeNull();
    }

    // ---- FLOW 5: fixed and flexible ------------------------------------------------------------

    [Fact]
    public async Task Fixed_work_stays_where_the_scheduler_put_it_and_flexible_work_may_be_moved_but_not_removed()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var jason = w.As(w.Jason);
        var fixedWork = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.ConnectWise, w.Jason, Monday, "09:00", "10:00", fixedWork: true));
        var flexible = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.Autotask, w.Jason, Monday, "14:00", "15:00"));

        var plan = await jason.Plans.PlanAsync(w.Jason.Id, w.Jason.Id, Monday, Monday);
        plan.Allocations.Single(a => a.Id == fixedWork.Id).Should().Match<WorkAllocationDto>(a => a.IsFixed && !a.CanEdit && !a.CanCancel);
        plan.Allocations.Single(a => a.Id == flexible.Id).Should().Match<WorkAllocationDto>(a => !a.IsFixed && a.CanEdit && !a.CanCancel);

        var moveFixed = () => jason.Plans.UpdateAsync(w.Jason.Id, fixedWork.Id, new WorkAllocationUpdate(At(Monday, "10:00"), At(Monday, "11:00"), fixedWork.Version));
        (await moveFixed.Should().ThrowAsync<ForbiddenException>()).WithMessage("*fixed in place*");
        var cancelFixed = () => jason.Plans.CancelAsync(w.Jason.Id, fixedWork.Id, null);
        await cancelFixed.Should().ThrowAsync<ForbiddenException>();
        var cancelFlexible = () => jason.Plans.CancelAsync(w.Jason.Id, flexible.Id, null);
        (await cancelFlexible.Should().ThrowAsync<ForbiddenException>()).WithMessage("*scheduled for you*");
        var unfix = () => jason.Plans.UpdateAsync(w.Jason.Id, flexible.Id, new WorkAllocationUpdate(At(Monday, "14:00"), At(Monday, "15:00"), flexible.Version, IsFixed: true));
        (await unfix.Should().ThrowAsync<ForbiddenException>()).WithMessage("*fix work in place*");

        // Jason moves the flexible one within his capacity; the lead moves the fixed one.
        var moved = await jason.Plans.UpdateAsync(w.Jason.Id, flexible.Id, new WorkAllocationUpdate(At(Monday, "15:00"), At(Monday, "16:30"), flexible.Version, Note: "Pushed back an hour"));
        (moved.StartsAt, moved.PlannedMinutes, moved.Version, moved.Note).Should().Be((At(Monday, "15:00"), 90, flexible.Version + 1, "Pushed back an hour"));
        var leadMoved = await lead.Plans.UpdateAsync(w.Lead.Id, fixedWork.Id, new WorkAllocationUpdate(At(Monday, "10:00"), At(Monday, "11:00"), fixedWork.Version));
        leadMoved.StartsAt.Should().Be(At(Monday, "10:00"));
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.moved")).Should().Be(2);
    }

    // ---- FLOW 6: reassignment --------------------------------------------------------------------

    [Fact]
    public async Task A_lead_gives_work_to_someone_else_and_their_capacity_is_checked_first()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var work = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.ConnectWise, w.Jason, Monday, "14:00", "16:00"));
        // Abbie is busy at that time.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Abbie, Monday, "15:00", "16:00"));

        var clash = () => lead.Plans.ReassignAsync(w.Lead.Id, work.Id, new WorkAllocationReassign(w.Abbie.Id, work.Version));
        (await clash.Should().ThrowAsync<ConflictException>()).WithMessage("This time is no longer available: Already has confirmed work during this period.");
        // At another time it goes through, and both people's capacity says so.
        var given = await lead.Plans.ReassignAsync(w.Lead.Id, work.Id, new WorkAllocationReassign(w.Abbie.Id, work.Version, At(Monday, "09:00"), At(Monday, "11:00")));
        (given.AppUserId, given.PersonName, given.StartsAt, given.Method, given.ScheduledByName, given.Version).Should().Be((w.Abbie.Id, "Abbie Noor", At(Monday, "09:00"), SchedulingMethod.AuthorizedUser, "Lena Lead", work.Version + 1));
        (await DayAsync(w, w.Lead, w.Jason, Monday)).ConfirmedMinutes.Should().Be(0);
        (await DayAsync(w, w.Lead, w.Abbie, Monday)).ConfirmedMinutes.Should().Be(180);
        (await w.Db.AuditLog.SingleAsync(a => a.Action == "workforce.allocation.reassigned")).DetailJson.Should().Contain("Jason Carter").And.Contain("Abbie Noor");

        // The ticket went with the work: Jason held it only because it had been planned for him.
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.ConnectWise.Id)).AssignedAppUserId.Should().Be(w.Abbie.Id);
        // Someone outside the lead's team is not found; a technician cannot give work away at all.
        var outside = () => lead.Plans.ReassignAsync(w.Lead.Id, given.Id, new WorkAllocationReassign(w.Sam.Id, given.Version));
        (await outside.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        var mine = await w.As(w.Abbie).Plans.CreateAsync(w.Abbie.Id, Place(w.OpenTicket, w.Abbie, Tuesday, "09:00", "10:00"));
        var tech = () => w.As(w.Abbie).Plans.ReassignAsync(w.Abbie.Id, mine.Id, new WorkAllocationReassign(w.Jason.Id, mine.Version));
        (await tech.Should().ThrowAsync<ForbiddenException>()).WithMessage("Only someone who schedules others can give work to someone else.");
    }

    // ---- FLOW 7: taking work out ---------------------------------------------------------------

    [Fact]
    public async Task Taking_work_out_of_the_plan_leaves_the_ticket_exactly_as_it_was()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var planned = await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));

        var gone = await jason.Plans.CancelAsync(w.Jason.Id, planned.Id, "Doing it Tuesday instead");
        (gone.Status, gone.CancelReason, gone.CancelledByName, gone.CanEdit, gone.CanCancel).Should().Be((WorkAllocationStatus.Cancelled, "Doing it Tuesday instead", "Jason Carter", false, false));
        (await DayAsync(w, w.Jason, w.Jason, Monday)).ConfirmedMinutes.Should().Be(0);
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id);
        (ticket.PortalStatus, ticket.AssignedAppUserId).Should().Be(("IN_PROGRESS", w.Jason.Id));
        (await jason.Plans.UnscheduledAsync(w.Jason.Id)).Should().Contain(u => u.Reference == "INT-000001");
        var again = () => jason.Plans.CancelAsync(w.Jason.Id, planned.Id, null);
        (await again.Should().ThrowAsync<ValidationFailedException>()).WithMessage("This work is already out of the plan.");
    }

    // ---- conflicts and overrides --------------------------------------------------------------

    [Fact]
    public async Task Work_outside_the_working_window_over_a_break_or_over_other_work_is_refused_unless_overridden()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var admin = w.As(w.Admin);
        await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));

        var overlap = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Jason, Monday, "09:30", "10:30"));
        var refused = (await overlap.Should().ThrowAsync<ConflictException>()).Which;
        refused.Message.Should().Be("This time is no longer available: Already has confirmed work during this period.");
        var problem = refused.Payload.Should().BeOfType<ConflictProblemDto>().Subject;
        (problem.CanOverride, problem.OverrideAllowedForCaller).Should().Be((true, false), "a technician has no override permission");
        problem.Conflicts.Should().ContainSingle().Which.Type.Should().Be(ConflictType.HardConflict);

        var lunch = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Jason, Monday, "12:00", "14:00"));
        (await lunch.Should().ThrowAsync<ConflictException>()).Which.Payload.As<ConflictProblemDto>().Conflicts.Select(c => c.Type).Should().Equal(ConflictType.BreakConflict);
        var evening = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Jason, Monday, "18:00", "19:00", reason: "Client-approved after-hours firewall maintenance"));
        (await evening.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("This time is no longer available");
        (await w.Db.WorkAllocations.CountAsync()).Should().Be(1, "none of it was written");

        // An administrator may override, with a reason that is kept with the work.
        var noReason = () => admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "18:00", "19:00"));
        (await noReason.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be("This time has a conflict: Outside this person's working hours. Give a reason to override it.");
        var overridden = await admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "18:00", "19:00", reason: "Client-approved after-hours firewall maintenance"));
        (overridden.OverrideReason, overridden.OverriddenByName).Should().Be(("Client-approved after-hours firewall maintenance", "Harpal Admin"));
        overridden.OverriddenConflicts.Should().Equal(ConflictType.OutsideWorkingWindow);
        (await w.Db.AuditLog.Where(a => a.Action == "workforce.allocation.created").Select(a => a.DetailJson!).ToListAsync())
            .Should().Contain(json => json.Contains("Client-approved after-hours") && json.Contains("OutsideWorkingWindow"));
    }

    [Fact]
    public async Task A_refused_placement_changes_nothing_about_the_ticket_and_a_refused_internal_task_raises_none()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var jason = w.As(w.Jason);
        var tickets = await w.Db.Tickets.CountAsync();

        // Nobody holds the NAS ticket. The lead plans it for Jason over his lunch: refused - and
        // Jason must not have become its holder on the way to being refused.
        var lunch = () => lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Jason, Monday, "12:00", "13:00"));
        await lunch.Should().ThrowAsync<ConflictException>();
        var nas = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.OpenTicket.Id);
        nas.AssignedAppUserId.Should().BeNull("a refused plan is no assignment");
        (await w.Db.TicketAssignments.CountAsync(a => a.TicketId == w.OpenTicket.Id)).Should().Be(0);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "ticket.assigned.portal")).Should().Be(0);
        (await w.Db.WorkAllocations.CountAsync()).Should().Be(0);

        // Placed cleanly, the same plan makes him the holder, in the same breath, with its audit entry.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Jason, Monday, "14:00", "15:00"));
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.OpenTicket.Id)).AssignedAppUserId.Should().Be(w.Jason.Id);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "ticket.assigned.portal")).Should().Be(1);

        // A quick internal task over the break is refused before any ticket is raised.
        var task = () => jason.Plans.CreateInternalWorkAsync(w.Jason.Id, new InternalWorkInput(w.Board.Id, "Tidy the rack", null, null, At(Monday, "12:30"), At(Monday, "13:00")));
        await task.Should().ThrowAsync<ConflictException>();
        (await w.Db.Tickets.CountAsync()).Should().Be(tickets, "nothing was raised for a plan that was refused");
        (await w.Db.WorkAllocations.CountAsync(a => a.AppUserId == w.Jason.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_change_made_while_the_screen_was_open_is_a_stale_screen_not_a_crash()
    {
        var w = await WorldAsync();
        var planned = await w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        // Two screens, each on its own database context, both showing version 1.
        var screenA = World.For(AdminHarness.Create(OrgA, w.DbName).Db, OrgA, w.Jason, w.Clock).Plans;
        var screenB = World.For(AdminHarness.Create(OrgA, w.DbName).Db, OrgA, w.Jason, w.Clock).Plans;
        await screenB.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "10:00"), At(Monday, "11:00"), planned.Version));

        // Screen A still believes version 1: told to reload, not an unexpected error.
        var move = () => screenA.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "11:00"), At(Monday, "12:00"), planned.Version));
        (await move.Should().ThrowAsync<ConflictException>()).Which.Payload.As<ConflictProblemDto>().Stale.Should().BeTrue();
        // And a cancel from a screen that read it before someone else's cancel: told it is already out.
        await screenB.CancelAsync(w.Jason.Id, planned.Id, null);
        var cancel = () => screenA.CancelAsync(w.Jason.Id, planned.Id, null);
        (await cancel.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("This work is already out of the plan.");
        (await w.Db.WorkAllocations.AsNoTracking().SingleAsync()).Should().Match<WorkAllocation>(a => a.Status == WorkAllocationStatus.Cancelled && a.StartsAt == At(Monday, "10:00"));
    }

    [Fact]
    public async Task Time_away_blocks_everyone_and_exact_boundaries_are_fine()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Exceptions.AddAsync(w.Admin.Id, w.Jason.Id, new CapacityExceptionInput(CapacityExceptionKind.Unavailable, false, Monday, null, "15:00", "16:00", CapacityExceptionReason.Appointment, null));

        var away = () => admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "15:30", "16:30", reason: "It is urgent"));
        var refused = (await away.Should().ThrowAsync<ConflictException>()).Which;
        refused.Message.Should().StartWith("This time cannot be used: Marked unavailable");
        refused.Payload.As<ConflictProblemDto>().CanOverride.Should().BeFalse("even an administrator changes the time away instead");

        await admin.Plans.CreateAsync(w.Admin.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        var touching = await admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "10:00", "11:00"));
        touching.OverrideReason.Should().BeNull("work that starts as the other ends does not overlap it");
        (await DayAsync(w, w.Admin, w.Jason, Monday)).ConfirmedMinutes.Should().Be(120);
    }

    [Fact]
    public async Task A_night_technicians_work_is_planned_across_midnight()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Schedules.SaveAsync(w.Admin.Id, w.Abbie.Id, new WorkScheduleInput(null, Zone,
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday }.Select(d => new WorkDayInput(d, "18:00", "03:00", [new WorkBreakDto("22:00", "22:30")])).ToList()));

        var night = await admin.Plans.CreateAsync(w.Admin.Id, new WorkAllocationInput(w.OpenTicket.Id, w.Abbie.Id, At(Monday, "23:00"), At(Tuesday, "01:00")));
        night.PlannedMinutes.Should().Be(120);
        var day = await DayAsync(w, w.Admin, w.Abbie, Monday);
        (day.ConfirmedMinutes, day.RemainingConfirmedMinutes).Should().Be((120, 390));
        (await admin.Plans.PlanAsync(w.Admin.Id, w.Abbie.Id, Monday, Monday)).Allocations.Should().ContainSingle("it belongs to the night that started on Monday");
    }

    [Theory]
    [InlineData("10:00", "10:00", 0, "The work must end after it starts.")]
    [InlineData("08:00", "09:00", 2, "One piece of planned work can be at most 24 hours.")]
    [InlineData("08:00", "09:00", 400, "Work can be planned at most a year ahead.")]
    public async Task A_period_is_checked_before_anything_is_read(string from, string to, int daysLater, string message)
    {
        var w = await WorldAsync();
        var start = At(Monday.AddDays(daysLater >= 100 ? daysLater : 0), from);
        var end = At(Monday.AddDays(daysLater), to);
        var act = () => w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, new WorkAllocationInput(w.JasonsTicket.Id, w.Jason.Id, start, end));
        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage(message);
    }

    [Fact]
    public async Task A_stale_screen_cannot_overwrite_a_change_made_since()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var planned = await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        await jason.Plans.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "10:00"), At(Monday, "11:00"), planned.Version));

        var stale = () => jason.Plans.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "11:00"), At(Monday, "12:00"), planned.Version));
        var refused = (await stale.Should().ThrowAsync<ConflictException>()).Which;
        refused.Message.Should().Be("This plan changed since you opened it. Reload and try again.");
        refused.Payload.As<ConflictProblemDto>().Stale.Should().BeTrue();
    }

    [Fact]
    public async Task A_note_is_plain_text_and_a_finished_ticket_cannot_be_planned()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        const string script = "<script>alert(1)</script>";
        (await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00", note: script))).Note.Should().Be(script);

        w.Db.Tickets.Single(t => t.Id == w.OpenTicket.Id).PortalStatus = "RESOLVED";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var done = () => jason.Plans.CreateAsync(w.Jason.Id, Place(w.OpenTicket, w.Jason, Monday, "10:00", "11:00"));
        (await done.Should().ThrowAsync<ValidationFailedException>()).WithMessage("This ticket is finished; there is nothing left to plan.");
    }

    // ---- internal work ---------------------------------------------------------------------------

    [Fact]
    public async Task A_small_piece_of_internal_work_is_raised_on_the_board_and_planned_in_one_step()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);

        var planned = await jason.Plans.CreateInternalWorkAsync(w.Jason.Id, new InternalWorkInput(w.Board.Id, "Review Microsoft 365 Secure Score", null, null, At(Monday, "15:00"), At(Monday, "16:00")));

        (planned.Title, planned.PlannedMinutes, planned.Method).Should().Be(("Review Microsoft 365 Secure Score", 60, SchedulingMethod.Self));
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == planned.TicketId);
        (ticket.Origin, ticket.BoardId, ticket.AssignedAppUserId, ticket.CreatedByUserId).Should().Be((TicketOrigin.Internal, w.Board.Id, w.Jason.Id, w.Jason.Id));
        ticket.Number.Should().StartWith("INT-");
        // There is no such thing as planned time without work behind it.
        (await w.Db.WorkAllocations.Include(a => a.Ticket).SingleAsync(a => a.Id == planned.Id)).Ticket!.Title.Should().Be("Review Microsoft 365 Secure Score");

        var noTitle = () => jason.Plans.CreateInternalWorkAsync(w.Jason.Id, new InternalWorkInput(w.Board.Id, "  ", null, null, At(Monday, "16:00"), At(Monday, "17:00")));
        await noTitle.Should().ThrowAsync<ValidationFailedException>();
    }

    // ---- what finishes leaves the plan -----------------------------------------------------------

    [Fact]
    public async Task Work_that_finishes_leaves_future_plans_and_stays_in_past_ones()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        var future = await admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "10:00"));
        // Earlier work on the same ticket, already in the past when it finishes.
        var past = await admin.Plans.CreateAsync(w.Admin.Id, Place(w.JasonsTicket, w.Jason, Today, "09:00", "10:00"));
        w.Clock.Advance(TimeSpan.FromHours(12));

        foreach (var id in new[] { w.OpenTicket.Id, w.JasonsTicket.Id })
            w.Db.Tickets.Single(t => t.Id == id).PortalStatus = "CLOSED";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        // The capacity engine stops counting the future one at once...
        (await DayAsync(w, w.Admin, w.Jason, Monday)).ConfirmedMinutes.Should().Be(0);
        // ...and the worker's pass records it, leaving the past one as history.
        (await admin.Releaser.ReleaseFinishedAsync()).Should().Be(1);
        var rows = await w.Db.WorkAllocations.AsNoTracking().ToDictionaryAsync(a => a.Id);
        (rows[future.Id].Status, rows[future.Id].CancelReason).Should().Be((WorkAllocationStatus.Cancelled, "The work finished before its planned time."));
        rows[past.Id].Status.Should().Be(WorkAllocationStatus.Planned);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.released")).Should().Be(1);
        (await admin.Releaser.ReleaseFinishedAsync()).Should().Be(0, "nothing left to release");
    }

    // ---- unscheduled work ------------------------------------------------------------------------

    [Fact]
    public async Task Unscheduled_work_is_mine_open_and_not_yet_in_my_future_plan()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var before = await jason.Plans.UnscheduledAsync(w.Jason.Id);
        before.Select(u => u.Reference).Should().BeEquivalentTo(["INT-000001", "Autotask 43829"], "what I hold; Sam's ticket and the unheld queue are not mine");
        before.Single(u => u.Reference == "Autotask 43829").Should().Match<UnscheduledWorkDto>(u => u.Source == "Autotask" && u.AssignedToMe && u.ClientName == "ABC Company");

        await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Today, "09:00", "10:00"));
        w.Clock.Advance(TimeSpan.FromHours(12));
        // Planned only in the past: still unscheduled, and it says how much was planned before.
        var after = await jason.Plans.UnscheduledAsync(w.Jason.Id);
        after.Single(u => u.Reference == "INT-000001").PlannedMinutesSoFar.Should().Be(60);
        // Work sitting with my team counts as mine to plan.
        w.Db.Tickets.Single(t => t.Id == w.OpenTicket.Id).AssignedTeamId = w.Noc.Id;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        (await jason.Plans.UnscheduledAsync(w.Jason.Id)).Should().Contain(u => u.Reference == "INT-000002" && u.TeamName == "NOC" && !u.AssignedToMe);
    }

    [Fact]
    public async Task The_people_someone_may_plan_for_follow_their_scope()
    {
        var w = await WorldAsync();
        (await w.As(w.Jason).Plans.PlannablePeopleAsync(w.Jason.Id)).Select(p => (p.DisplayName, p.IsSelf)).Should().Equal(("Jason Carter", true));
        (await w.As(w.Lead).Plans.PlannablePeopleAsync(w.Lead.Id)).Select(p => p.DisplayName).Should().Equal("Abbie Noor", "Jason Carter", "Lena Lead");
        (await w.As(w.Admin).Plans.PlannablePeopleAsync(w.Admin.Id)).Should().HaveCount(5);
    }

    [Fact]
    public async Task What_is_planned_on_a_ticket_shows_only_people_the_asker_may_see()
    {
        var w = await WorldAsync();
        var admin = w.As(w.Admin);
        await admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "10:00"));
        await admin.Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Sam, Monday, "09:00", "10:00"));

        (await admin.Plans.ForTicketAsync(w.Admin.Id, w.OpenTicket.Id)).Select(a => a.PersonName).Should().BeEquivalentTo(["Jason Carter", "Sam Shah"]);
        (await w.As(w.Lead).Plans.ForTicketAsync(w.Lead.Id, w.OpenTicket.Id)).Select(a => a.PersonName).Should().BeEquivalentTo(["Jason Carter"], "the lead sees the NOC, not Sam");
        (await w.As(w.Jason).Plans.ForTicketAsync(w.Jason.Id, w.OpenTicket.Id)).Select(a => a.PersonName).Should().Equal("Jason Carter");
        var hidden = () => w.As(w.Jason).Plans.ForTicketAsync(w.Jason.Id, w.SamsTicket.Id);
        (await hidden.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        // Sam cannot see his own board-planned colleague... but he can see himself: his own planned work only.
        (await w.As(w.Sam).Plans.ForTicketAsync(w.Sam.Id, w.OpenTicket.Id)).Select(a => a.PersonName).Should().Equal("Sam Shah");
    }

    // ---- FLOW 10: two at once --------------------------------------------------------------------

    [Fact]
    public async Task Two_planners_taking_the_same_free_hour_at_once_end_with_one_booking()
    {
        var w = await WorldAsync();
        // Each planner on their own database context, as two requests are.
        var leadDb = AdminHarness.Create(OrgA, w.DbName).Db;
        var jasonDb = AdminHarness.Create(OrgA, w.DbName).Db;
        var lead = World.For(leadDb, OrgA, w.Lead, w.Clock).Plans;
        var jason = World.For(jasonDb, OrgA, w.Jason, w.Clock).Plans;
        var slot = (Start: At(Monday, "15:00"), End: At(Monday, "16:00"));

        // Both saw 15:00-16:00 free a moment ago.
        (await lead.CreateAsync(w.Lead.Id, new WorkAllocationInput(w.ConnectWise.Id, w.Jason.Id, slot.Start, slot.End), default)).Should().NotBeNull();
        await lead.CancelAsync(w.Lead.Id, (await leadDb.WorkAllocations.SingleAsync()).Id, null);
        var attempts = await Task.WhenAll(
            Try(() => lead.CreateAsync(w.Lead.Id, new WorkAllocationInput(w.ConnectWise.Id, w.Jason.Id, slot.Start, slot.End))),
            Try(() => jason.CreateAsync(w.Jason.Id, new WorkAllocationInput(w.JasonsTicket.Id, w.Jason.Id, slot.Start, slot.End))));

        attempts.Count(a => a.Ok).Should().Be(1, "one wins");
        attempts.Single(a => !a.Ok).Error.Should().BeOfType<ConflictException>().Which.Message.Should().Contain("no longer available");
        (await w.Db.WorkAllocations.CountAsync(a => a.Status == WorkAllocationStatus.Planned && a.StartsAt == slot.Start)).Should().Be(1);

        static async Task<(bool Ok, Exception? Error)> Try(Func<Task<WorkAllocationDto>> call)
        {
            try { await call(); return (true, null); }
            catch (Exception ex) { return (false, ex); }
        }
    }

    // ---- FLOW 8 and 9: who cannot reach any of it -------------------------------------------------

    [Fact]
    public async Task A_sign_in_that_is_not_a_staff_account_is_refused_by_every_planning_action()
    {
        var everyClaim = new HashSet<string> { Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.ScheduleOverride };
        var notStaff = new Desk.Api.Controllers.WorkforcePlanController(null!, new TestCurrentUser(OrgA, permissions: everyClaim, userId: null), new WorkforceFeatureOptions { Enabled = true });
        var id = Guid.NewGuid();
        Func<Task>[] calls =
        [
            () => notStaff.Plan(id, null, null, default), () => notStaff.Unscheduled(default), () => notStaff.PlannablePeople(default), () => notStaff.ForTicket(id, default),
            () => notStaff.Create(new WorkAllocationInput(id, id, At(Monday, "09:00"), At(Monday, "10:00")), default),
            () => notStaff.CreateInternalWork(new InternalWorkInput(id, "x", null, null, At(Monday, "09:00"), At(Monday, "10:00")), default),
            () => notStaff.Update(id, new WorkAllocationUpdate(At(Monday, "09:00"), At(Monday, "10:00"), 0), default),
            () => notStaff.Reassign(id, new WorkAllocationReassign(id, 0), default), () => notStaff.Cancel(id, null, default),
        ];
        foreach (var call in calls)
            (await call.Should().ThrowAsync<ForbiddenException>()).WithMessage("Only staff accounts can use the workforce module.");
        var off = new Desk.Api.Controllers.WorkforcePlanController(null!, new TestCurrentUser(OrgA, permissions: everyClaim, userId: Guid.NewGuid()), new WorkforceFeatureOptions { Enabled = false });
        var hidden = () => off.Unscheduled(default);
        (await hidden.Should().ThrowAsync<NotFoundException>()).WithMessage("Workforce was not found.");
    }

    [Fact]
    public async Task Another_organizations_plans_cannot_be_read_written_or_detected()
    {
        var w = await WorldAsync();
        var planned = await w.As(w.Admin).Plans.CreateAsync(w.Admin.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "10:00"));

        var other = AdminHarness.Create(OrgB, w.DbName);
        var adminB = new AppUser { MspOrganizationId = OrgB, DisplayName = "Bea Admin", Email = "bea@other.test", IsActive = true };
        var roleB = new Role { MspOrganizationId = OrgB, Name = "Administrator", BuiltInType = RoleType.MspAdministrator };
        foreach (var key in new[] { Permissions.TicketsViewAll, Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.ScheduleOverride })
            roleB.Permissions.Add(new RolePermission { PermissionKey = key, Scope = PermissionScope.All });
        adminB.Roles.Add(new UserRole { RoleId = roleB.Id });
        other.Db.AddRange(roleB, adminB);
        await other.Db.SaveChangesAsync();
        var b = World.For(other.Db, OrgB, adminB, other.Clock).Plans;

        // A's person, A's ticket and A's allocation: each "not found", the same words as for an id that names nothing.
        var person = () => b.PlanAsync(adminB.Id, w.Jason.Id, Monday, Monday);
        (await person.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        var create = () => b.CreateAsync(adminB.Id, Place(w.OpenTicket, w.Jason, Monday, "10:00", "11:00"));
        (await create.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        var ownPersonTheirTicket = () => b.CreateAsync(adminB.Id, new WorkAllocationInput(w.OpenTicket.Id, adminB.Id, At(Monday, "10:00"), At(Monday, "11:00")));
        (await ownPersonTheirTicket.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        foreach (var id in new[] { planned.Id, Guid.NewGuid() })
        {
            var update = () => b.UpdateAsync(adminB.Id, id, new WorkAllocationUpdate(At(Monday, "10:00"), At(Monday, "11:00"), 0));
            (await update.Should().ThrowAsync<NotFoundException>()).WithMessage("Planned work was not found.");
            var reassign = () => b.ReassignAsync(adminB.Id, id, new WorkAllocationReassign(adminB.Id, 0));
            (await reassign.Should().ThrowAsync<NotFoundException>()).WithMessage("Planned work was not found.");
            var cancel = () => b.CancelAsync(adminB.Id, id, null);
            (await cancel.Should().ThrowAsync<NotFoundException>()).WithMessage("Planned work was not found.");
        }
        var onTicket = () => b.ForTicketAsync(adminB.Id, w.OpenTicket.Id);
        (await onTicket.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        (await b.UnscheduledAsync(adminB.Id)).Should().BeEmpty();
        (await w.Db.WorkAllocations.AsNoTracking().SingleAsync()).Should().Match<WorkAllocation>(a => a.Id == planned.Id && a.Status == WorkAllocationStatus.Planned && a.Version == 0);
    }
}
