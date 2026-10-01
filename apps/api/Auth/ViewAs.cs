using System.Security.Claims;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Auth;

/// <summary>
/// "View as": an administrator sees the portal exactly as one of its users does - their menu, their
/// tickets, their figures - without their password and without acting as them.
///
/// The browser never names the person directly: the web app keeps the choice in an httpOnly cookie
/// and its proxy turns it into <see cref="Header"/>. That grants nothing on its own. On every request
/// <see cref="DeskClaimsTransformation"/> checks the REAL caller may view as (<see cref="MayViewAs"/>)
/// and that the person is someone they may see (<see cref="ResolveAsync"/>); only then is the
/// request run as that person, and only for reading - <see cref="Middleware.ViewAsReadOnlyMiddleware"/>
/// refuses every change. Starting and ending are audited under the administrator's own name.
/// </summary>
public static class ViewAs
{
    public const string Header = "X-Desk-View-As";

    // Who is really looking, and as whom. Present only while viewing as someone.
    public const string ByClaim = "desk_view_by";
    public const string ByNameClaim = "desk_view_by_name";
    public const string KindClaim = "desk_view_kind";
    public const string SubjectClaim = "desk_view_subject";
    public const string NameClaim = "desk_view_name";
    public const string EmailClaim = "desk_view_email";

    /// <summary>
    /// Only someone who already decides who has which access may look through anyone's eyes: managing
    /// users AND roles, which the built-in Administrator holds and a Manager does not.
    /// </summary>
    public static bool MayViewAs(IReadOnlySet<string> callerPermissions)
        => callerPermissions.Contains(Permissions.UsersManage) && callerPermissions.Contains(Permissions.RolesManage);

    public static bool IsViewing(ClaimsPrincipal? principal) => principal?.HasClaim(c => c.Type == ByClaim) ?? false;

    /// <summary>The person to view as, once the request for them has been checked.</summary>
    public sealed record Target(string Key, string Kind, Guid Id, string Name, string Email, Guid? OrganizationId, string? Subject);

    /// <summary>
    /// "u:&lt;staff id&gt;" or "c:&lt;client portal user id&gt;" into a person the caller may view as, or the
    /// reason they may not. Same organization only (a platform administrator excepted); never
    /// yourself; never a platform administrator unless you are one; a client only once they have
    /// signed in, because a client's access is found by their sign-in identity.
    /// </summary>
    public static async Task<(Target? Target, string? Refusal)> ResolveAsync(
        DeskDbContext db, string? key, Guid callerUserId, Guid? callerOrg, bool callerIsPlatform, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 3 || key[1] != ':' || !Guid.TryParse(key[2..], out var id))
            return (null, "Choose who to view as.");

        if (key[0] == 'u')
        {
            if (id == callerUserId) return (null, "That is you.");
            var staff = await db.AppUsers.AsNoTracking().Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
            if (staff is null || (!callerIsPlatform && staff.MspOrganizationId != callerOrg))
                return (null, "That person was not found, or is not active.");
            var roleIds = staff.Roles.Select(r => r.RoleId).ToList();
            var isPlatform = await db.Roles.AnyAsync(r => roleIds.Contains(r.Id) && r.BuiltInType == RoleType.PlatformSuperAdministrator, ct);
            if (isPlatform && !callerIsPlatform)
                return (null, "Only a platform administrator can view as a platform administrator.");
            return (new Target(key, "staff", staff.Id, staff.DisplayName, staff.Email, staff.MspOrganizationId, staff.IdpSubject), null);
        }

        if (key[0] == 'c')
        {
            var client = await db.ClientUsers.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
            if (client is null || (!callerIsPlatform && client.MspOrganizationId != callerOrg))
                return (null, "That person was not found, or is not active.");
            if (string.IsNullOrEmpty(client.IdpSubject))
                return (null, $"{client.DisplayName} has not signed in yet, so there is nothing of theirs to show.");
            return (new Target(key, "client", client.Id, client.DisplayName, client.Email, client.MspOrganizationId, client.IdpSubject), null);
        }

        return (null, "Choose who to view as.");
    }

    /// <summary>The claims that say who is really looking, added beside the viewed person's own.</summary>
    public static IEnumerable<Claim> MarkerClaims(Target target, Guid callerUserId, string callerName)
    {
        yield return new Claim(ByClaim, callerUserId.ToString());
        yield return new Claim(ByNameClaim, callerName);
        yield return new Claim(KindClaim, target.Kind);
        yield return new Claim(NameClaim, target.Name);
        yield return new Claim(EmailClaim, target.Email);
        // Never the administrator's own subject: anything keyed on it (a client contact who is also
        // staff, the audit's actor) must not find the administrator. A staff member who never signed
        // in has no subject, and gets one that matches nobody.
        yield return new Claim(SubjectClaim, string.IsNullOrEmpty(target.Subject) ? $"view-as:{target.Id}" : target.Subject);
    }
}
