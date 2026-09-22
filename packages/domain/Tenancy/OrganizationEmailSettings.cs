using Desk.Domain.Common;

namespace Desk.Domain.Tenancy;

/// <summary>
/// The mail account an organization sends from, entered by its administrator in the portal. At most
/// one per organization. The password is not on this row: it lives in the encrypted secret store and
/// the row holds only the reference, the same arrangement PSA connection credentials use.
/// </summary>
public class OrganizationEmailSettings : TenantEntity
{
    /// <summary>
    /// "Smtp" (a mail server and optional login) or "Graph" (Microsoft 365 through the Graph API with an
    /// app registration: tenant, client id, and the client secret in the secret store where the SMTP
    /// password would be). Graph exists because Microsoft is retiring password SMTP sign-in, and direct
    /// send from an unlisted server lands in Junk.
    /// </summary>
    public string Method { get; set; } = "Smtp";
    public string? GraphTenantId { get; set; }
    public string? GraphClientId { get; set; }

    public required string Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>"StartTls", "SslOnConnect" or "None".</summary>
    public string Security { get; set; } = "StartTls";

    public string? Username { get; set; }
    public string? PasswordSecretRef { get; set; }

    public required string FromAddress { get; set; }
    public string FromName { get; set; } = "Desk Portal";

    public Guid? UpdatedByUserId { get; set; }
}
