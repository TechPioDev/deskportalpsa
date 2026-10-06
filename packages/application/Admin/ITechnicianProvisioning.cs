namespace Desk.Application.Admin;

/// <summary>
/// How a PSA technician relates to this portal today. The point of the whole screen is that these
/// three states look different: what is already wired, what needs one click, and what would create
/// a new account.
/// </summary>
public enum PsaTechnicianLink
{
    /// <summary>No portal user. Provisioning creates one.</summary>
    NotInPortal = 0,
    /// <summary>A portal user shares this email but is not mapped to this PSA identity yet.</summary>
    MatchedByEmail = 1,
    /// <summary>Already mapped on this connection. Nothing to do.</summary>
    Linked = 2,
    /// <summary>
    /// An administrator has said this login is nobody the portal needs to know as one of its people
    /// (an API or service account, someone who left). It is not counted as still to link.
    /// </summary>
    Ignored = 3,
}

/// <summary>
/// One technician as the PSA describes them, alongside what the portal already knows about them.
/// <paramref name="CanProvision"/> is false where the PSA gives no email: the portal binds sign-in
/// by verified email, so an account without one could never be logged into.
/// </summary>
public sealed record PsaTechnicianDto(
    string ExternalId,
    string Name,
    string Email,
    bool IsActive,
    PsaTechnicianLink Link,
    Guid? PortalUserId,
    bool CanProvision,
    string? Blocker);

/// <summary>
/// Bringing PSA technicians into the portal as portal users.
///
/// Deliberately NOT automatic. A PSA's resource list contains API users, service accounts and people
/// who left, and every one auto-created here would become a real login on a real tenant. These are
/// suggestions an administrator confirms one at a time.
/// </summary>
public interface ITechnicianProvisioningService
{
    /// <summary>Every technician on this connection, with what the portal already knows.</summary>
    Task<IReadOnlyList<PsaTechnicianDto>> ListAsync(Guid psaConnectionId, CancellationToken ct = default);

    /// <summary>
    /// Creates the portal user for this PSA technician (or links an existing one with the same
    /// email) and maps them to this connection, so their logged time is attributed to them.
    /// Idempotent: running it again on someone already linked changes nothing.
    /// </summary>
    Task<UserSummary> ProvisionAsync(Guid psaConnectionId, string externalTechnicianId, CancellationToken ct = default);

    /// <summary>
    /// Says a PSA login is to be left alone, or takes that back. Nothing about its tickets or time
    /// changes: they are shown under the PSA's own name, as for any unlinked login. A login that is
    /// linked to a portal user cannot be ignored. Audited.
    /// </summary>
    Task SetIgnoredAsync(Guid psaConnectionId, string externalTechnicianId, bool ignored, string? name, CancellationToken ct = default);
}
