namespace Desk.PsaCore.Contracts;

public enum AuthenticationType
{
    ApiKey,
    OAuth2ClientCredentials,
    OAuth2AuthorizationCode,
    BasicAuth,
    PersonalAccessToken,
}

/// <summary>
/// Declares exactly what a provider connector supports. The frontend and sync engine read
/// this matrix to hide/disable unsupported functions rather than assuming feature parity
/// (Integration Plan: "Create a provider capability matrix instead of assuming all
/// integrations are identical"). Never force behaviour a provider cannot do.
/// </summary>
public record ProviderCapabilities
{
    public bool SupportsTicketCreate { get; init; }
    public bool SupportsTicketUpdate { get; init; }
    public bool SupportsTicketDelete { get; init; }
    public bool SupportsPublicNotes { get; init; }
    public bool SupportsPrivateNotes { get; init; }

    /// <summary>
    /// Whether the caller can choose who a public note is emailed to. Neither real provider allows
    /// it: ConnectWise's ServiceNote has no recipient fields (it notifies by board rules when
    /// processNotifications is set) and Autotask's TicketNote notifies from its workflow rules. The
    /// portal sends no mail of its own either way, so where this is false the UI must state who the
    /// provider will notify rather than offer a choice it cannot honour.
    /// </summary>
    public bool SupportsNoteEmailRecipients { get; init; }
    public bool SupportsAttachments { get; init; }

    /// <summary>
    /// Whether the provider will serve an attachment's bytes back and can be swept for new files.
    /// Distinct from <see cref="SupportsAttachments"/>: a provider can accept uploads and still have
    /// no readable download route, which makes inbound attachment sync impossible rather than merely
    /// unimplemented.
    /// </summary>
    public bool SupportsAttachmentDownload { get; init; }

    /// <summary>
    /// True when the provider can answer "every attachment added since X" tenant-wide. Autotask can;
    /// ConnectWise indexes documents per record with no dated query, so its files must be read one
    /// ticket at a time instead.
    /// </summary>
    public bool SupportsAttachmentSweep { get; init; }
    public bool SupportsTimeEntries { get; init; }
    public bool SupportsAssets { get; init; }
    public bool SupportsContracts { get; init; }

    /// <summary>Provider maintains a holiday calendar the portal can import (CW holiday lists, AT holiday sets).</summary>
    public bool SupportsHolidayCalendars { get; init; }
    public bool SupportsSlaData { get; init; }
    public bool SupportsCustomFields { get; init; }
    public bool SupportsInboundWebhooks { get; init; }
    public bool SupportsOutboundWebhooks { get; init; }
    public bool SupportsIncrementalSync { get; init; }
    public bool SupportsBulkRead { get; init; }
    public bool SupportsBulkWrite { get; init; }
    public bool SupportsCompanies { get; init; }
    public bool SupportsContacts { get; init; }
    public bool SupportsTechnicians { get; init; }
    public bool SupportsTeams { get; init; }
    public bool SupportsQueues { get; init; }

    public int? MaximumPageSize { get; init; }
    public long? MaximumAttachmentSize { get; init; }
    public string? RateLimitModel { get; init; }
    public IReadOnlyList<AuthenticationType> AuthenticationTypes { get; init; }
        = Array.Empty<AuthenticationType>();
}
