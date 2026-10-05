using System.Reflection;
using Desk.Api.Auth;
using Desk.Api.Controllers;
using Desk.Domain.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Who may call what, checked across every controller rather than one endpoint at a time. The feature
/// audit found no test that a refusal stays a refusal: an action added without an attribute would be
/// open to anyone, and nothing would notice until someone called it.
/// </summary>
public class EndpointAuthorizationTests
{
    private static readonly Type[] Controllers = typeof(AdminEmailController).Assembly.GetTypes()
        .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
        .ToArray();

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        Controllers.SelectMany(c => c.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(m => (c, m)));

    private static string Name((Type Controller, MethodInfo Action) a) => $"{a.Controller.Name}.{a.Action.Name}";

    private static bool IsAnonymous((Type Controller, MethodInfo Action) a)
        => a.Action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
           || (a.Controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null
               && a.Action.GetCustomAttributes<AuthorizeAttribute>().FirstOrDefault() is null);

    private static string Route((Type Controller, MethodInfo Action) a)
        => $"{a.Controller.GetCustomAttribute<RouteAttribute>()?.Template}/{a.Action.GetCustomAttributes<HttpMethodAttribute>().First().Template}".TrimEnd('/');

    /// <summary>
    /// Permission keys that let a caller through: any-of the action's attribute, else the controller's.
    /// Where both exist ASP.NET requires both, so the action's is the stricter, narrower answer.
    /// </summary>
    private static IReadOnlySet<string>? Required((Type Controller, MethodInfo Action) a)
    {
        var attr = a.Action.GetCustomAttribute<RequirePermissionAttribute>() ?? a.Controller.GetCustomAttribute<RequirePermissionAttribute>();
        return attr?.Policy is { } policy
            ? policy[PermissionPolicyProvider.Prefix.Length..].Split(PermissionPolicyProvider.Any).ToHashSet()
            : null;
    }

    // The deliberate exceptions: a public enquiry form, PSA webhooks (verified by signature), signed
    // attachment links, and monitoring-tool alerts (verified by the source's own key, which arrives in
    // a header because the request comes from a vendor's cloud with no session behind it). Anything else anonymous is a mistake. Listed by controller for wholly public ones and by action otherwise, so a new anonymous action on
    // a mostly-private controller still fails this test.
    private static readonly HashSet<string> KnownAnonymous = new(StringComparer.Ordinal)
    {
        "PublicEnquiriesController", "WebhooksController", "AttachmentsController.Blob",
        "AlertIntakeController",
    };

    [Fact]
    public void Every_action_requires_a_signed_in_caller_unless_it_is_a_known_public_endpoint()
    {
        var open = Actions()
            .Where(a => IsAnonymous(a) ? !KnownAnonymous.Contains(a.Controller.Name) && !KnownAnonymous.Contains(Name(a))
                : a.Action.GetCustomAttributes<AuthorizeAttribute>(true).FirstOrDefault() is null
                  && a.Controller.GetCustomAttributes<AuthorizeAttribute>(true).FirstOrDefault() is null)
            .Select(Name).ToList();

        open.Should().BeEmpty("these actions would answer anyone, signed in or not");
    }

    [Fact]
    public void Every_admin_report_and_dashboard_action_names_the_permission_it_needs()
    {
        // Signed in is not enough here: these carry other people's figures, credentials and configuration.
        var bare = Actions()
            .Where(a => Route(a) is var r && (r.StartsWith("api/admin") || r.StartsWith("api/reports") || r.StartsWith("api/dashboard")))
            .Where(a => Required(a) is null)
            .Select(a => $"{Name(a)} ({Route(a)})").ToList();

        bare.Should().BeEmpty();
    }

    public static TheoryData<string, string, string[]> Golden => new()
    {
        // Mail account: reading status is for health viewers; the account and the test send are admin-only.
        { nameof(AdminEmailController), nameof(AdminEmailController.Status), [Permissions.IntegrationHealthView, Permissions.OrgManage] },
        { nameof(AdminEmailController), nameof(AdminEmailController.Settings), [Permissions.OrgManage] },
        { nameof(AdminEmailController), nameof(AdminEmailController.SaveSettings), [Permissions.OrgManage] },
        { nameof(AdminEmailController), nameof(AdminEmailController.RemoveSettings), [Permissions.OrgManage] },
        { nameof(AdminEmailController), nameof(AdminEmailController.Test), [Permissions.OrgManage] },
        // Staff reports name people: team productivity throughout, organization admin for the time zone.
        { nameof(StaffReportsController), nameof(StaffReportsController.Schedules), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.Save), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.RunNow), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.Download), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.ClientQbrPdf), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.TechnicianPdf), [Permissions.ProductivityViewTeam] },
        { nameof(StaffReportsController), nameof(StaffReportsController.SaveSettings), [Permissions.OrgManage] },
        // Mapping snapshots: view to read drift, manage to write one.
        { nameof(AdminMappingsController), nameof(AdminMappingsController.SnapshotStatus), [Permissions.MappingsView] },
        { nameof(AdminMappingsController), nameof(AdminMappingsController.SaveSnapshot), [Permissions.MappingsManage] },
        { nameof(AdminMappingsController), nameof(AdminMappingsController.Rollback), [Permissions.MappingsManage] },
        // Dashboard: the organization-wide views are team-only.
        { nameof(DashboardController), nameof(DashboardController.Team), [Permissions.ProductivityViewTeam] },
        { nameof(DashboardController), nameof(DashboardController.Clients), [Permissions.ProductivityViewTeam] },
        { nameof(DashboardController), nameof(DashboardController.ExportTeam), [Permissions.ProductivityViewTeam] },
        // Workforce: reading needs schedule.view (scoped per person in the services); every change
        // also needs workforce.manage. Action-level attributes add to the controller's, never replace it.
        { nameof(WorkforceController), nameof(WorkforceController.People), [Permissions.ScheduleView] },
        { nameof(WorkforceController), nameof(WorkforceController.Schedule), [Permissions.ScheduleView] },
        { nameof(WorkforceController), nameof(WorkforceController.Skills), [Permissions.ScheduleView] },
        { nameof(WorkforceController), nameof(WorkforceController.PersonSkills), [Permissions.ScheduleView] },
        { nameof(WorkforceController), nameof(WorkforceController.SaveSchedule), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.RemoveUpcoming), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.CopySchedule), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.SetSchedulable), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.CreateSkill), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.UpdateSkill), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.AssignSkill), [Permissions.WorkforceManage] },
        { nameof(WorkforceController), nameof(WorkforceController.RemoveSkill), [Permissions.WorkforceManage] },
        // Capacity and availability: internal only. Reading needs schedule.view (scoped per person in
        // the services); recording an exception also needs availability.manage.
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.PersonCapacity), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.TeamCapacity), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.Groups), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.FindAvailable), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.EvaluateConflicts), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.Exceptions), [Permissions.ScheduleView] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.AddException), [Permissions.AvailabilityManage] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.UpdateException), [Permissions.AvailabilityManage] },
        { nameof(WorkforceCapacityController), nameof(WorkforceCapacityController.RemoveException), [Permissions.AvailabilityManage] },
        // Planned work: reading needs schedule.view; every change also needs schedule.manage, whose
        // scope decides per person in the service (Own = your own plan only).
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Plan), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Unscheduled), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.PlannablePeople), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.ForTicket), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Team), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.UnscheduledTeam), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Create), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.CreateInternalWork), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Update), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Reassign), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Cancel), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Confirm), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.MakeTentative), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Requirement), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.SetRequirement), [Permissions.ScheduleManage] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Queue), [Permissions.ScheduleView] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.Preview), [Permissions.ScheduleView] },
        // Phase 6: the clock on a piece of work is logging time; a day and the team are schedule reads.
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.Active), [Permissions.ScheduleView] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.Start), [Permissions.TicketsLogTime] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.Pause), [Permissions.TicketsLogTime] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.Resume), [Permissions.TicketsLogTime] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.Stop), [Permissions.TicketsLogTime] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.MyDay), [Permissions.ScheduleView] },
        { nameof(WorkforceTimeController), nameof(WorkforceTimeController.TeamToday), [Permissions.ScheduleView] },
        // Phase 7: the analytics are schedule reads (the scope decides whose figures); the export, which
        // carries named people's figures out of the system, needs its own key.
        { nameof(WorkforceAnalyticsController), nameof(WorkforceAnalyticsController.Filters), [Permissions.ScheduleView] },
        { nameof(WorkforceAnalyticsController), nameof(WorkforceAnalyticsController.Overview), [Permissions.ScheduleView] },
        { nameof(WorkforceAnalyticsController), nameof(WorkforceAnalyticsController.Technician), [Permissions.ScheduleView] },
        { nameof(WorkforceAnalyticsController), nameof(WorkforceAnalyticsController.Work), [Permissions.ScheduleView] },
        { nameof(WorkforceAnalyticsController), nameof(WorkforceAnalyticsController.Export), [Permissions.WorkforceAnalyticsExport] },
        // The ticket time panel, which a stopped clock writes through: every action needs tickets.time.log.
        { nameof(TicketTimeController), nameof(TicketTimeController.List), [Permissions.TicketsLogTime] },
        { nameof(TicketTimeController), nameof(TicketTimeController.LogTime), [Permissions.TicketsLogTime] },
        { nameof(TicketTimeController), nameof(TicketTimeController.Update), [Permissions.TicketsLogTime] },
        { nameof(TicketTimeController), nameof(TicketTimeController.Delete), [Permissions.TicketsLogTime] },
        { nameof(TicketTimeController), nameof(TicketTimeController.Retry), [Permissions.TicketsLogTime] },
        { nameof(WorkforcePlanController), nameof(WorkforcePlanController.ConfirmPreview), [Permissions.ScheduleManage] },
    };

    [Fact]
    public void No_client_account_can_reach_any_workforce_endpoint()
    {
        // What a client login can ever hold: its role's grants, or the fixed claims a pure client
        // portal login receives. None of it may open a single workforce action.
        var clientHeld = Permissions.ForRole(Desk.Domain.Enums.RoleType.ClientAdministrator)
            .Concat(Permissions.ForRole(Desk.Domain.Enums.RoleType.ClientUser))
            .Select(p => p.Key).Concat([Permissions.TicketsCreate, Permissions.TicketsAddPublicNote]).ToHashSet();
        // Every controller under api/workforce - found by route, so one added later is covered too.
        var workforce = Controllers.Where(c => (c.GetCustomAttribute<RouteAttribute>()?.Template ?? "").StartsWith("api/workforce", StringComparison.Ordinal)).ToList();
        workforce.Should().Contain([typeof(WorkforceController), typeof(WorkforceCapacityController), typeof(WorkforcePlanController), typeof(WorkforceTimeController), typeof(WorkforceAnalyticsController)]);
        foreach (var controller in workforce)
        {
            controller.GetCustomAttributes<AuthorizeAttribute>().Should().NotBeEmpty($"{controller.Name} is never anonymous");
            var classPolicy = controller.GetCustomAttribute<RequirePermissionAttribute>()?.Policy;
            classPolicy.Should().NotBeNull($"every action of {controller.Name} sits behind the controller's own requirement");
            classPolicy![PermissionPolicyProvider.Prefix.Length..].Split(PermissionPolicyProvider.Any)
                .Should().NotIntersectWith(clientHeld, controller.Name);
            foreach (var action in Actions().Where(a => a.Controller == controller))
            {
                IsAnonymous(action).Should().BeFalse(Name(action));
                (Required(action) ?? new HashSet<string>()).Should().NotIntersectWith(clientHeld, Name(action));
            }
        }
    }

    [Fact]
    public void No_client_role_or_client_login_holds_a_workforce_permission()
    {
        string[] workforce = [Permissions.ScheduleView, Permissions.WorkforceManage, Permissions.AvailabilityManage, Permissions.ScheduleManage, Permissions.ScheduleOverride];
        foreach (var role in new[] { Desk.Domain.Enums.RoleType.ClientAdministrator, Desk.Domain.Enums.RoleType.ClientUser })
            Permissions.ForRole(role).Select(p => p.Key).Should().NotIntersectWith(workforce, role.ToString());
    }

    [Fact]
    public void Reading_capacity_never_needs_a_request_that_changes_anything()
    {
        // Searching and conflict checks are reads, so they are GETs: they stay usable while an
        // administrator views the portal as someone (read-only), and nothing caches or replays a write.
        foreach (var name in new[] { nameof(WorkforceCapacityController.PersonCapacity), nameof(WorkforceCapacityController.TeamCapacity), nameof(WorkforceCapacityController.Groups),
                     nameof(WorkforceCapacityController.FindAvailable), nameof(WorkforceCapacityController.EvaluateConflicts), nameof(WorkforceCapacityController.Exceptions) })
            typeof(WorkforceCapacityController).GetMethod(name)!.GetCustomAttributes<HttpMethodAttribute>().Single().HttpMethods.Should().Equal("GET");
        // Phase 5's reads too: the requirement, the queue and a preview (which writes nothing) are GETs.
        foreach (var name in new[] { nameof(WorkforcePlanController.Requirement), nameof(WorkforcePlanController.Queue), nameof(WorkforcePlanController.Preview) })
            typeof(WorkforcePlanController).GetMethod(name)!.GetCustomAttributes<HttpMethodAttribute>().Single().HttpMethods.Should().Equal("GET");
        // Phase 6: reading the running clock, a day and the team changes nothing; every clock change is a POST.
        foreach (var name in new[] { nameof(WorkforceTimeController.Active), nameof(WorkforceTimeController.MyDay), nameof(WorkforceTimeController.TeamToday) })
            typeof(WorkforceTimeController).GetMethod(name)!.GetCustomAttributes<HttpMethodAttribute>().Single().HttpMethods.Should().Equal("GET");
        foreach (var name in new[] { nameof(WorkforceTimeController.Start), nameof(WorkforceTimeController.Pause), nameof(WorkforceTimeController.Resume), nameof(WorkforceTimeController.Stop) })
            typeof(WorkforceTimeController).GetMethod(name)!.GetCustomAttributes<HttpMethodAttribute>().Single().HttpMethods.Should().Equal("POST");
        // Phase 7: analytics are reads only, the export included; nothing on the dashboard changes anything.
        foreach (var name in new[] { nameof(WorkforceAnalyticsController.Filters), nameof(WorkforceAnalyticsController.Overview), nameof(WorkforceAnalyticsController.Technician), nameof(WorkforceAnalyticsController.Work), nameof(WorkforceAnalyticsController.Export) })
            typeof(WorkforceAnalyticsController).GetMethod(name)!.GetCustomAttributes<HttpMethodAttribute>().Single().HttpMethods.Should().Equal("GET");
    }

    [Fact]
    public void Nothing_a_client_can_receive_carries_workforce_planning()
    {
        // A ticket a client can see does not make its planning visible. The shapes the ticket API and
        // the client portal return must not grow a field about schedules, capacity or who is planned
        // when - whatever a later phase adds to the internal side.
        string[] forbidden = ["Capacity", "Schedul", "Allocat", "Availability", "FreeSlot", "WorkingWindow", "Utilization", "Skill", "Planned", "Tentative", "MyPlan", "Override", "RequiredMinutes", "WaitingReason", "PlanToken", "Shortage", "Session", "Segment", "ActualSeconds", "MyDay", "TeamToday", "Variance", "Heatmap", "Reactive", "Analytics"];
        var clientFacing = typeof(Desk.Application.Tickets.TicketDetailDto).Assembly.GetTypes().Where(t =>
            t.Namespace is "Desk.Application.Tickets" or "Desk.Application.ControlPanel" or "Desk.Application.Knowledge" or "Desk.Application.Attachments"
            && !t.IsInterface && !t.IsEnum && !t.Name.StartsWith('<')).ToList();
        clientFacing.Should().Contain(typeof(Desk.Application.Tickets.TicketDetailDto));

        var leaks = clientFacing.SelectMany(t => t.GetProperties().Select(p => (Type: t.Name, Property: p.Name)))
            .Where(x => forbidden.Any(f => x.Property.Contains(f, StringComparison.OrdinalIgnoreCase)))
            // "Schedule" of a different kind: when a recurring ticket or a report is raised, and a
            // client company's own opening hours - none of them when a member of staff works.
            .Where(x => !x.Type.Contains("Recurring", StringComparison.Ordinal) && !x.Type.Contains("Report", StringComparison.Ordinal)
                        && !x.Type.StartsWith("BusinessHours", StringComparison.Ordinal))
            .Select(x => $"{x.Type}.{x.Property}").ToList();
        leaks.Should().BeEmpty("ticket and client-portal shapes must stay free of internal workforce planning");
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void Sensitive_endpoints_require_exactly_these_permissions(string controller, string action, string[] expected)
    {
        var match = Actions().Single(a => a.Controller.Name == controller && a.Action.Name == action);

        Required(match).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task A_caller_without_the_permission_is_refused_and_one_with_it_is_let_through()
    {
        var requirement = new PermissionRequirement(Permissions.OrgManage);
        async Task<bool> Allowed(IReadOnlySet<string> held)
        {
            var handler = new PermissionAuthorizationHandler(new TestCurrentUser(Guid.NewGuid(), permissions: held));
            var context = new AuthorizationHandlerContext([requirement], new System.Security.Claims.ClaimsPrincipal(), null);
            await handler.HandleAsync(context);
            return context.HasSucceeded;
        }

        (await Allowed(new HashSet<string>())).Should().BeFalse();
        (await Allowed(new HashSet<string> { Permissions.IntegrationHealthView })).Should().BeFalse();
        (await Allowed(new HashSet<string> { Permissions.OrgManage })).Should().BeTrue();
    }
}
