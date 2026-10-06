using Desk.Domain.Enums;
using Desk.PsaCore.Models;

namespace Desk.PsaCore.Contracts;

/// <summary>
/// The single contract every PSA / service-desk connector implements. Provider-specific
/// code lives ONLY behind this interface — controllers, UI, and core services stay
/// provider-neutral (spec §3, Integration Plan connector contract).
///
/// A connector instance is bound to one <see cref="Desk.Domain.Tenancy.PsaConnection"/> and
/// resolves its credentials from the secret store; it never receives raw secrets from callers.
/// </summary>
public interface IServiceManagementConnector
{
    ProviderType Provider { get; }

    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);

    // Directory
    Task<IReadOnlyList<ExternalOrganization>> GetOrganizationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalContact>> GetContactsAsync(string organizationId, CancellationToken ct = default);
    Task<IReadOnlyList<ExternalTechnician>> GetTechniciansAsync(CancellationToken ct = default);

    /// <summary>
    /// Role and queue coverage per technician. Empty for providers that do not model it, in which
    /// case callers fall back to offering every technician rather than none.
    /// </summary>
    Task<IReadOnlyList<ExternalTechnicianAssignment>> GetTechnicianAssignmentsAsync(CancellationToken ct = default);

    /// <summary>Managed devices/assets for a company. Empty when the provider has no asset registry.</summary>
    Task<IReadOnlyList<ExternalDevice>> GetDevicesAsync(string organizationId, CancellationToken ct = default);

    /// <summary>
    /// Points a ticket at a device (or at none). <paramref name="previousDeviceExternalId"/> is the
    /// device the portal set before: a provider that allows several devices per ticket removes only
    /// that one, never the others someone added in the PSA itself.
    /// </summary>
    Task SetTicketDeviceAsync(string ticketId, string? deviceExternalId, string? previousDeviceExternalId, CancellationToken ct = default);

    /// <summary>
    /// Agreements/contracts the provider holds for a company (CW agreements, Autotask contracts).
    /// Only meaningful when <see cref="ProviderCapabilities.SupportsContracts"/> — callers must
    /// check, and connectors without the concept return an empty list rather than throwing.
    /// </summary>
    Task<IReadOnlyList<ExternalAgreement>> GetAgreementsAsync(string organizationId, CancellationToken ct = default);

    /// <summary>
    /// The provider's holiday calendar (all lists/sets merged, deduplicated by date+name) — an MSP
    /// typically maintains one. Only meaningful when
    /// <see cref="ProviderCapabilities.SupportsHolidayCalendars"/>; others return empty.
    /// </summary>
    Task<IReadOnlyList<ExternalHoliday>> GetHolidaysAsync(CancellationToken ct = default);

    /// <summary>
    /// Can this connection log time right now? A read-only pre-flight, so a misconfiguration is
    /// found on the settings page rather than when a technician's hour is rejected.
    /// </summary>
    Task<TimeEntryReadiness> CheckTimeEntryReadinessAsync(CancellationToken ct = default);

    // Tickets
    Task<PaginatedResult<UnifiedTicket>> GetTicketsAsync(TicketFilter filter, CancellationToken ct = default);
    Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default);
    Task<CreateTicketResult> CreateTicketAsync(UnifiedTicketCreateRequest ticket, CancellationToken ct = default);
    Task<UpdateTicketResult> UpdateTicketAsync(string ticketId, UnifiedTicketUpdate update, CancellationToken ct = default);

    // Notes (public/private separation enforced by callers using capabilities)
    Task<IReadOnlyList<UnifiedTicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default);
    Task<CreateNoteResult> AddPublicNoteAsync(string ticketId, UnifiedTicketNoteCreateRequest note, CancellationToken ct = default);

    // Attachments
    Task<IReadOnlyList<UnifiedAttachment>> GetAttachmentsAsync(string ticketId, CancellationToken ct = default);
    Task<CreateAttachmentResult> AddAttachmentAsync(string ticketId, SecureAttachment attachment, CancellationToken ct = default);

    /// <summary>
    /// Every attachment added across the tenant since <paramref name="since"/> (all of them when
    /// null). Needed because providers do not necessarily touch a ticket's modified timestamp when a
    /// file is attached to it — Autotask does not — so a ticket-driven incremental sync would never
    /// notice new files. Returns <see cref="ProviderAttachmentSweep.Unsupported"/> for providers that
    /// cannot query attachments by date. <see cref="ProviderAttachmentSweep.Complete"/> must be false
    /// whenever anything asked for may be missing from the answer.
    /// </summary>
    Task<ProviderAttachmentSweep> GetRecentAttachmentsAsync(DateTimeOffset? since, CancellationToken ct = default);

    /// <summary>Pulls an attachment's bytes back from the provider. Null when the provider cannot
    /// serve the content (unsupported, deleted, or too large to inline). The parent ticket is
    /// required because some providers only expose content on the ticket's child route.</summary>
    Task<DownloadedAttachment?> DownloadAttachmentAsync(string ticketId, string attachmentId, CancellationToken ct = default);

    // Time
    Task<IReadOnlyList<UnifiedTimeEntry>> GetTimeEntriesAsync(string ticketId, CancellationToken ct = default);
    Task<CreateTimeEntryResult> AddTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, CancellationToken ct = default);
    Task<UpdateTimeEntryResult> UpdateTimeEntryAsync(string entryId, UnifiedTimeEntryUpdate update, CancellationToken ct = default);
    Task<UpdateTimeEntryResult> DeleteTimeEntryAsync(string entryId, CancellationToken ct = default);

    // Field discovery (live from the connected tenant — never hard-coded)
    Task<IReadOnlyList<ExternalFieldOption>> GetStatusesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldOption>> GetPrioritiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldOption>> GetQueuesOrBoardsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldOption>> GetCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldOption>> GetWorkTypesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldOption>> GetWorkRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExternalFieldDefinition>> GetCustomFieldsAsync(CancellationToken ct = default);

    // Webhooks
    /// <summary>
    /// The PSA's id for a time entry that an earlier, unanswered <see cref="AddTimeEntryAsync"/> of
    /// this same request already created on the ticket, or null when there is none.
    ///
    /// A push whose answer was lost may have been carried out. Sent again blind it is a second
    /// entry, and the customer is billed the hour twice. Only the connector can say whether it is
    /// there: it knows what it would have sent - which technician stands in when none is named,
    /// what it writes for empty notes, how it dates an entry.
    /// </summary>
    /// <param name="since">When the portal first tried. Nothing created before it can be this entry.</param>
    /// <param name="alreadyLinked">Entries on the ticket that already belong to another portal entry.</param>
    /// <remarks>The default is "cannot tell": a connector that has not implemented this is retried as before.</remarks>
    Task<string?> FindTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, DateTimeOffset since,
        IReadOnlyCollection<string> alreadyLinked, CancellationToken ct = default) => Task.FromResult<string?>(null);

    Task<WebhookValidationResult> ValidateWebhookAsync(WebhookRequest request, CancellationToken ct = default);
    Task<NormalizedProviderEvent> ProcessWebhookAsync(WebhookRequest request, CancellationToken ct = default);
}

/// <summary>
/// Resolves a connector instance for a given PSA connection. Registered per provider;
/// adding a provider means registering a new factory — no core code changes.
/// </summary>
public interface IConnectorFactory
{
    ProviderType Provider { get; }

    /// <summary>Build a connector bound to the given connection id (credentials resolved internally).</summary>
    Task<IServiceManagementConnector> CreateAsync(Guid psaConnectionId, CancellationToken ct = default);
}
