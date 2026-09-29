using Desk.Application.Abstractions;
using Desk.Domain.Authorization;

namespace Desk.Api.Auth;

public static class StaffTicketAccess
{
    /// <summary>
    /// Whether the caller works tickets as the MSP: all tickets, or assigned ones. Which tickets they
    /// then see is decided by scope, never here - this only decides staff path or client path.
    /// </summary>
    public static bool SeesStaffTickets(this ICurrentUser user) => Permissions.StaffTicketViews.Any(user.HasPermission);
}
