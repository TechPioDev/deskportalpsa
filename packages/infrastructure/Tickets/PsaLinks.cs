using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Which portal user each PSA login belongs to, where an admin has said so (a user's PSA identity).
///
/// A desk often has fewer PSA logins than people: one licensed technician in Autotask, a whole team
/// working in the portal. Work done directly in the PSA under a linked login is that person's, so it
/// is counted under their portal user - one person, one row - rather than as a second, PSA-only
/// "colleague" with the same name. Everyone else needs no link: they are credited by what they do in
/// the portal. Per connection, like <see cref="IntegrationIdentity"/>: the same id on another PSA is a
/// different login.
///
/// A link to the account the integration writes as is ignored: that account carries the whole portal
/// team's work to the PSA, so treating it as one person's login would hand them everyone's.
/// </summary>
public sealed class PsaLinks
{
    private readonly Dictionary<(Guid Connection, string External), Guid> _userByLogin;

    private PsaLinks(Dictionary<(Guid, string), Guid> userByLogin) => _userByLogin = userByLogin;

    public static async Task<PsaLinks> LoadAsync(DeskDbContext db, CancellationToken ct = default)
    {
        var rows = await db.UserPsaIdentities.AsNoTracking()
            .Select(i => new { i.PsaConnectionId, i.ExternalTechnicianId, i.AppUserId })
            .ToListAsync(ct);
        var account = await IntegrationIdentity.LoadAsync(db, ct);
        var map = new Dictionary<(Guid, string), Guid>();
        foreach (var r in rows)
            if (!string.IsNullOrWhiteSpace(r.ExternalTechnicianId) && !account.IsAccount(r.PsaConnectionId, r.ExternalTechnicianId))
                map[(r.PsaConnectionId, r.ExternalTechnicianId.Trim().ToLowerInvariant())] = r.AppUserId;
        return new(map);
    }

    /// <summary>The portal user a PSA login belongs to, or null when nobody has linked it.</summary>
    public Guid? UserFor(Guid? connectionId, string? externalId)
        => connectionId is { } c && !string.IsNullOrWhiteSpace(externalId)
           && _userByLogin.TryGetValue((c, externalId.Trim().ToLowerInvariant()), out var user)
            ? user
            : null;
}
