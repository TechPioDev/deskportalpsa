using Desk.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace Desk.Api.Auth;

/// <summary>
/// Authorization requirement satisfied when the caller holds ANY of the listed permissions.
///
/// Any-of rather than all-of, because the cases that need more than one key are views serving two
/// audiences from one endpoint — a technician reading their own figures and a manager reading the
/// team's. Requiring both would lock out each of them in turn; the alternative, gating on one and
/// checking the other by hand inside the action, puts half the rule somewhere nobody looks for it.
/// </summary>
public sealed class PermissionRequirement(params string[] permissionKeys) : IAuthorizationRequirement
{
    public IReadOnlyList<string> PermissionKeys { get; } = permissionKeys;
}

public sealed class PermissionAuthorizationHandler(ICurrentUser currentUser)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.PermissionKeys.Any(currentUser.HasPermission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds authorization policies on demand for names of the form "perm:&lt;key&gt;", so a
/// controller can declare <c>[Authorize(Policy = Policy.For(Permissions.X)]</c> without
/// registering every policy up front.
/// </summary>
public sealed class PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    public const string Prefix = "perm:";
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    /// <summary>Separator for an any-of policy name. Not a character any permission key contains.</summary>
    public const char Any = '|';

    public static string For(params string[] permissionKeys) => Prefix + string.Join(Any, permissionKeys);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..].Split(Any)))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }
        return _fallback.GetPolicyAsync(policyName);
    }
}

/// <summary>Convenience attribute: <c>[RequirePermission(Permissions.TicketsCreate)]</c>.</summary>
public sealed class RequirePermissionAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    /// <summary>Holding ANY of these is enough. One key is the ordinary case.</summary>
    public RequirePermissionAttribute(params string[] permissionKeys)
        => Policy = PermissionPolicyProvider.For(permissionKeys);
}
