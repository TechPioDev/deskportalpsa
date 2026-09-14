using Desk.Domain.Common;

namespace Desk.Domain.Tenancy;

/// <summary>
/// The mail account an organization sends from, entered by its administrator in the portal. At most
/// one per organization. The password is not on this row: it lives in the encrypted secret store and
/// the row holds only the reference, the same arrangement PSA connection credentials use.
/// </summary>
public class OrganizationEmailSettings : TenantEntity
{
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
