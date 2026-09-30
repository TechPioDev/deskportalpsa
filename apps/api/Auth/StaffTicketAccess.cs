using Desk.Application.Abstractions;
using Desk.Application.Authorization;
using Desk.Domain.Authorization;

namespace Desk.Api.Auth;

public static class StaffTicketAccess
{
    /// <summary>
    /// Whether the caller works tickets as the MSP: all tickets, or assigned ones. Which tickets they
    /// then see is decided by scope, never here - this only decides staff path or client path.
    /// </summary>
    public static bool SeesStaffTickets(this ICurrentUser user) => Permissions.StaffTicketViews.Any(user.HasPermission);

    /// <summary>
    /// Whether the caller sees EVERY ticket in the organization: tickets.view.all at All scope. For
    /// organization-wide lists that name tickets (the attention list, unsynced tickets) where
    /// narrowing per viewer would change what the list means. Holding the key is not enough - a role
    /// can hold it at Department scope.
    /// </summary>
    public static async Task<bool> SeesEveryTicketAsync(this ICurrentUser user, IEffectivePermissionService permissions, CancellationToken ct)
        => user.UserId is { } uid
           && (await permissions.ResolveAsync(uid, Permissions.TicketsViewAll, ct)).Scope == PermissionScope.All;
}
