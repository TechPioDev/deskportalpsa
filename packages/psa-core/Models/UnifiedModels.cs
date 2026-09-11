namespace Desk.PsaCore.Models;

/// <summary>Normalized company/account/organization as returned by any provider.</summary>
public record ExternalOrganization(string ExternalId, string Name, bool IsActive);

/// <summary>Normalized contact/user/caller.</summary>
public record ExternalContact(string ExternalId, string Email, string DisplayName, bool IsActive);

/// <summary>Normalized technician/resource/member/agent.</summary>
public record ExternalTechnician(string ExternalId, string Email, string DisplayName, bool IsActive);

/// <summary>A managed device/asset belonging to a company (CW "configuration", Autotask "installed product").</summary>
public record ExternalDevice(string ExternalId, string Name, string? Type, string? Identifier, bool IsActive);

/// <summary>A selectable option for a picklist field (status/priority/queue/category).</summary>
/// <summary>
/// One selectable value on a provider field, in the two forms callers actually need.
///
/// <see cref="Value"/> is what the PROVIDER is sent: queue ids in an import filter, a queue id when
/// a ticket is reassigned. <see cref="SyncValue"/> is what a synced ticket ARRIVES carrying, and is
/// therefore the only thing a mapping rule can usefully hold.
///
/// They are separate because for some fields they genuinely differ — a queue is filtered by id and
/// reported by name — and collapsing them is not a simplification but a bug. One list served both
/// jobs before this, so whichever caller lost the argument got the wrong string: rules saved with an
/// id were compared against a name and silently never matched, on both providers, for every ticket.
/// </summary>
public record ExternalFieldOption(string Value, string Label, bool IsActive = true)
{
    /// <summary>What a synced ticket carries for this option. Defaults to <see cref="Value"/>, which
    /// is right wherever the provider reports a field the same way it accepts it.</summary>
    public string SyncValue { get; init; } = Value;
}

/// <summary>Definition of a provider custom field discovered at runtime.</summary>
public record ExternalFieldDefinition(
    string Key,
    string Label,
    string DataType,
    bool IsRequired,
    IReadOnlyList<ExternalFieldOption>? Options = null);

/// <summary>Normalized ticket returned from a provider read.</summary>
public record UnifiedTicket
{
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public string? QueueOrBoard { get; init; }
    public string? AssignedTechnicianExternalId { get; init; }
    public string? RequesterExternalId { get; init; }
    /// <summary>Display name of the owning company, when the provider sends it inline (CW does).</summary>
    public string? CompanyName { get; init; }
    public string? RequesterName { get; init; }
    public string? RequesterEmail { get; init; }
    public DateTimeOffset? SlaDueAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public IReadOnlyDictionary<string, string?> CustomFields { get; init; }
        = new Dictionary<string, string?>();
}

/// <summary>Request payload to create a ticket in a provider (portal → PSA).</summary>
public record UnifiedTicketCreateRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Category { get; init; }
    public string? QueueOrBoard { get; init; }
    /// <summary>Provider classification fields, supplied from the connection's defaults.
    /// Autotask: ticketType / issueType / subIssueType. ConnectWise: type / subType / item.</summary>
    public string? TicketType { get; init; }
    public string? IssueType { get; init; }
    public string? SubIssueType { get; init; }
    public required string ExternalCompanyId { get; init; }
    public string? RequesterExternalId { get; init; }
    public string? RequesterEmail { get; init; }
    /// <summary>Idempotency key so retried creates never duplicate a PSA ticket.</summary>
    public required string IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, string?> CustomFields { get; init; }
        = new Dictionary<string, string?>();
}

/// <summary>Partial update payload (only set fields are written).</summary>
public record UnifiedTicketUpdate
{
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Category { get; init; }
    public string? QueueOrBoard { get; init; }
    public string? AssignedTechnicianExternalId { get; init; }
    /// <summary>
    /// Role the technician takes the ticket in. Autotask refuses an assignment without one — the
    /// resource and its role are a pair there — so this is not decoration.
    /// </summary>
    public string? AssignedTechnicianRoleId { get; init; }
    public required string IdempotencyKey { get; init; }
}

public record UnifiedTicketNote(
    string? ExternalId,
    string AuthorName,
    string Body,
    bool IsPublic,
    DateTimeOffset CreatedAt,
    // True when the provider says a customer CONTACT wrote this note (CW: contact instead of
    // member; AT: createdByContactID). Without it every imported note renders as the MSP's own
    // words, and the thread loses its two sides.
    bool FromClient = false,
    // The provider's id for the STAFF member who wrote it - Autotask resource, ConnectWise member -
    // and null for a contact's or the system's note. The name above is only what that person is
    // called; the integration account is called after a real person, and only the id tells them apart.
    string? AuthorExternalId = null);

public record UnifiedTicketNoteCreateRequest(string Body, bool IsPublic, string IdempotencyKey)
{
    /// <summary>
    /// Email the ticket's own contact, where the provider lets the caller decide. ConnectWise does
    /// (emailContactFlag); Autotask does not expose recipients on a note at all and decides from
    /// its own workflow rules, so this is ignored there rather than quietly half-honoured.
    /// Check <see cref="ProviderCapabilities.SupportsNoteEmailRecipients"/> before offering it.
    /// </summary>
    public bool EmailContact { get; init; }

    /// <summary>
    /// Additional addresses to copy. Only ever addresses belonging to the ticket's own client
    /// company — the portal resolves them from that company's contacts, so one customer's reply can
    /// never be copied to another's.
    /// </summary>
    public IReadOnlyList<string> EmailCc { get; init; } = [];
}

public record UnifiedTimeEntry(
    string ExternalId,
    string TechnicianExternalId,
    decimal Hours,
    bool Billable,
    DateTimeOffset EntryDate,
    string? Notes)
{
    /// <summary>Display name of the technician, resolved by the connector. Null when unresolvable.</summary>
    public string? TechnicianName { get; init; }

    /// <summary>Work-type label as the provider names it (CW work type, Autotask billing code).</summary>
    public string? WorkType { get; init; }

    /// <summary>How the entry is charged — finer grained than <see cref="Billable"/>.</summary>
    public BillableOption BillableOption { get; init; } = BillableOption.Billable;

    /// <summary>
    /// The entry's INTERNAL notes, where the provider keeps them separately (Autotask shows these
    /// under "Internal Only"). A technician who writes "See internal notes" in the summary and the
    /// real detail here had that detail dropped entirely — the portal showed the pointer and not
    /// the thing it pointed at. Never client-visible.
    /// </summary>
    public string? InternalNotes { get; init; }
}

/// <summary>How a time entry is charged. Maps to ConnectWise billableOption / Autotask billing flags.</summary>
public enum BillableOption { Billable, DoNotBill, NoCharge }

/// <summary>
/// An agreement/contract the PSA holds for a client organization (ConnectWise agreement, Autotask
/// contract). Read-only in the portal — the PSA owns the commercial relationship; the portal only
/// shows the client what already governs their account. Type and Status carry the provider's own
/// labels, resolved by the connector, never a hardcoded translation table.
/// </summary>
/// <summary>
/// A holiday from the provider's own calendar (CW holiday list, Autotask holiday set). Date is the
/// ISO day (yyyy-MM-dd) — holidays are day-scoped, and carrying a timestamp would invite timezone
/// off-by-one-day bugs for nothing.
/// </summary>
public record ExternalHoliday(string Date, string Name);

/// <summary>
/// Whether this connection can actually log time, checked WITHOUT writing an entry. Providers
/// impose rules a settings form cannot express — Autotask needs a technician who is not the API
/// user and a role that technician genuinely holds — and the only alternative to checking here is
/// discovering it when a technician's logged hour is rejected.
/// </summary>
public record TimeEntryReadiness(bool Ready, string Summary)
{
    /// <summary>What to change, when it is not ready. Empty when it is.</summary>
    public IReadOnlyList<string> Remedies { get; init; } = [];

    /// <summary>Roles the configured technician actually holds, so the admin can pick a real one.</summary>
    public IReadOnlyList<string> AvailableRoles { get; init; } = [];
}

public record ExternalAgreement(
    string ExternalId,
    string Name,
    string? Type,
    string? Status,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate);

/// <summary>Request to log time against a provider ticket. WorkType/WorkRole/Member are optional in
/// Phase 1 (wired to discovery + mapping in a later phase); Hours + Billable are the core inputs.</summary>
public record UnifiedTimeEntryCreateRequest(
    decimal Hours,
    string? WorkType,
    string? WorkRole,
    BillableOption Billable,
    string? Notes,
    string? MemberIdentifier);

/// <summary>Partial update of an existing time entry — only non-null fields are applied.</summary>
public record UnifiedTimeEntryUpdate(
    decimal? Hours,
    BillableOption? Billable,
    string? Notes);

public record UnifiedAttachment(
    string ExternalId,
    string FileName,
    string ContentType,
    long SizeBytes)
{
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Who attached it provider-side. Empty means provider-generated, as with notes.</summary>
    public string? AuthorName { get; init; }

    /// <summary>Provider note this file hangs off, when the provider records one.</summary>
    public string? ExternalNoteId { get; init; }

    /// <summary>
    /// The provider's id for the staff member who attached it, as for a note: the name above is only
    /// what that person is called. Null for a contact's file, a system file, and wherever the provider
    /// does not report the id in the form its other person references use.
    /// </summary>
    public string? AuthorExternalId { get; init; }
}

/// <summary>
/// One technician's coverage: the role they hold, and the queue/board it applies to. A technician
/// commonly appears several times — one row per queue they work, sometimes in different roles — so
/// assignment can offer the people who actually cover the ticket's board rather than everyone.
/// </summary>
public record ExternalTechnicianAssignment(
    string TechnicianExternalId, string? RoleId, string? RoleName, string? QueueOrBoardId);

/// <summary>An attachment paired with the ticket it hangs off, as returned by a tenant-wide sweep.</summary>
public record ProviderAttachmentRef(string TicketExternalId, UnifiedAttachment Attachment);

/// <summary>An attachment's bytes pulled back from a provider, ready to stage in object storage.</summary>
public record DownloadedAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>An attachment already scanned + staged in object storage, ready to push to a PSA.</summary>
public record SecureAttachment(
    string FileName,
    string ContentType,
    long SizeBytes,
    string StorageObjectKey,
    byte[] Content)
{
    /// <summary>Provider note this file was posted with, so the PSA files it against the message
    /// rather than the ticket at large. Null for a standalone upload.</summary>
    public string? ExternalNoteId { get; init; }
}

/// <summary>Cursor/offset filter for paginated reads.</summary>
public record TicketFilter
{
    public DateTimeOffset? ModifiedSince { get; init; }
    public string? ExternalCompanyId { get; init; }
    public int PageSize { get; init; } = 100;
    public string? Cursor { get; init; }

    // Optional import filters. Connectors push down what the provider can express server-side; the
    // sync runner re-applies them client-side so behaviour is identical across providers.
    public IReadOnlyList<string> CompanyIds { get; init; } = [];
    public IReadOnlyList<string> QueueOrBoardIds { get; init; } = [];
    public IReadOnlyList<string> AssignedResourceIds { get; init; } = [];

    /// <summary>Include tickets the provider considers closed/completed. False = open/active only.</summary>
    public bool IncludeClosed { get; init; } = true;

    /// <summary>Only tickets active within this many days. Null = no age limit.</summary>
    public int? ActiveWithinDays { get; init; }
}

/// <summary>Provider-agnostic paginated result.</summary>
public record PaginatedResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
