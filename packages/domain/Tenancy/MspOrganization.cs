using Desk.Domain.Common;

namespace Desk.Domain.Tenancy;

/// <summary>
/// Top-level tenant. Every business record in the platform is scoped to one MSP organization.
/// A single MSP may hold many PSA connections across many providers.
/// </summary>
public class MspOrganization : BaseEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BrandingLogoUrl { get; set; }
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// Who is emailed the "needs attention" digest (failed pushes, stalled sync, closed tickets
    /// without a closed date, reports that were not sent). Null: nobody; the list still shows in
    /// the portal. Comma-separated, same box format as report recipients.
    /// </summary>
    public string? AttentionDigestRecipients { get; set; }

    /// <summary>The organization-local date the digest last went out (or was found empty), so it is sent once a day.</summary>
    public DateOnly? AttentionDigestSentOn { get; set; }

    public ICollection<PsaConnection> PsaConnections { get; set; } = new List<PsaConnection>();
    public ICollection<ClientCompany> ClientCompanies { get; set; } = new List<ClientCompany>();
}
