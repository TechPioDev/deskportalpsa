using Desk.Domain.Enums;

namespace Desk.Domain.Authorization;

/// <summary>
/// Central catalogue of permission-claim keys. Authorization is evaluated against these
/// claims — never against role names directly — so access can be recomposed without code
/// changes (spec §10: "Use permission claims rather than hard-coded role checks").
/// </summary>
public static class Permissions
{
    // Platform
    public const string PlatformManageOrganizations = "platform.organizations.manage";
    public const string PlatformViewAllHealth = "platform.health.view";
    public const string PlatformManageSettings = "platform.settings.manage";

    // Organization / connections / mapping
    public const string OrgManage = "org.manage";
    public const string ConnectionsManage = "connections.manage";
    public const string ConnectionsView = "connections.view";
    public const string MappingsManage = "mappings.manage";
    public const string MappingsView = "mappings.view";

    // Users & roles
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string ClientUsersManage = "clientusers.manage";

    // Tickets
    public const string TicketsViewAll = "tickets.view.all";
    public const string TicketsViewAssigned = "tickets.view.assigned";
    public const string TicketsViewOwnCompany = "tickets.view.company";
    public const string TicketsViewOwn = "tickets.view.own";

    /// <summary>
    /// The two keys that make someone STAFF for ticket purposes: all tickets (admins, managers) or
    /// assigned tickets (technicians). "Can this person work tickets as the MSP" is either of them -
    /// asking for <see cref="TicketsViewAll"/> alone locked every Standard Technician out of the ticket
    /// list and treated them as a client. Which tickets they then see is the scope's business
    /// (see TicketScopeQuery), not this check's.
    /// </summary>
    public static readonly string[] StaffTicketViews = [TicketsViewAll, TicketsViewAssigned];
    public const string TicketsCreate = "tickets.create";
    public const string TicketsAddPublicNote = "tickets.note.public.add";
    public const string TicketsLogTime = "tickets.time.log";
    public const string TicketsUpdate = "tickets.update";

    // Dashboards & reports
    public const string ReportsView = "reports.view";
    public const string ProductivityViewTeam = "productivity.team.view";
    public const string ProductivityViewOwn = "productivity.own.view";

    // Ops & audit
    public const string IntegrationHealthView = "integration.health.view";
    public const string JobsManage = "jobs.manage";
    public const string AuditView = "audit.view";
    public const string SecurityConfigView = "security.config.view";

    // Inbound enquiries from the public site
    public const string EnquiriesView = "enquiries.view";

    /// <summary>
    /// Create and configure the team's own boards. Deliberately separate from raising or taking a
    /// ticket on one, which anybody on the team may do: deciding what boards exist is a lead's call,
    /// handing work to a colleague is not.
    /// </summary>
    public const string BoardsManage = "boards.manage";

    /// <summary>
    /// See people's working schedules and skills, as far as the scope reaches: your own (Own), the
    /// people you share a team or department with, or everyone. Schedules are a capacity boundary for
    /// planning work, never attendance. Staff only - no client role ever holds it.
    /// </summary>
    public const string ScheduleView = "schedule.view";

    /// <summary>
    /// Set up the workforce: people's working schedules and breaks, whether they are offered for
    /// planned work, and the skill catalogue and who holds which skill.
    /// </summary>
    public const string WorkforceManage = "workforce.manage";

    /// <summary>Every claim, used to grant the full set to super-administrators.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PlatformManageOrganizations, PlatformViewAllHealth, PlatformManageSettings,
        OrgManage, ConnectionsManage, ConnectionsView, MappingsManage, MappingsView,
        UsersManage, RolesManage, ClientUsersManage,
        TicketsViewAll, TicketsViewAssigned, TicketsViewOwnCompany, TicketsViewOwn,
        TicketsCreate, TicketsAddPublicNote, TicketsLogTime, TicketsUpdate,
        ReportsView, ProductivityViewTeam, ProductivityViewOwn,
        IntegrationHealthView, JobsManage, AuditView, SecurityConfigView, EnquiriesView,
        BoardsManage,
        ScheduleView, WorkforceManage,
    };

    /// <summary>
    /// Default claim set for each built-in role, with the reach of each grant.
    ///
    /// Every scope here reproduces what the role could ALREADY do before scoped enforcement
    /// existed — not what it arguably should do. In particular a Technician's edit / add-note /
    /// log-time grants are <see cref="PermissionScope.All"/>, because until now nothing row-filtered
    /// them: narrowing them here would silently strip access from technicians mid-shift, with no
    /// admin UI yet to grant an exception back. Tightening is a deliberate, visible admin action in
    /// a later phase. <c>PermissionGoldenMatrixTests</c> pins this to the pre-enforcement behavior.
    /// </summary>
    public static IReadOnlyList<(string Key, PermissionScope Scope)> ForRole(RoleType role) => role switch
    {
        // Every permission, each at the reach its own key already implies.
        RoleType.PlatformSuperAdministrator =>
            All.Select(k => (k, ScopeForKey(k))).ToArray(),

        RoleType.MspAdministrator =>
        [
            (OrgManage, PermissionScope.All), (ConnectionsManage, PermissionScope.All),
            (ConnectionsView, PermissionScope.All), (MappingsManage, PermissionScope.All),
            (MappingsView, PermissionScope.All), (UsersManage, PermissionScope.All),
            (RolesManage, PermissionScope.All), (ClientUsersManage, PermissionScope.All),
            (TicketsViewAll, PermissionScope.All), (TicketsCreate, PermissionScope.All),
            (TicketsAddPublicNote, PermissionScope.All), (TicketsLogTime, PermissionScope.All),
            (TicketsUpdate, PermissionScope.All), (ReportsView, PermissionScope.All),
            (ProductivityViewTeam, PermissionScope.All), (IntegrationHealthView, PermissionScope.All),
            (JobsManage, PermissionScope.All), (AuditView, PermissionScope.All),
            (SecurityConfigView, PermissionScope.All), (EnquiriesView, PermissionScope.All),
            (BoardsManage, PermissionScope.All),
            (ScheduleView, PermissionScope.All), (WorkforceManage, PermissionScope.All),
        ],

        RoleType.Manager =>
        [
            (ConnectionsView, PermissionScope.All), (MappingsView, PermissionScope.All),
            (TicketsViewAll, PermissionScope.All), (TicketsLogTime, PermissionScope.All),
            (TicketsUpdate, PermissionScope.All), (ReportsView, PermissionScope.All),
            (ProductivityViewTeam, PermissionScope.All), (IntegrationHealthView, PermissionScope.All),
            (EnquiriesView, PermissionScope.All), (BoardsManage, PermissionScope.All),
            (ScheduleView, PermissionScope.All),
        ],

        RoleType.Technician =>
        [
            (TicketsViewAssigned, PermissionScope.Assigned),
            // Raise tickets on the team's own boards (owner's call, 29 Sep 2026). The client-side
            // endpoints this key also opens still demand a client account, so nothing else widens.
            (TicketsCreate, PermissionScope.All),
            // All, not Assigned — see the summary above. This is today's behavior, preserved.
            (TicketsAddPublicNote, PermissionScope.All),
            (TicketsLogTime, PermissionScope.All),
            (TicketsUpdate, PermissionScope.All),
            (ProductivityViewOwn, PermissionScope.Own),
            // Their own working schedule and skills.
            (ScheduleView, PermissionScope.Own),
        ],

        RoleType.ClientAdministrator =>
        [
            (TicketsViewOwnCompany, PermissionScope.Selected), (TicketsCreate, PermissionScope.All),
            (TicketsAddPublicNote, PermissionScope.All), (ClientUsersManage, PermissionScope.All),
            (ReportsView, PermissionScope.All),
        ],

        RoleType.ClientUser =>
        [
            (TicketsViewOwn, PermissionScope.Own), (TicketsCreate, PermissionScope.All),
            (TicketsAddPublicNote, PermissionScope.All),
        ],

        RoleType.Auditor =>
        [
            (AuditView, PermissionScope.All), (SecurityConfigView, PermissionScope.All),
            (IntegrationHealthView, PermissionScope.All),
        ],

        _ => [],
    };

    /// <summary>The scope a key's own name already asserts — used for the super-admin set, which
    /// holds every key and must carry each one at its declared reach.</summary>
    private static PermissionScope ScopeForKey(string key) => key switch
    {
        TicketsViewAssigned => PermissionScope.Assigned,
        TicketsViewOwn => PermissionScope.Own,
        TicketsViewOwnCompany => PermissionScope.Selected,
        ProductivityViewOwn => PermissionScope.Own,
        _ => PermissionScope.All,
    };
}
