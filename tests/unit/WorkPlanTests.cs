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
public partial class WorkPlanTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 1, 1);
    private static readonly DateOnly Monday = new(2026, 1, 5);
    private static readonly DateOnly Tuesday = new(2026, 1, 6);
    private const string Zone = "Asia/Kolkata";
    private static readonly TimeZoneInfo Tz = TimeZones.Resolve(Zone);
    private static DateTimeOffset At(DateOnly date, string hm) => TimeZones.WallToUtc(date.ToDateTime(TimeOnly.Parse(hm)), Tz, true);

    private sealed record Services(WorkPlanService Plans, CapacityService Capacity, WorkScheduleService Schedules, CapacityExceptionService Exceptions, WorkAllocationReleaser Releaser,
        WorkTimeService Time, TicketTimeWriter Writer, TicketScopeQuery Scope, AuditWriter Audit, WorkforceAnalyticsService Analytics);

    private sealed record World(DeskDbContext Db, string DbName, AppUser Admin, AppUser Lead, AppUser Jason, AppUser Abbie, AppUser Sam, AppUser Outsider,
        Team Noc, Board Board, Ticket JasonsTicket, Ticket OpenTicket, Ticket SamsTicket, Ticket Autotask, Ticket ConnectWise, TestClock Clock)
    {
        /// <summary>The PSA every Autotask ticket's time goes to, shared by every service built from this world.</summary>
        public StubConnector Connector { get; } = new();

        public Services As(AppUser who) => For(Db, OrgA, who, Clock, Connector);

        public static Services For(DeskDbContext db, Guid org, AppUser who, TimeProvider clock, StubConnector? connector = null)
        {
            var tenant = new Desk.Infrastructure.Tenancy.TenantContext();
            tenant.SetTenant(org);
            var user = new TestCurrentUser(org, userId: who.Id, name: who.DisplayName);
            var permissions = new EffectivePermissionService(db);
            var access = new WorkforceAccess(db, tenant, permissions);
            var scope = new TicketScopeQuery(db, permissions);
            var audit = new AuditWriter(db, user, tenant, clock);
            var capacity = new CapacityService(db, access, new WorkAllocationReader(db, scope, clock), clock);
            var gate = new PlanningGate(db);
            var plans = new WorkPlanService(db, access, capacity, scope, new InternalTicketService(db, tenant, clock, new RecordingActivity()), gate, audit, clock);
            var writer = new TicketTimeWriter(db, null!, audit);
            var time = new WorkTimeService(db, access, capacity, scope, plans, new StubResolver(connector ?? new StubConnector()), writer, permissions, gate, audit, clock);
            return new Services(plans, capacity, new WorkScheduleService(db, access, audit, clock), new CapacityExceptionService(db, access, audit, clock),
                new WorkAllocationReleaser(db, audit, clock), time, writer, scope, audit,
                new WorkforceAnalyticsService(db, access, capacity, scope, permissions, audit, clock));
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
        var admin = R("Administrator", RoleType.MspAdministrator, (Permissions.TicketsViewAll, all), (Permissions.TicketsUpdate, all), (Permissions.TicketsLogTime, all), (Permissions.BoardsManage, all),
            (Permissions.ScheduleView, all), (Permissions.WorkforceManage, all), (Permissions.AvailabilityManage, all), (Permissions.ScheduleManage, all), (Permissions.ScheduleOverride, all),
            (Permissions.WorkforceAnalyticsExport, all));
        var lead = R("Team lead", RoleType.Manager, (Permissions.TicketsViewAll, all), (Permissions.TicketsUpdate, all), (Permissions.TicketsLogTime, all), (Permissions.BoardsManage, all),
            (Permissions.ScheduleView, PermissionScope.Team), (Permissions.AvailabilityManage, PermissionScope.Team), (Permissions.ScheduleManage, PermissionScope.Team));
        var tech = R("Technician", RoleType.Technician, (Permissions.TicketsViewAssigned, PermissionScope.Assigned), (Permissions.TicketsCreate, all), (Permissions.TicketsUpdate, all), (Permissions.TicketsLogTime, all),
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

    /// <summary>
    /// "Autotask 43829" says which ticket only while there is one Autotask account. With two, both
    /// can have a 43829, so the reference names the connection - and with one, it reads as it did.
    /// </summary>
    [Fact]
    public async Task A_reference_names_the_connection_once_there_are_two_accounts_of_the_same_PSA()
    {
        var w = await WorldAsync();
        var a = new PsaConnection { MspOrganizationId = OrgA, Name = "Customer A", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://a", CredentialSecretRef = "mem://a" };
        w.Db.PsaConnections.Add(a);
        (await w.Db.Tickets.SingleAsync(t => t.Id == w.Autotask.Id)).PsaConnectionId = a.Id;
        await w.Db.SaveChangesAsync();

        var one = await w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, Place(w.Autotask, w.Jason, Monday, "09:30", "10:30"));
        one.Reference.Should().Be("Autotask 43829", "one Autotask account: its name adds nothing");

        // Put away still counts: its tickets are still here to be told apart from.
        w.Db.PsaConnections.Add(new PsaConnection
        {
            MspOrganizationId = OrgA, Name = "Customer B", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://b",
            CredentialSecretRef = "mem://b", ArchivedAt = w.Clock.GetUtcNow(), IsEnabled = false,
        });
        await w.Db.SaveChangesAsync();

        var two = await w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, Place(w.Autotask, w.Jason, Monday, "11:00", "12:00"));
        two.Reference.Should().Be("Customer A 43829");
        (await w.As(w.Jason).Plans.ForTicketAsync(w.Jason.Id, w.Autotask.Id)).Select(p => p.Reference).Distinct().Should().Equal("Customer A 43829");
        (await w.Db.AuditLog.OrderBy(x => x.CreatedAt).Where(x => x.Action == "workforce.allocation.created").Select(x => x.DetailJson).ToListAsync())
            .Last().Should().Contain("Customer A 43829");
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

    // ---- Phase 4: the team scheduler's data ---------------------------------------------------------

    [Fact]
    public async Task The_team_scheduler_lists_the_people_the_asker_may_see_with_their_days_and_what_is_planned()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var placed = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "11:00", fixedWork: true));
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Abbie, Tuesday, "14:00", "15:00"));

        // The lead's reach is the NOC: Jason and Abbie (and the lead, who has no schedule), never Sam.
        var day = await lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday, Monday));
        day.People.Select(p => p.DisplayName).Should().BeEquivalentTo("Jason Carter", "Abbie Noor", "Lena Lead");
        day.People.Should().NotContain(p => p.AppUserId == w.Sam.Id);
        var jason = day.People.Single(p => p.AppUserId == w.Jason.Id);
        jason.Days.Should().ContainSingle().Which.ConfirmedMinutes.Should().Be(120);
        jason.Allocations.Should().ContainSingle().Which.Should().Match<WorkAllocationDto>(a => a.Id == placed.Id && a.IsFixed && a.Reference == "INT-000001" && a.CanEdit && a.CanReassign);
        jason.CanPlan.Should().BeTrue("the lead schedules the NOC");
        day.People.Single(p => p.AppUserId == w.Abbie.Id).Allocations.Should().BeEmpty("her work is on Tuesday");
        (day.UsableMinutes, day.ConfirmedMinutes, day.RemainingConfirmedMinutes, day.AllocationCount).Should().Be((2 * 480, 120, 2 * 480 - 120, 1), "sums are over people offered for work");
        (day.CanScheduleOthers, day.CanOverride).Should().Be((true, false));

        // A week: both pieces of work, each on its own day.
        var week = await lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday, Monday.AddDays(6)));
        week.People.Single(p => p.AppUserId == w.Jason.Id).Days.Should().HaveCount(7);
        week.AllocationCount.Should().Be(2);
        week.People.Single(p => p.AppUserId == w.Abbie.Id).Allocations.Should().ContainSingle().Which.Reference.Should().Be("INT-000002");

        // Narrowed to a team the lead cannot see: nobody, not Sam.
        var security = await w.Db.Teams.SingleAsync(t => t.Name == "Security");
        (await lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday, Monday, TeamId: security.Id))).People.Should().BeEmpty();
        (await lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday, Monday, TeamId: Guid.NewGuid()))).People.Should().BeEmpty();

        // A technician's team scheduler is themselves: the Own scope reaches nobody else.
        var mine = await w.As(w.Jason).Plans.TeamAsync(w.Jason.Id, new TeamPlanQuery(Monday, Monday));
        mine.People.Should().ContainSingle().Which.AppUserId.Should().Be(w.Jason.Id);
        mine.People.Single().CanPlan.Should().BeTrue("their own plan");
        mine.CanScheduleOthers.Should().BeFalse();
        mine.People.Single().Allocations.Single().Should().Match<WorkAllocationDto>(a => !a.CanEdit && !a.CanCancel && !a.CanReassign, "fixed work scheduled for them");

        // Limits: at most two weeks, and within a year.
        var wide = () => lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday, Monday.AddDays(14)));
        (await wide.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Ask for at most 14 days at a time.");
        var far = () => lead.Plans.TeamAsync(w.Lead.Id, new TeamPlanQuery(Monday.AddYears(2), Monday.AddYears(2)));
        await far.Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task Team_unscheduled_work_is_what_the_group_holds_or_is_routed_and_nobody_has_planned()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);

        // Jason holds his ticket and the Autotask one; the NAS ticket is held by nobody and routed nowhere yet.
        var before = await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery());
        before.Select(t => t.Reference).Should().BeEquivalentTo("INT-000001", "Autotask 43829");
        before.Single(t => t.Reference == "INT-000001").Should().Match<TeamUnscheduledWorkDto>(t => t.HolderId == w.Jason.Id && t.HolderName == "Jason Carter" && t.TeamName == null);
        before.Should().NotContain(t => t.Reference == "Autotask 777", "Sam is outside the lead's reach");

        // Routed to the NOC, the NAS ticket sits with the group.
        var nas = await w.Db.Tickets.SingleAsync(t => t.Id == w.OpenTicket.Id);
        nas.AssignedTeamId = w.Noc.Id;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var routed = await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery(TeamId: w.Noc.Id));
        routed.Single(t => t.Reference == "INT-000002").Should().Match<TeamUnscheduledWorkDto>(t => t.HolderId == null && !t.HeldOutside && t.TeamId == w.Noc.Id && t.TeamName == "NOC");
        // Routed to the NOC but held by Sam, whom the lead may not see: said to be held, by nobody named.
        nas = await w.Db.Tickets.SingleAsync(t => t.Id == w.OpenTicket.Id);
        nas.AssignedAppUserId = w.Sam.Id;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        (await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery(TeamId: w.Noc.Id))).Single(t => t.Reference == "INT-000002")
            .Should().Match<TeamUnscheduledWorkDto>(t => t.HolderId == null && t.HolderName == null && t.HeldOutside);
        var unknownSkill = () => lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery(SkillIds: [Guid.NewGuid()]));
        (await unknownSkill.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("One of the skills asked for is not in the skill catalogue.");

        // Planned - for anyone - it leaves the list; planned only in the past, it is back.
        var placed = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Abbie, Monday, "09:00", "10:00"));
        (await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery())).Should().NotContain(t => t.Reference == "INT-000001");
        await lead.Plans.CancelAsync(w.Lead.Id, placed.Id, null);
        (await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery())).Should().Contain(t => t.Reference == "INT-000001");

        // A technician sees only the work in their own hands; a team they are not in adds nothing.
        var mine = await w.As(w.Jason).Plans.UnscheduledTeamAsync(w.Jason.Id, new TeamPlanQuery());
        mine.Select(t => t.Reference).Should().BeEquivalentTo("INT-000001", "Autotask 43829", "INT-000002");
        (await w.As(w.Jason).Plans.UnscheduledTeamAsync(w.Jason.Id, new TeamPlanQuery(TeamId: Guid.NewGuid()))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_resize_keeps_the_start_changes_the_planned_duration_and_is_audited_as_a_resize()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        var planned = await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "14:00", "15:00"));
        var longer = await jason.Plans.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "14:00"), At(Monday, "16:00"), planned.Version));
        (longer.StartsAt, longer.PlannedMinutes).Should().Be((At(Monday, "14:00"), 120));
        (await DayAsync(w, w.Jason, w.Jason, Monday)).ConfirmedMinutes.Should().Be(120);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.resized")).Should().Be(1);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.moved")).Should().Be(0);
        // Into the break it cannot grow without an override, and the technician has none.
        var tooLong = () => jason.Plans.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "14:00"), At(Monday, "17:00"), longer.Version));
        await tooLong.Should().NotThrowAsync("17:00 is still inside the window");
        var past = () => jason.Plans.UpdateAsync(w.Jason.Id, planned.Id, new WorkAllocationUpdate(At(Monday, "14:00"), At(Monday, "18:00"), longer.Version + 1));
        (await past.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("This time is no longer available");
    }

    // ---- Phase 5: tentative work, requirements, the queue, previews ------------------------------

    [Fact]
    public async Task Tentative_work_takes_tentative_capacity_only_and_is_confirmed_after_a_fresh_check()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var jason = w.As(w.Jason);
        var pencilled = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "11:00") with { Tentative = true });
        pencilled.Status.Should().Be(WorkAllocationStatus.Tentative);
        pencilled.CanConfirm.Should().BeTrue("the lead schedules Jason");

        // Confirmed capacity is untouched; projected capacity carries it.
        var day = await DayAsync(w, w.Lead, w.Jason, Monday);
        (day.ConfirmedMinutes, day.TentativeMinutes, day.RemainingConfirmedMinutes, day.ProjectedRemainingMinutes).Should().Be((0, 120, 480, 360));
        day.FreeSlots.Should().HaveCount(2, "the morning is still free for confirmed work");
        day.ProjectedFreeSlots.Select(s => s.Start).Should().Contain(At(Monday, "08:30")).And.Contain(At(Monday, "11:00"));
        // His own plan shows it as pencilled in, and he may confirm it himself (flexible, scheduled for him).
        var mine = await jason.Plans.PlanAsync(w.Jason.Id, w.Jason.Id, Monday, Monday);
        mine.Allocations.Should().ContainSingle().Which.Should().Match<WorkAllocationDto>(a => a.Status == WorkAllocationStatus.Tentative && a.CanConfirm);
        // Pencilled-in work does not keep the ticket in the queue.
        (await lead.Plans.UnscheduledTeamAsync(w.Lead.Id, new TeamPlanQuery())).Should().NotContain(t => t.TicketId == w.OpenTicket.Id);

        // Jason commits his own work over it: a warning, not a refusal, since pencilled work holds no capacity.
        await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:30", "10:30"));
        (await w.Db.AuditLog.Where(a => a.Action == "workforce.allocation.created").OrderBy(a => a.CreatedAt).LastAsync()).DetailJson.Should().Contain("tentative work");

        // Now the pencilled work no longer fits as committed work: confirming re-checks everything.
        var refused = () => lead.Plans.ConfirmAsync(w.Lead.Id, pencilled.Id, new WorkAllocationStateInput(pencilled.Version));
        (await refused.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("This time is no longer available");
        (await w.Db.WorkAllocations.AsNoTracking().SingleAsync(a => a.Id == pencilled.Id)).Status.Should().Be(WorkAllocationStatus.Tentative, "nothing was confirmed blindly");
        var confirmed = await w.As(w.Admin).Plans.ConfirmAsync(w.Admin.Id, pencilled.Id, new WorkAllocationStateInput(pencilled.Version, "Both on the same server"));
        (confirmed.Status, confirmed.OverrideReason, confirmed.Version).Should().Be((WorkAllocationStatus.Planned, "Both on the same server", pencilled.Version + 1));
        (await DayAsync(w, w.Lead, w.Jason, Monday)).ConfirmedMinutes.Should().Be(120, "the two overlap for an hour, and a double-booked hour is taken once");
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.confirmed")).Should().Be(1);
        var again = () => lead.Plans.ConfirmAsync(w.Lead.Id, pencilled.Id, new WorkAllocationStateInput(confirmed.Version));
        (await again.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("This work is not pencilled in.");
    }

    [Fact]
    public async Task Committed_work_is_pencilled_back_in_only_by_a_scheduler_and_finished_tentative_work_is_released_too()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var planned = await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Jason, Monday, "09:00", "10:00"));
        var own = () => w.As(w.Jason).Plans.MakeTentativeAsync(w.Jason.Id, planned.Id, new WorkAllocationStateInput(planned.Version));
        (await own.Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Be("Only someone who schedules others can pencil committed work back in.");
        var back = await lead.Plans.MakeTentativeAsync(w.Lead.Id, planned.Id, new WorkAllocationStateInput(planned.Version));
        back.Status.Should().Be(WorkAllocationStatus.Tentative);
        (await DayAsync(w, w.Lead, w.Jason, Monday)).Should().Match<DayCapacityDto>(d => d.ConfirmedMinutes == 0 && d.TentativeMinutes == 60);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.made_tentative")).Should().Be(1);
        // A finished ticket releases its future pencilled time as it releases committed time.
        w.Db.Tickets.Single(t => t.Id == w.OpenTicket.Id).PortalStatus = "CLOSED";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        (await lead.Releaser.ReleaseFinishedAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_requirement_says_effort_window_splittable_and_skill_and_what_is_allocated_is_derived()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var skill = new Skill { MspOrganizationId = OrgA, Name = "SonicWall", NormalizedName = "SONICWALL", IsActive = true };
        w.Db.Skills.Add(skill);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var set = await lead.Plans.SetRequirementAsync(w.Lead.Id, w.JasonsTicket.Id, new PlanningRequirementInput(120, At(Monday, "08:30"), At(Tuesday, "17:30"), false, skill.Id, "Change window first"));
        (set.RequiredMinutes, set.Splittable, set.RequiredSkillName, set.RemainingMinutes, set.UpdatedByName).Should().Be((120, false, "SonicWall", 120, "Lena Lead"));
        // Allocated effort is derived from the plans: confirmed counts against the requirement, pencilled-in does not.
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Abbie, Monday, "14:00", "14:30") with { Tentative = true });
        var got = await lead.Plans.RequirementAsync(w.Lead.Id, w.JasonsTicket.Id);
        (got.ConfirmedMinutes, got.TentativeMinutes, got.RemainingMinutes).Should().Be((60, 30, 60));
        // The skill the work asks for is a warning on a person without it, kept with the placement.
        (await w.Db.AuditLog.Where(a => a.Action == "workforce.allocation.created").OrderBy(a => a.CreatedAt).FirstAsync()).DetailJson.Should().Contain("SonicWall");
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.planning.requirement_set")).Should().Be(1);

        foreach (var (input, message) in new (PlanningRequirementInput, string)[]
        {
            (new PlanningRequirementInput(3, null, null), "Effort is between 5 minutes and 100 hours, in whole five-minute steps."),
            (new PlanningRequirementInput(60, At(Tuesday, "17:30"), At(Monday, "08:30")), "The window must end after it starts."),
            (new PlanningRequirementInput(60, At(Monday, "08:30"), At(Monday.AddDays(40), "08:30")), "The planning window is at most 31 days."),
            (new PlanningRequirementInput(60, null, null, RequiredSkillId: Guid.NewGuid()), "That skill is not in the skill catalogue."),
        })
        {
            var bad = () => lead.Plans.SetRequirementAsync(w.Lead.Id, w.JasonsTicket.Id, input);
            (await bad.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be(message);
        }
        // A technician cannot see Sam's ticket, so there is no requirement to read or write.
        var hidden = () => w.As(w.Jason).Plans.RequirementAsync(w.Jason.Id, w.SamsTicket.Id);
        (await hidden.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        // A technician says what their own work needs; not what they neither hold nor share a team with, even when they can see it.
        (await w.As(w.Jason).Plans.SetRequirementAsync(w.Jason.Id, w.JasonsTicket.Id, new PlanningRequirementInput(60, null, null))).RequiredMinutes.Should().Be(60);
        var notTheirs = () => w.As(w.Jason).Plans.SetRequirementAsync(w.Jason.Id, w.OpenTicket.Id, new PlanningRequirementInput(60, null, null));
        (await notTheirs.Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Be("Only whoever holds this work, their team, or someone who schedules others can say what it needs.");
    }

    [Fact]
    public async Task The_planning_queue_says_why_work_waits_how_urgent_it_is_and_what_the_group_is_short()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var nas = await w.Db.Tickets.SingleAsync(t => t.Id == w.OpenTicket.Id);
        nas.AssignedTeamId = w.Noc.Id;
        var firewall = await w.Db.Tickets.SingleAsync(t => t.Id == w.JasonsTicket.Id);
        firewall.SlaDueAt = At(Today.AddDays(1), "17:00");
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        // Jason's ticket needs twenty hours by tomorrow evening; he has eight hours a day, so sixteen before it is due.
        await lead.Plans.SetRequirementAsync(w.Lead.Id, w.JasonsTicket.Id, new PlanningRequirementInput(1200, null, null, true));

        var queue = await lead.Plans.QueueAsync(w.Lead.Id, new TeamPlanQuery(), 14);
        queue.From.Should().Be(Today);
        var waiting = queue.Items.Single(i => i.Work.TicketId == w.JasonsTicket.Id);
        (waiting.Reason, waiting.Due, waiting.RequiredMinutes, waiting.RemainingMinutes, waiting.Splittable).Should().Be((WaitingReason.InsufficientCapacityBeforeDue, DueRisk.DueTomorrow, 1200, 1200, true));
        waiting.FreeBeforeDueMinutes.Should().Be(2 * 480, "today and tomorrow, nothing planned yet");
        queue.Items.Single(i => i.Work.TicketId == w.OpenTicket.Id).Reason.Should().Be(WaitingReason.NoTechnicianAssigned);
        queue.Items.Single(i => i.Work.TicketId == w.Autotask.Id).Should().Match<PlanningQueueItemDto>(i => i.Reason == WaitingReason.AwaitingPlanning && i.Due == DueRisk.None && i.RequiredMinutes == null);
        queue.Items.First().Work.TicketId.Should().Be(w.JasonsTicket.Id, "the most urgent comes first");
        // Demand is the remaining effort of what has an estimate; the shortage is what the group lacks over the horizon.
        (queue.DemandMinutes, queue.ItemsWithoutEstimate, queue.PeopleCounted).Should().Be((1200, 2, 3));
        queue.AvailableMinutes.Should().Be(2 * 10 * 480, "Jason and Abbie have schedules and ten working days each in the fortnight with nothing planned; the lead is offered for work but has no schedule, so counts for nothing");
        queue.ShortageMinutes.Should().Be(0);
        // Past its due date (yesterday evening), it is overdue; a technician's queue is their own work only.
        firewall = await w.Db.Tickets.SingleAsync(t => t.Id == w.JasonsTicket.Id);
        firewall.SlaDueAt = At(Today.AddDays(-1), "17:00");
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        (await lead.Plans.QueueAsync(w.Lead.Id, new TeamPlanQuery(), 14)).Items.Single(i => i.Work.TicketId == w.JasonsTicket.Id)
            .Should().Match<PlanningQueueItemDto>(i => i.Due == DueRisk.Overdue && i.Reason == WaitingReason.AwaitingPlanning && i.FreeBeforeDueMinutes == null, "there is no time before a date that has gone");
        (await w.As(w.Jason).Plans.QueueAsync(w.Jason.Id, new TeamPlanQuery(), 14)).PeopleCounted.Should().Be(1);
    }

    [Fact]
    public async Task A_preview_places_continuous_work_in_one_sitting_and_splittable_work_across_free_time_and_says_what_does_not_fit()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        var window = (From: At(Monday, "08:30"), To: At(Monday, "17:30"));

        // Four hours in one sitting: the morning has only two and a half left, so the afternoon.
        var sitting = await lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 240));
        sitting.Pieces.Should().ContainSingle().Which.Should().Be(new PlanPieceDto(At(Monday, "13:30"), At(Monday, "17:30"), 240));
        (sitting.AllocatedMinutes, sitting.UnallocatedMinutes, sitting.FreeMinutesInWindow, sitting.LongestFreeMinutes).Should().Be((240, 0, 420, 240));
        sitting.Warnings.Should().BeEmpty();
        sitting.PlanToken.Should().HaveLength(32);

        // Five hours in one sitting: nothing that long is free; the longest free period is named.
        var tooLong = await lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 300));
        tooLong.Pieces.Should().BeEmpty();
        tooLong.UnallocatedMinutes.Should().Be(300);
        tooLong.Warnings.Should().ContainSingle().Which.Should().Be("No single free period of 5h in the window; the longest is 4h.");

        // Five hours that may be split: the free time in order, nothing shorter than half an hour unless it finishes the work.
        var split = await lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 300, Splittable: true));
        split.Pieces.Should().Equal(
            new PlanPieceDto(At(Monday, "08:30"), At(Monday, "09:00"), 30),
            new PlanPieceDto(At(Monday, "10:00"), At(Monday, "12:30"), 150),
            new PlanPieceDto(At(Monday, "13:30"), At(Monday, "15:30"), 120));
        (split.AllocatedMinutes, split.UnallocatedMinutes).Should().Be((300, 0));

        // Ten hours in one day: seven fit, three are said to remain. Nothing is overbooked.
        var partial = await lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 600, Splittable: true));
        (partial.AllocatedMinutes, partial.UnallocatedMinutes).Should().Be((420, 180));
        partial.Warnings.Should().Contain("Only 7h of 10h fits in the window; 3h remains unallocated.");

        // A preview writes nothing.
        (await w.Db.WorkAllocations.CountAsync()).Should().Be(1);
        // Limits: a window of more than two weeks, and effort beyond a hundred hours, are refused before anything is read.
        var wide = () => lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, At(Monday.AddDays(20), "17:30"), 60));
        (await wide.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("A preview covers at most 14 days.");
        var huge = () => lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 6005));
        await huge.Should().ThrowAsync<ValidationFailedException>();
        // Scope: a technician previews only for themselves (a colleague is outside what they see), and only work they can see.
        var others = () => w.As(w.Jason).Plans.PreviewAsync(w.Jason.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Abbie.Id, window.From, window.To, 60));
        (await others.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        var hidden = () => w.As(w.Jason).Plans.PreviewAsync(w.Jason.Id, new PlanPreviewInput(w.SamsTicket.Id, w.Jason.Id, window.From, window.To, 60));
        (await hidden.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        // Time that has passed is never proposed: at 10:00 on Monday (in the organization's zone, which
        // on Linux is not UTC) a window from 08:30 proposes from 10:00, and says so.
        w.Clock.Advance(At(Monday, "10:00") - w.Clock.GetUtcNow());
        var late = await lead.Plans.PreviewAsync(w.Lead.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, window.From, window.To, 240));
        late.Pieces.Should().ContainSingle().Which.Start.Should().Be(At(Monday, "13:30"), "what is left of the morning after 10:00 is too short for four hours");
        late.Warnings.Should().Contain("The window started before now; proposing from 5 Jan 10:00.");
        late.FreeMinutesInWindow.Should().Be(150 + 240, "10:00-12:30 and the afternoon");
    }

    [Fact]
    public async Task A_preview_is_written_only_while_the_plan_is_what_it_saw_and_what_is_written_follows_every_rule()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        await lead.Plans.SetRequirementAsync(w.Lead.Id, w.OpenTicket.Id, new PlanningRequirementInput(300, null, null, true));
        var request = new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, At(Monday, "08:30"), At(Monday, "17:30"), 300, Splittable: true);
        var preview = await lead.Plans.PreviewAsync(w.Lead.Id, request);
        preview.Pieces.Should().HaveCount(2);

        // Someone else takes part of the morning before the confirmation: the plan the preview saw is gone.
        await w.As(w.Jason).Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "09:30"));
        var stale = () => lead.Plans.ConfirmPreviewAsync(w.Lead.Id, new PlanConfirmInput(request, preview.Pieces, preview.PlanToken));
        var refused = (await stale.Should().ThrowAsync<ConflictException>()).Which;
        refused.Message.Should().Be("The plan changed since the preview. Review the new proposal.");
        var changed = refused.Payload.Should().BeOfType<PlanChangedDto>().Subject;
        changed.Stale.Should().BeTrue();
        changed.Preview.PlanToken.Should().NotBe(preview.PlanToken);
        changed.Preview.Pieces.Should().HaveCount(3, "the morning is now in two pieces");
        (await w.Db.WorkAllocations.CountAsync(a => a.TicketId == w.OpenTicket.Id)).Should().Be(0, "nothing stale was written");

        // The fresh preview, confirmed: every piece checked and written in one transaction; the holder bridge once.
        var done = await lead.Plans.ConfirmPreviewAsync(w.Lead.Id, new PlanConfirmInput(request, changed.Preview.Pieces, changed.Preview.PlanToken, Note: "Spread over the day"));
        (done.Allocations.Count, done.AllocatedMinutes, done.RemainingMinutes).Should().Be((3, 300, 0));
        done.Allocations.Should().OnlyContain(a => a.Status == WorkAllocationStatus.Planned && a.Note == "Spread over the day" && a.Method == SchedulingMethod.AuthorizedUser);
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.OpenTicket.Id)).AssignedAppUserId.Should().Be(w.Jason.Id);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "ticket.assigned.portal")).Should().Be(1);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.allocation.plan_confirmed")).Should().Be(1);
        (await DayAsync(w, w.Lead, w.Jason, Monday)).RemainingConfirmedMinutes.Should().Be(480 - 30 - 300);
        // The same token again: the plan is no longer what it saw.
        var twice = () => lead.Plans.ConfirmPreviewAsync(w.Lead.Id, new PlanConfirmInput(request, changed.Preview.Pieces, changed.Preview.PlanToken));
        await twice.Should().ThrowAsync<ConflictException>();
        // Pieces outside the window, overlapping each other, or adding up to more than the effort are refused before the gate.
        var outside = () => lead.Plans.ConfirmPreviewAsync(w.Lead.Id, new PlanConfirmInput(request, [new PlanPieceDto(At(Tuesday, "09:00"), At(Tuesday, "10:00"), 60)], "x"));
        (await outside.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Every piece must lie inside the planning window.");
    }

    [Fact]
    public async Task Two_managers_confirming_previews_for_the_same_time_end_with_one_plan()
    {
        var w = await WorldAsync();
        var leadDb = AdminHarness.Create(OrgA, w.DbName).Db;
        var adminDb = AdminHarness.Create(OrgA, w.DbName).Db;
        var lead = World.For(leadDb, OrgA, w.Lead, w.Clock).Plans;
        var admin = World.For(adminDb, OrgA, w.Admin, w.Clock).Plans;
        var request = new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, At(Monday, "08:30"), At(Monday, "12:30"), 240);
        var a = await lead.PreviewAsync(w.Lead.Id, request);
        var b = await admin.PreviewAsync(w.Admin.Id, request with { TicketId = w.ConnectWise.Id });
        a.PlanToken.Should().NotBe(b.PlanToken, "a different ticket is a different plan");

        var results = await Task.WhenAll(
            Attempt(() => lead.ConfirmPreviewAsync(w.Lead.Id, new PlanConfirmInput(request, a.Pieces, a.PlanToken))),
            Attempt(() => admin.ConfirmPreviewAsync(w.Admin.Id, new PlanConfirmInput(request with { TicketId = w.ConnectWise.Id }, b.Pieces, b.PlanToken))));
        results.Count(r => r is null).Should().Be(1, "one wins");
        results.Single(r => r is not null).Should().BeOfType<ConflictException>();
        (await w.Db.WorkAllocations.CountAsync(a => a.AppUserId == w.Jason.Id && a.StartsAt == At(Monday, "08:30"))).Should().Be(1);

        static async Task<Exception?> Attempt(Func<Task<PlanConfirmedDto>> call)
        {
            try { await call(); return null; }
            catch (Exception ex) { return ex; }
        }
    }

    [Fact]
    public async Task Work_that_would_end_after_its_due_date_is_placed_with_a_warning_never_a_moved_due_date()
    {
        var w = await WorldAsync();
        var firewall = await w.Db.Tickets.SingleAsync(t => t.Id == w.JasonsTicket.Id);
        firewall.SlaDueAt = At(Monday, "10:00");
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var jason = w.As(w.Jason);
        await jason.Plans.CreateAsync(w.Jason.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:30", "10:30"));
        (await w.Db.AuditLog.Where(a => a.Action == "workforce.allocation.created").SingleAsync()).DetailJson.Should().Contain("Ends after the due date");
        (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id)).SlaDueAt.Should().Be(At(Monday, "10:00"));
        // The preview says it too.
        var preview = await jason.Plans.PreviewAsync(w.Jason.Id, new PlanPreviewInput(w.JasonsTicket.Id, w.Jason.Id, At(Monday, "10:30"), At(Monday, "17:30"), 60));
        preview.Warnings.Should().ContainSingle().Which.Should().StartWith("Ends after the due date");
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
            () => notStaff.Team(new Desk.Api.Controllers.WorkforcePlanController.TeamPlanRequest(), default), () => notStaff.UnscheduledTeam(new Desk.Api.Controllers.WorkforcePlanController.TeamPlanRequest(), default),
            () => notStaff.Confirm(id, new WorkAllocationStateInput(0), default), () => notStaff.MakeTentative(id, new WorkAllocationStateInput(0), default),
            () => notStaff.Requirement(id, default), () => notStaff.SetRequirement(id, new PlanningRequirementInput(60, null, null), default),
            () => notStaff.Queue(new Desk.Api.Controllers.WorkforcePlanController.TeamPlanRequest(), null, default),
            () => notStaff.Preview(new Desk.Api.Controllers.WorkforcePlanController.PreviewRequest(id, id, At(Monday, "08:00"), At(Monday, "18:00"), 60), default),
            () => notStaff.ConfirmPreview(new PlanConfirmInput(new PlanPreviewInput(id, id, At(Monday, "08:00"), At(Monday, "18:00"), 60), [new PlanPieceDto(At(Monday, "09:00"), At(Monday, "10:00"), 60)], "x"), default),
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
        // The team scheduler of B shows B's own people only, and A's team id narrows it to nobody.
        var theirs = (await b.TeamAsync(adminB.Id, new TeamPlanQuery(Monday, Monday))).People.Select(p => p.AppUserId).ToList();
        theirs.Should().Contain(adminB.Id).And.NotContain(new[] { w.Jason.Id, w.Abbie.Id, w.Lead.Id, w.Sam.Id, w.Admin.Id });
        (await b.TeamAsync(adminB.Id, new TeamPlanQuery(Monday, Monday, TeamId: w.Noc.Id))).People.Should().BeEmpty();
        (await b.UnscheduledTeamAsync(adminB.Id, new TeamPlanQuery())).Should().BeEmpty();
        (await b.UnscheduledTeamAsync(adminB.Id, new TeamPlanQuery(TeamId: w.Noc.Id))).Should().BeEmpty();
        (await b.QueueAsync(adminB.Id, new TeamPlanQuery(), 14)).Items.Should().BeEmpty();
        var requirement = () => b.RequirementAsync(adminB.Id, w.OpenTicket.Id);
        (await requirement.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        var preview = () => b.PreviewAsync(adminB.Id, new PlanPreviewInput(w.OpenTicket.Id, w.Jason.Id, At(Monday, "08:00"), At(Monday, "18:00"), 60));
        (await preview.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        var confirm = () => b.ConfirmAsync(adminB.Id, planned.Id, new WorkAllocationStateInput(0));
        (await confirm.Should().ThrowAsync<NotFoundException>()).WithMessage("Planned work was not found.");
        (await w.Db.WorkAllocations.AsNoTracking().SingleAsync()).Should().Match<WorkAllocation>(a => a.Id == planned.Id && a.Status == WorkAllocationStatus.Planned && a.Version == 0);
    }
}
