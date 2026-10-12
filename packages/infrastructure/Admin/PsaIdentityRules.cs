using Desk.Application.Common;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// One person per PSA login on a connection. A link says "this login IS this person": work done in
/// the PSA under it is credited to them, and tickets assigned to it are theirs to see. With two
/// people on one login the later link silently won, and listing the connection's technicians
/// failed outright. The database refuses a second link; this says so in words first.
/// </summary>
public static class PsaIdentityRules
{
    public static async Task EnsureLoginIsFreeAsync(
        DeskDbContext db, Guid psaConnectionId, string externalTechnicianId, Guid forUserId, CancellationToken ct)
    {
        var holder = await db.UserPsaIdentities.AsNoTracking()
            .Where(i => i.PsaConnectionId == psaConnectionId
                        && i.ExternalTechnicianId == externalTechnicianId
                        && i.AppUserId != forUserId)
            .Select(i => i.AppUser!.DisplayName)
            .FirstOrDefaultAsync(ct);
        if (holder is not null)
            throw new ValidationFailedException(
                $"That PSA login is already linked to {holder}. Remove that link first, or choose another login.");
    }

    /// <summary>
    /// Removes the "leave this login alone" decision for a login that is being linked to a person.
    /// Linked says the login IS someone; ignored says it is nobody; a login is not both. Not saved:
    /// the caller saves it with the link.
    /// </summary>
    public static async Task StopIgnoringAsync(DeskDbContext db, Guid psaConnectionId, string externalTechnicianId, CancellationToken ct = default)
    {
        var id = externalTechnicianId.Trim();
        var rows = (await db.PsaTechnicianIgnores.Where(i => i.PsaConnectionId == psaConnectionId).ToListAsync(ct))
            .Where(i => string.Equals(i.ExternalTechnicianId.Trim(), id, StringComparison.OrdinalIgnoreCase));
        db.PsaTechnicianIgnores.RemoveRange(rows);
    }
}
