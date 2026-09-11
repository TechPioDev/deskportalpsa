using Desk.Domain.Enums;

namespace Desk.Application.Tickets;

/// <summary>The client identity a request acts on behalf of, resolved from the authenticated subject.</summary>
public sealed record ClientAccess(Guid MspOrganizationId, Guid ClientCompanyId, Guid ClientUserId, bool IsCompanyAdministrator);

public sealed record TicketListItem(
    Guid Id,
    string? ExternalTicketId,
    ProviderType Provider,
    string Title,
    string PortalStatus,
    string PortalPriority,
    string? QueueOrBoard,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncedAt,
    string? CustomerName,
    string? ConnectionName,
    // When the PSA says the ticket was RAISED, falling back to the import time only where the
    // provider gave none. Exposed because the client-workload figures are windowed on exactly this
    // date: a list filtered on CreatedAt - the IMPORT date - would disagree with the number that
    // linked to it, and on a freshly connected PSA every ticket shares one import day.
    DateTimeOffset? RaisedAt = null,
    // Worked and billable hours as the PSA totals them per ticket: the same figures the client
    // workload sums, so a list of one client's tickets can show the total that brought someone to it.
    decimal TimeWorkedHours = 0,
    decimal BillableHours = 0,
    // Who holds the ticket and who logged time on it, keyed by PersonKey. STAFF list only: the
    // client list leaves this null, so no technician identity reaches a client, and the UI reads
    // null as "this list has no people to filter by" rather than "nobody worked these".
    IReadOnlyList<TicketPersonRef>? People = null);

public sealed record TicketNoteDto(
    Guid Id,
    string AuthorName,
    bool AuthoredByClient,
    string Body,
    DateTimeOffset CreatedAt,
    // Trailing + defaulted so existing construction sites are unaffected. False only ever reaches
    // STAFF readers — the client detail path filters internal notes out server-side.
    bool IsPublic = true,
    // Set when this note IS a time entry's notes (imported as "te-{id}") OR a reply that logged
    // time — the UI pairs it with the live entry so the thread says whose time it was, how much,
    // and whether it bills.
    string? TimeEntryExternalId = null,
    // Hours/billable carried directly for portal-logged entries, so the thread can state the time
    // even before (or without) the live entry list loading. Null for provider-side te- notes,
    // whose hours only the live PSA fetch knows.
    decimal? TimeEntryHours = null,
    bool? TimeEntryBillable = null);

/// <summary>
/// Ticket detail for the client portal. Contains ONLY public conversation — internal PSA notes
/// are never loaded into this shape (they are not even persisted to the portal).
/// </summary>
public sealed record TicketDetailDto(
    Guid Id,
    string? ExternalTicketId,
    ProviderType Provider,
    string Title,
    string? Description,
    string PortalStatus,
    string PortalPriority,
    string? PortalCategory,
    string? QueueOrBoard,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<TicketNoteDto> Conversation,
    IReadOnlyList<AttachmentDto> Attachments,
    string? CustomerName,
    DateTimeOffset UpdatedAt,
    string? ConnectionName,
    // Ticket service instructions the client set for technicians to follow (account override
    // if present, otherwise the organization-wide default). Trailing + optional so existing
    // construction sites and tests are unaffected.
    string? ServiceInstructions = null,
    // Who the work sits with. The id is the provider's; the name is resolved for display, and is
    // null when the technician can no longer be looked up.
    string? AssignedTechnicianExternalId = null,
    string? AssignedTechnicianName = null,
    // Deep link to the same record in the PSA, for verifying a note or a time entry at source.
    // Null when the connection's endpoint does not match a shape we can map with confidence.
    string? ExternalTicketUrl = null,
    // Who is working it in the PORTAL, which is a separate answer from the provider's assignee
    // above and frequently the only true one: work done by a portal-only technician reaches the
    // PSA under the integration's identity, so the field above names the API user or nobody.
    Guid? AssignedAppUserId = null,
    string? AssignedAppUserName = null);

public sealed record AttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    AttachmentScanStatus ScanStatus,
    DateTimeOffset UploadedAt)
{
    /// <summary>Who attached it. Null for a portal upload by the ticket's own requester.</summary>
    public string? AuthorName { get; init; }

    /// <summary>True when the file came from the PSA rather than being uploaded here.</summary>
    public bool FromProvider { get; init; }

    /// <summary>Conversation entry this file was posted with, so the UI can show it in context.</summary>
    public Guid? TicketNoteId { get; init; }
}

public sealed record CreateTicketInput(
    string Title,
    string? Description,
    string? Priority,
    string? Category,
    string? QueueOrBoard);

public sealed record CreateTicketResultDto(Guid Id, string? ExternalTicketId);

public sealed record NotificationDto(
    Guid TicketId,
    string Title,
    string Kind,
    string Summary,
    DateTimeOffset At);

/// <summary>One entry in the client's notification history. Kind: ticket-created | client-reply |
/// staff-reply | ticket-resolved. Actor is the author for replies, null for lifecycle events.</summary>
public sealed record ActivityEventDto(
    Guid TicketId,
    string TicketTitle,
    string Kind,
    string? Actor,
    DateTimeOffset At);

/// <summary>One person a public reply can be sent to — a contact of the ticket's own client company.</summary>
public sealed record ReplyRecipientDto(string ExternalId, string Name, string Email);

/// <summary>
/// Who a public reply on this ticket reaches. <paramref name="CanChooseRecipients"/> is the
/// provider's answer, not a preference: ConnectWise takes recipients on the note, Autotask decides
/// from its own workflow rules. The portal itself sends no mail in either case, so where this is
/// false the UI states what will happen rather than offering a control nothing honours.
/// </summary>
public sealed record ReplyRecipientsDto(
    string CompanyName,
    bool CanChooseRecipients,
    IReadOnlyList<ReplyRecipientDto> Contacts);
