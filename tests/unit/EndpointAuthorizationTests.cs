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

    // The deliberate exceptions: a public enquiry form, PSA webhooks (verified by signature) and
    // signed attachment links. Anything else anonymous is a mistake. Listed by controller for wholly public ones and by action otherwise, so a new anonymous action on
    // a mostly-private controller still fails this test.
    private static readonly HashSet<string> KnownAnonymous = new(StringComparer.Ordinal)
    {
        "PublicEnquiriesController", "WebhooksController", "AttachmentsController.Blob",
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
    };

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
