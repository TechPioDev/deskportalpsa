using System.Security.Claims;
using Desk.Application.Abstractions;

namespace Desk.Api.Auth;

/// <summary>Reads the authenticated caller from the current HTTP request's claims principal.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string OrgClaim = "desk_org";
    public const string PermissionClaim = "desk_perm";
    public const string PlatformScopeClaim = "desk_platform";
    public const string UserIdClaim = "desk_uid";
    public const string TechnicianClaim = "desk_tech";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
    // While viewing as someone, the token still names the administrator; the person viewed wins.
    public string? Subject => Principal?.FindFirstValue(ViewAs.SubjectClaim)
        ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Principal?.FindFirstValue("sub");
    public string? Email => Principal?.FindFirstValue(ViewAs.EmailClaim)
        ?? Principal?.FindFirstValue(ClaimTypes.Email) ?? Principal?.FindFirstValue("email");
    public string? DisplayName => Principal?.FindFirstValue(ViewAs.NameClaim) ?? Principal?.FindFirstValue("name") ?? Email;

    public Guid? ViewedByUserId => Guid.TryParse(Principal?.FindFirstValue(ViewAs.ByClaim), out var by) ? by : null;
    public string? ViewedByName => Principal?.FindFirstValue(ViewAs.ByNameClaim);
    /// <summary>"staff" or "client" while viewing as someone.</summary>
    public string? ViewKind => Principal?.FindFirstValue(ViewAs.KindClaim);

    public Guid? OrganizationId =>
        Guid.TryParse(Principal?.FindFirstValue(OrgClaim), out var id) ? id : null;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(UserIdClaim), out var uid) ? uid : null;

    public string? TechnicianExternalId => Principal?.FindFirstValue(TechnicianClaim);

    public IReadOnlySet<string> Permissions =>
        Principal?.FindAll(PermissionClaim).Select(c => c.Value).ToHashSet() ?? new HashSet<string>();

    public bool HasPermission(string permissionKey) => Permissions.Contains(permissionKey);
}
