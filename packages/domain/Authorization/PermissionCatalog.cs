namespace Desk.Domain.Authorization;

/// <summary>
/// Declarative metadata for every permission key: which module it belongs to, which scopes are
/// legal for it, and whether board access fences it.
///
/// Deliberately a code catalogue rather than a database table, matching how <see cref="Permissions"/>
/// already works. Adding a module (Assets, Billing, …) is a new constant plus an entry here — no
/// schema change, no migration, which is what keeps the permission system extensible without a
/// rewrite. The tradeoff is that permissions cannot be defined at runtime by an admin; that is the
/// intended boundary, since a permission the code never checks would be a lie in the UI.
/// </summary>
public sealed record PermissionDefinition(
    string Key,
    string Module,
    string DisplayName,
    IReadOnlyList<PermissionScope> SupportedScopes,
    PermissionScope DefaultScope,
    bool IsBoardAware,
    BoardAction RequiredBoardAction = BoardAction.View);

public static class PermissionCatalog
{
    private static readonly PermissionScope[] Fixed = [PermissionScope.All];

    /// <summary>Scopes a ticket-module permission can meaningfully take.</summary>
    private static readonly PermissionScope[] TicketScopes =
        [PermissionScope.All, PermissionScope.Department, PermissionScope.Team, PermissionScope.Assigned, PermissionScope.None];

    private static PermissionDefinition Admin(string key, string module, string name) =>
        new(key, module, name, Fixed, PermissionScope.All, IsBoardAware: false);

    /// <summary>
    /// The four legacy tickets.view.* keys each bake their scope into the key name — a convention
    /// that predates the Scope column. They are declared with exactly one legal scope so the
    /// catalogue tells the truth about them, rather than being collapsed into a single
    /// "tickets.view" + Scope. Collapsing them is a worthwhile separate change; doing it here would
    /// mean rewriting the existing role assertions at the same time as introducing enforcement, so a
    /// failure could be either cause.
    /// </summary>
    private static PermissionDefinition Legacy(string key, string name, PermissionScope only, bool boardAware = true) =>
        new(key, "Tickets", name, [only], only, boardAware, BoardAction.View);

    public static readonly IReadOnlyList<PermissionDefinition> Definitions =
    [
        // Platform
        Admin(Permissions.PlatformManageOrganizations, "Platform", "Manage organizations"),
        Admin(Permissions.PlatformViewAllHealth, "Platform", "View platform health"),
        Admin(Permissions.PlatformManageSettings, "Platform", "Manage platform settings"),

        // Organization / connections / mapping
        Admin(Permissions.OrgManage, "Organization", "Manage organization"),
        Admin(Permissions.ConnectionsManage, "Integrations", "Manage PSA connections"),
        Admin(Permissions.ConnectionsView, "Integrations", "View PSA connections"),
        Admin(Permissions.MappingsManage, "Integrations", "Manage field mappings"),
        Admin(Permissions.MappingsView, "Integrations", "View field mappings"),

        // Users & roles
        Admin(Permissions.UsersManage, "Users", "Manage users"),
        Admin(Permissions.RolesManage, "Users", "Manage roles and permissions"),
        Admin(Permissions.ClientUsersManage, "Users", "Manage client users"),

        // Tickets — the legacy view keys, each pinned to the scope its name already asserts
        Legacy(Permissions.TicketsViewAll, "View all tickets", PermissionScope.All),
        Legacy(Permissions.TicketsViewAssigned, "View assigned tickets", PermissionScope.Assigned),
        Legacy(Permissions.TicketsViewOwnCompany, "View own company's tickets", PermissionScope.Selected, boardAware: false),
        Legacy(Permissions.TicketsViewOwn, "View own tickets", PermissionScope.Own, boardAware: false),

        // Tickets — action permissions, genuinely scope-capable. Note-adding and time-logging both
        // modify the ticket, and the board action vocabulary (View/Create/Edit/Assign/Close/Delete/
        // Manage) has no separate verb for either, so both map to Edit — the same board grant that
        // lets someone edit a ticket already implies they may comment on it or log time against it.
        new(Permissions.TicketsCreate, "Tickets", "Create tickets", TicketScopes, PermissionScope.All, IsBoardAware: true, BoardAction.Create),
        new(Permissions.TicketsAddPublicNote, "Tickets", "Add public notes", TicketScopes, PermissionScope.All, IsBoardAware: true, BoardAction.Edit),
        new(Permissions.TicketsLogTime, "Tickets", "Log time", TicketScopes, PermissionScope.All, IsBoardAware: true, BoardAction.Edit),
        new(Permissions.TicketsUpdate, "Tickets", "Edit tickets", TicketScopes, PermissionScope.All, IsBoardAware: true, BoardAction.Edit),

        // Dashboards & reports
        Admin(Permissions.ReportsView, "Reports", "View reports"),
        Admin(Permissions.ProductivityViewTeam, "Reports", "View team productivity"),
        new(Permissions.ProductivityViewOwn, "Reports", "View own productivity",
            [PermissionScope.Own], PermissionScope.Own, IsBoardAware: false),

        // Ops & audit
        Admin(Permissions.IntegrationHealthView, "Integrations", "View integration health"),
        Admin(Permissions.JobsManage, "Integrations", "Manage background jobs"),
        Admin(Permissions.AuditView, "Audit Log", "View audit log"),
        Admin(Permissions.SecurityConfigView, "Audit Log", "View security configuration"),
        Admin(Permissions.EnquiriesView, "Enquiries", "View enquiries"),
        Admin(Permissions.BoardsManage, "Tickets", "Create and configure internal boards"),

        // Workforce: working schedules and skills (capacity planning, not attendance)
        new(Permissions.ScheduleView, "Workforce", "View working schedules and skills",
            [PermissionScope.All, PermissionScope.Department, PermissionScope.Team, PermissionScope.Own, PermissionScope.None],
            PermissionScope.All, IsBoardAware: false),
        Admin(Permissions.WorkforceManage, "Workforce", "Manage working schedules and skills"),
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static bool TryGet(string key, out PermissionDefinition? definition)
        => ByKey.TryGetValue(key, out definition);

    public static PermissionDefinition Get(string key)
        => ByKey.TryGetValue(key, out var d)
            ? d
            : throw new InvalidOperationException($"Permission '{key}' is not declared in PermissionCatalog.");

    /// <summary>Grouped for the admin UI a later phase builds, in declaration order.</summary>
    public static IEnumerable<IGrouping<string, PermissionDefinition>> ByModule()
        => Definitions.GroupBy(d => d.Module);

    /// <summary>True when board access fences this permission — used by the effective-permission
    /// calculation to decide whether to intersect with the caller's board grants.</summary>
    public static bool IsBoardAware(string key) => ByKey.TryGetValue(key, out var d) && d.IsBoardAware;
}
