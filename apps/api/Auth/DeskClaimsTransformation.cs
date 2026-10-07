using System.Security.Claims;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Auth;

/// <summary>
/// After the JWT is validated, enrich the principal with the DB-authoritative org id and
/// permission claims for the matching internal user. Keeping the DB as the source of truth
/// means access can change without re-issuing tokens. Runs idempotently per request.
/// </summary>
public sealed class DeskClaimsTransformation(
    DeskDbContext db, TimeProvider clock, IHttpContextAccessor? http = null, ILogger<DeskClaimsTransformation>? logger = null)
    : Microsoft.AspNetCore.Authentication.IClaimsTransformation
{
    /// <summary>How stale AppUser.LastActiveAt must be before it's worth a write. This method runs
    /// on every authenticated request (stateless bearer tokens, no session), so writing on every
    /// call would be a write-per-API-call hot path; this keeps it to roughly one write per active
    /// user per window while still giving a meaningfully fresh signal.</summary>
    private static readonly TimeSpan ActivityThrottle = TimeSpan.FromMinutes(5);


    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true })
            return principal;

        // Already enriched?
        if (principal.HasClaim(c => c.Type == CurrentUser.OrgClaim || c.Type == CurrentUser.PlatformScopeClaim))
            return principal;

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
            return principal;

        // AppUser is not tenant-scoped, so this lookup is safe before any tenant scope exists.
        var user = await db.AppUsers
            .AsNoTracking()
            .Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.IdpSubject == subject && u.IsActive);

        // First login for an invited user: no row carries this subject yet, but an active account
        // created by an admin is waiting on the token's VERIFIED email. Bind the subject now, once —
        // afterwards resolution is by subject alone, so a later email change cannot re-bind.
        if (user is null)
        {
            var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
            if (!string.IsNullOrEmpty(email))
            {
                var invited = await OnlyInvitationAsync(
                    db.AppUsers.Include(u => u.Roles)
                        .Where(u => u.IdpSubject == null && u.IsActive && u.Email.ToLower() == email.ToLower())
                        .OrderBy(u => u.Id),
                    "staff");
                if (invited is not null)
                {
                    invited.IdpSubject = subject;
                    await db.SaveChangesAsync();
                    user = invited;
                }
            }
        }
        // Not staff — a CLIENT portal user, perhaps. Staff resolution runs first so a dual
        // identity (an AppUser who is also a company contact) keeps its staff powers.
        if (user is null)
            return await TransformClientAsync(principal, subject);

        var now = clock.GetUtcNow();
        if (user.LastActiveAt is null || now - user.LastActiveAt > ActivityThrottle)
        {
            // A targeted update rather than re-loading the tracked entity: this runs on every
            // authenticated request, so the write itself has to stay as cheap as the throttle it's
            // guarded by is meant to make it.
            await db.AppUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.LastActiveAt, now));
        }

        var (identity, granted, isPlatform) = await StaffIdentityAsync(user);

        // "View as": an administrator asked to see the portal as someone else. Honoured only when the
        // REAL caller may (checked here, on every request - the header alone grants nothing), and only
        // for someone they may see. Then the request runs as that person, read-only, with markers
        // saying who is really looking; otherwise it runs as the caller, as if nothing was asked.
        var asked = http?.HttpContext?.Request.Headers[ViewAs.Header].ToString();
        if (!string.IsNullOrEmpty(asked) && ViewAs.MayViewAs(granted))
        {
            var callerName = principal.FindFirstValue("name") ?? user.DisplayName;
            var (target, _) = await ViewAs.ResolveAsync(db, asked, user.Id, user.MspOrganizationId, isPlatform);
            if (target is not null)
            {
                var viewed = target.Kind == "staff"
                    ? (await StaffIdentityAsync(await db.AppUsers.AsNoTracking().Include(u => u.Roles).SingleAsync(u => u.Id == target.Id))).Identity
                    : ClientIdentity(target.OrganizationId!.Value);
                viewed.AddClaims(ViewAs.MarkerClaims(target, user.Id, callerName));
                principal.AddIdentity(viewed);
                return principal;
            }
        }

        principal.AddIdentity(identity);
        return principal;
    }

    /// <summary>
    /// The one account waiting on this e-mail, or null when there is none - or more than one.
    ///
    /// An e-mail is unique within an organization (staff) or within a client company (clients), not
    /// across them, and a client company belongs to one PSA connection. So the same address can be
    /// waiting in two places: the same contact under two connections, or two organizations inviting
    /// the same person. Which of them a first sign-in means is not something to guess - binding
    /// either would open the wrong organization's or company's data half the time - so neither is
    /// bound. It used to be an exception on every request instead: the person could not sign in at
    /// all, and nothing said why.
    /// </summary>
    private async Task<T?> OnlyInvitationAsync<T>(IOrderedQueryable<T> waiting, string kind) where T : class
    {
        // Two rows are enough to know there is more than one. Ordered, because a row limit without
        // an order is a query the database may answer differently each time, and says so in the log.
        var found = await waiting.Take(2).ToListAsync();
        if (found.Count > 1)
            logger?.LogWarning(
                "First sign-in not bound: more than one unclaimed {Kind} account is waiting on the same e-mail address. "
                + "Deactivate or remove all but one, then ask the person to sign in again.", kind);
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>A staff member's portal identity: who they are, their organization, and what they may do.</summary>
    private async Task<(ClaimsIdentity Identity, HashSet<string> Granted, bool IsPlatform)> StaffIdentityAsync(Desk.Domain.Identity.AppUser user)
    {
        var roleIds = user.Roles.Select(r => r.RoleId).ToList();
        var roles = await db.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Include(r => r.Permissions)
            .ToListAsync();

        var identity = new ClaimsIdentity();

        // Who this is. Emitted here because this is the one place per request that already has the
        // AppUser loaded — anything scoping data to "this person's own work" would otherwise
        // re-query for it on every call site.
        //
        // Only the portal id. A person's PSA logins are per connection (UserPsaIdentity) and are
        // looked up where they are needed; a single technician id in the token could only ever be
        // right for one PSA account, and would name somebody else on a second.
        identity.AddClaim(new Claim(CurrentUser.UserIdClaim, user.Id.ToString()));

        var isPlatform = roles.Any(r => r.BuiltInType == RoleType.PlatformSuperAdministrator);
        if (isPlatform)
            identity.AddClaim(new Claim(CurrentUser.PlatformScopeClaim, "true"));
        else if (user.MspOrganizationId is { } org)
            identity.AddClaim(new Claim(CurrentUser.OrgClaim, org.ToString()));

        var granted = roles.SelectMany(r => r.Permissions).Select(p => p.PermissionKey).ToHashSet();

        // A per-user override REPLACES the role-derived answer for its key, not just at the
        // fine-grained scope level resolved later by IEffectivePermissionService — the coarse
        // claim-presence gate (`[RequirePermission]`) has to agree with it too, or a Deny override
        // would stop nothing: the endpoint-level check would still see the role's claim and let the
        // request through, leaving only the later, easier-to-forget scope check standing between the
        // caller and the denied action. A Grant override works the same way in reverse — it can hand
        // out a permission no role held at all.
        var overrides = await db.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.AppUserId == user.Id)
            .ToListAsync();
        foreach (var o in overrides)
        {
            if (o.Effect == PermissionEffect.Deny) granted.Remove(o.PermissionKey);
            else granted.Add(o.PermissionKey);
        }

        foreach (var perm in granted)
            identity.AddClaim(new Claim(CurrentUser.PermissionClaim, perm));
        return (identity, granted, isPlatform);
    }

    /// <summary>A client portal user's identity: their organization and the fixed client permissions.</summary>
    private static ClaimsIdentity ClientIdentity(Guid organizationId)
    {
        var identity = new ClaimsIdentity();
        // Org only — no UserIdClaim: that is the AppUser id space, and the staff fallbacks that
        // read it must keep seeing "no staff identity" for a client caller.
        identity.AddClaim(new Claim(CurrentUser.OrgClaim, organizationId.ToString()));
        foreach (var perm in ClientClaims)
            identity.AddClaim(new Claim(CurrentUser.PermissionClaim, perm));
        return identity;
    }

    /// <summary>
    /// The fixed claim set a pure client-portal login receives. Deliberately NOT role-driven:
    /// clients are not staff, and every client-reachable endpoint either needs only these two
    /// permissions or authorizes through <see cref="Desk.Application.Tickets.IClientAccessResolver"/>
    /// company scoping. Growing this list is a security decision, not a convenience.
    /// </summary>
    private static readonly string[] ClientClaims = [Permissions.TicketsCreate, Permissions.TicketsAddPublicNote];

    /// <summary>
    /// Resolves a CLIENT portal user. Without this branch a pure client login authenticated and then
    /// hit a wall: no org claim (so the tenant filter returned zero rows) and no permission claims
    /// (so every gate refused) — only people who were ALSO staff could ever use the client portal.
    ///
    /// ClientUser is tenant-scoped and no tenant exists yet at claims time, so lookups here must
    /// ignore query filters; resolution is by unique IdP subject (then a one-time verified-email
    /// bind for invited users, mirroring the staff path above).
    /// </summary>
    private async Task<ClaimsPrincipal> TransformClientAsync(ClaimsPrincipal principal, string subject)
    {
        var client = await db.ClientUsers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.IdpSubject == subject && u.IsActive);

        if (client is null)
        {
            var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
            if (!string.IsNullOrEmpty(email))
            {
                var invited = await OnlyInvitationAsync(
                    db.ClientUsers.IgnoreQueryFilters()
                        .Where(u => u.IdpSubject == null && u.IsActive && u.Email.ToLower() == email.ToLower())
                        .OrderBy(u => u.Id),
                    "client");
                if (invited is not null)
                {
                    invited.IdpSubject = subject;
                    await db.SaveChangesAsync();
                    client = invited;
                }
            }
        }
        if (client is null)
            return principal;

        principal.AddIdentity(ClientIdentity(client.MspOrganizationId));
        return principal;
    }
}
