using Desk.Domain.Common;

namespace Desk.Domain.Identity;

/// <summary>
/// An internal platform user (technician, manager, MSP admin, auditor, platform super-admin).
/// Client-side users are modelled separately as <see cref="Desk.Domain.Tenancy.ClientUser"/>.
///
/// Platform super-administrators are the only users allowed to be cross-tenant; all other
/// users are pinned to a single MSP organization.
/// </summary>
public class AppUser : BaseEntity
{
    /// <summary>Null only for platform super-administrators (cross-tenant).</summary>
    public Guid? MspOrganizationId { get; set; }

    public required string Email { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Subject (sub) claim from the identity provider (Keycloak).</summary>
    public string? IdpSubject { get; set; }

    /// <summary>Identifier of the matching resource/member in the external PSA, if this user is a technician.</summary>
    public string? ExternalTechnicianId { get; set; }

    public bool IsActive { get; set; } = true;

    public string? PhoneNumber { get; set; }
    public string? Location { get; set; }

    /// <summary>The staff user this person reports to. Self-referencing; a manager need not hold
    /// any particular role — this is org structure, not access.</summary>
    public Guid? ManagerId { get; set; }
    public AppUser? Manager { get; set; }

    /// <summary>Opaque storage key for an uploaded profile photo — same pattern as
    /// <see cref="Tenancy.PsaConnection.LogoStorageKey"/>. Null until one is uploaded.</summary>
    public string? PhotoStorageKey { get; set; }
    /// <summary>Served URL for the photo, routed back through the API — same pattern as
    /// <see cref="Tenancy.PsaConnection.LogoUrl"/>.</summary>
    public string? PhotoUrl { get; set; }

    /// <summary>
    /// Last time this account made an authenticated request. The app has no session/login-event
    /// tracking — bearer tokens are validated fresh on every call — so this is the one real signal
    /// available, not a true "last sign-in" timestamp. Written by DeskClaimsTransformation, and only
    /// when stale by more than a few minutes: that transformation runs on every authenticated
    /// request, so an unconditional write here would be a write on every single API call.
    /// </summary>
    public DateTimeOffset? LastActiveAt { get; set; }

    /// <summary>
    /// Whether this person is offered for planned work. Off for someone who is active in the portal
    /// but is not given work (a manager who does not take tickets, someone on long leave): they keep
    /// their schedule and skills, and are simply left out when work is planned. An inactive account is
    /// never offered, whatever this says.
    /// </summary>
    public bool IsSchedulable { get; set; } = true;

    public ICollection<UserRole> Roles { get; set; } = new List<UserRole>();
}
