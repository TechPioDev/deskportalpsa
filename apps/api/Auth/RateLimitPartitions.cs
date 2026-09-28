using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Desk.Api.Auth;

/// <summary>
/// Who a request's rate-limit budget belongs to.
///
/// It used to belong to the ORGANIZATION, which meant one desk of forty people shared a single
/// allowance that a handful of them could spend: one page load is several requests, so a few busy
/// technicians could throttle everybody else. The budget is now the person's own, with a much
/// larger organization ceiling behind it so one tenant still cannot take the host down.
///
/// The order matters. A signed-in caller is identified by their portal user id, falling back to the
/// token subject for a token issued before that claim existed. Anything unauthenticated falls back
/// to the remote address — which, for traffic arriving through the portal's own server, is that
/// server. That is deliberate: it bounds anonymous traffic as a whole, and the routes that have to
/// be anonymous (public forms, provider webhooks, monitoring alerts) each carry their own narrower
/// policy so they can never spend this one.
/// </summary>
public static class RateLimitPartitions
{
    /// <summary>One person's allowance, in a minute.</summary>
    public const int PerUserPermitLimit = 300;

    /// <summary>
    /// The whole organization's ceiling, in a minute. Well above what a desk generates so it is
    /// never met in ordinary use, and low enough that a runaway client cannot exhaust the host.
    /// </summary>
    public const int PerOrganizationPermitLimit = 3000;

    /// <summary>The caller's own budget: their user id, else their token subject, else their address.</summary>
    public static string UserKey(HttpContext context)
    {
        var user = context.User;
        var id = user.FindFirst(CurrentUser.UserIdClaim)?.Value
                 ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(id)) return "user:" + id;
        return "addr:" + (context.Connection.RemoteIpAddress?.ToString() ?? "anon");
    }

    /// <summary>
    /// The organization ceiling, applied only to signed-in callers. Anonymous traffic is already
    /// bounded per address by <see cref="UserKey"/> and by the narrower policies on those routes,
    /// and giving it an organization partition would put every anonymous caller in one bucket.
    /// </summary>
    public static string? OrganizationKey(HttpContext context)
    {
        var org = context.User.FindFirst(CurrentUser.OrgClaim)?.Value;
        return string.IsNullOrWhiteSpace(org) ? null : "org:" + org;
    }
}
