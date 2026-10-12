using Desk.Domain.Common;

namespace Desk.Domain.Tenancy;

/// <summary>
/// A client company. Most come from a PSA connection: different clients within one MSP may be
/// routed to different PSA connections (even different providers).
///
/// Some are in no PSA at all: a client the desk works for and has not set up there, added by hand
/// so that the team's own tickets can say who the work was for (the owner's ask of 12 October
/// 2026). Such a company has no connection. Nothing about it is ever read from or written to a
/// PSA, it has no client sign-ins, and a client is shown nothing because of it.
/// </summary>
public class ClientCompany : TenantEntity
{
    /// <summary>The connection it came from. Null for a company added by hand, which is in no PSA.</summary>
    public Guid? PsaConnectionId { get; set; }
    public PsaConnection? PsaConnection { get; set; }

    /// <summary>Added by hand, in no PSA.</summary>
    public bool IsLocal => PsaConnectionId is null;

    /// <summary>What a hand-added company carries where a PSA's own id would be: unique, and recognisably not one.</summary>
    public static string LocalExternalId() => "local:" + Guid.NewGuid().ToString("N");

    public required string Name { get; set; }

    /// <summary>Identifier of the matching company/account in the external PSA.</summary>
    public required string ExternalCompanyId { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<ClientUser> Users { get; set; } = new List<ClientUser>();
}
