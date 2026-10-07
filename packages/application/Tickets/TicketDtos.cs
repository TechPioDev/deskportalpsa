using Desk.Domain.Enums;

namespace Desk.Application.Tickets;

/// <summary>The client identity a request acts on behalf of, resolved from the authenticated subject.</summary>
/// <summary>
/// Who a client request is from and which ONE company it is about. Every client read and write is
/// held to <see cref="ClientCompanyId"/>; nothing a client is answered is gathered across companies.
///
/// It is the person's own company unless the request names another, and then only a company they
/// have been explicitly given (see <c>ClientCompanyAccess</c>). In a company they were given they
/// are never its administrator: <see cref="IsCompanyAdministrator"/> is the control panel's flag,
/// and a grant does not carry it.
/// </summary>
public sealed record ClientAccess(Guid MspOrganizationId, Guid ClientCompanyId, Guid ClientUserId, bool IsCompanyAdministrator)
{
    /// <summary>True when this is a company the person was given, and not their own.</summary>
    public bool IsGranted { get; init; }

    /// <summary>What a grant says about seeing every ticket. Null in the person's own company, where the administrator flag decides.</summary>
    public bool? GrantSeesAllTickets { get; init; }

    /// <summary>Every ticket of the company, or only the ones this person raised.</summary>
    public bool SeesAllTickets => GrantSeesAllTickets ?? IsCompanyAdministrator;

    /// <summary>May raise tickets and reply. False for a company given to be looked at only.</summary>
    public bool CanWrite { get; init; } = true;
}

/// <summary>
/// The company a client request asks to act in, when it names one, and whether the request would
/// change anything. Read from the request by the API; the resolver decides whether it is allowed.
/// A request carries no authority of its own: naming a company gives nothing that a grant has not.
/// </summary>
public interface IActingCompany
{
    /// <summary>The company named, or null when none was (the person's own is meant).</summary>
    Guid? RequestedCompanyId { get; }

    /// <summary>A company was named and could not be read as one. Refused, never treated as "none".</summary>
    bool Malformed { get; }

    /// <summary>The request would change something (anything but a read).</summary>
    bool IsWrite { get; }
}

public sealed record TicketListItem(
    Guid Id,
    string? ExternalTicketId,
    /// <summary>Null for a ticket that belongs to no PSA: the team's own board, or an RMM alert.</summary>
    ProviderType? Provider,
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
    IReadOnlyList<TicketPersonRef>? People = null,
    // The board this ticket sits on, when it is not a provider queue, and the number people quote
    // for it (INT-000123). Null on a PSA ticket, which is quoted by the provider's own id.
    Guid? BoardId = null,
    string? Number = null,
    // Who holds it, by name, so a board reads as a list of people's work rather than of ids.
    string? AssignedToName = null,
    // The columns a desk organised by department reads its queue with: who owns it, when it is due,
    // how much conversation it has had, and when it last moved.
    string? DepartmentName = null,
    string? Topic = null,
    string? Source = null,
    DateTimeOffset? DueAt = null,
    int ReplyCount = 0,
    DateTimeOffset? LastActivityAt = null,
    // The team it sits with, when it is a team's job before it is a person's. Shown beside the
    // assignee rather than instead of it: "Level 2 · Basit" is a different fact from either half.
    // Both id and name: the name is what a row displays, the id is what "a team I am in" compares
    // against — two departments can each have a "Level 2", so the name is not an identity.
    Guid? AssignedTeamId = null,
    string? AssignedTeamName = null,
    // Whether the CALLER follows this ticket, so a list can be narrowed to what they are watching
    // without a second round trip per row. False on the client list, which has no followers.
    bool Following = false,
    // The ticket's task list as a count, so a row can say "3/5" without anyone opening it.
    int TaskCount = 0,
    int TasksDone = 0,
    // The SLA's reply promise, and whether it has been kept.
    DateTimeOffset? FirstResponseDueAt = null,
    DateTimeOffset? FirstRespondedAt = null,
    // Set while the SLA clock is stopped — waiting on the customer, or on hold.
    DateTimeOffset? SlaPausedAt = null,
    // Where it came from: a PSA, the team's own board, or a monitoring alert. Drives the source badge.
    Desk.Domain.Enums.TicketOrigin Origin = Desk.Domain.Enums.TicketOrigin.Psa);

/// <summary>
/// A named, validated filter set. Every field is optional and an absent one means "do not narrow by
/// this" — so the empty query is the whole visible list, which is what the list page asks for first.
/// </summary>
/// <param name="Q">
/// Free text. Matched against the ticket number, the provider's reference, the title, the customer
/// name, the requester, and — when <paramref name="IncludeNotes"/> is set — the conversation itself.
/// </param>
/// <param name="Openness">"open" or "resolved": each a SET of statuses, which is why it is not Status.</param>
/// <param name="MineOnly">Tickets the caller holds, or that sit with a team the caller is in.</param>
/// <param name="RaisedWithinDays">A window, never an absolute date — a saved view outlives the date it was saved on.</param>
public sealed record TicketQuery(
    string? Q = null,
    Guid? BoardId = null,
    Guid? DepartmentId = null,
    Guid? TeamId = null,
    Guid? ClientCompanyId = null,
    string? Status = null,
    string? Priority = null,
    string? Openness = null,
    bool MineOnly = false,
    bool FollowingOnly = false,
    bool UnassignedOnly = false,
    bool OverdueOnly = false,
    int? RaisedWithinDays = null,
    bool IncludeNotes = false,
    bool DueSoonOnly = false,
    int Take = 50,
    // The list page's own filters, by the names it shows: company, queue, source connection.
    string? CompanyName = null,
    string? QueueName = null,
    string? ConnectionName = null,
    /// <summary>A PersonKey: tickets that person holds or logged time on.</summary>
    string? PersonKey = null,
    /// <summary>Raised on or after this moment - the date a figure elsewhere was counted from.</summary>
    DateTimeOffset? RaisedSince = null,
    /// <summary>"psa", "internal" or "monitoring".</summary>
    string? Kind = null,
    /// <summary>For paging: how many matching tickets to pass over first.</summary>
    int Skip = 0,
    /// <summary>Resolved work waiting for a board lead's review.</summary>
    bool ReviewPending = false);

/// <summary>One page of the ticket list, and what the whole filtered set adds up to.</summary>
public sealed record TicketPage(
    IReadOnlyList<TicketListItem> Items, int Total, int Skip, int Take, decimal HoursWorked, decimal HoursBillable);

/// <summary>
/// What the list's filters can be set to, taken from every ticket the caller can see - not just the
/// page on screen, which would offer only the companies that happen to be on page one.
/// </summary>
public sealed record TicketFacets(
    IReadOnlyList<string> Statuses, IReadOnlyList<string> Priorities, IReadOnlyList<string> Companies,
    IReadOnlyList<string> Queues, IReadOnlyList<string> Sources, IReadOnlyList<TicketPersonRef> People);

/// <summary>A count with a label, for a breakdown.</summary>
public sealed record LabelCount(string Label, int Count);

/// <summary>
/// The open work, counted: for the Overview across everything the caller can see, for My Work across
/// what they hold. Counted in the database, never by loading the tickets.
/// </summary>
public sealed record TicketSummary(
    int Open, int Overdue, int DueToday, int DueSoon, int Waiting, int HighPriority, int Unassigned,
    int ResolvedLast7Days, IReadOnlyList<LabelCount> OpenByPriority, IReadOnlyList<LabelCount> OpenBySource,
    decimal? HoursLoggedThisWeek = null);

/// <summary>
/// The tickets raised in a window (or ever), counted: how many, how many still open, and how they split
/// by priority and queue. For the Overview and Analytics, which used to load every ticket to count.
/// </summary>
public sealed record TicketBreakdown(int Total, int Open, IReadOnlyList<LabelCount> ByPriority, IReadOnlyList<LabelCount> ByQueue);

/// <summary>One person's share of the open work, for a lead balancing the team.</summary>
public sealed record WorkloadRow(
    string Key, string Name, int Open, int Overdue, int HighPriority, int Stale, DateTimeOffset? OldestRaisedAt);

/// <summary>The team's open work by person, and what nobody holds yet.</summary>
public sealed record TeamWorkload(IReadOnlyList<WorkloadRow> People, int Unassigned, int UnassignedOverdue, int Stale, int StaleDays, int AwaitingReview = 0);

/// <summary>
/// What a search found, and honestly whether that was all of it: a result set silently cut at the
/// limit reads as "there are no more", and somebody then concludes the ticket does not exist.
/// </summary>
public sealed record TicketSearchResult(
    IReadOnlyList<TicketListItem> Items,
    int Total,
    bool Truncated);

/// <summary>A filter set somebody named. Built-in views are not rows and never appear here.</summary>
public sealed record SavedViewDto(
    Guid Id,
    string Name,
    bool Shared,
    bool IsMine,
    string? OwnerName,
    Guid? BoardId,
    SavedViewFilters Filters,
    int SortOrder);

/// <summary>The filters a saved view carries, in the vocabulary the list page puts in its URL.</summary>
public sealed record SavedViewFilters(
    string? Search = null,
    string? Status = null,
    string? Priority = null,
    string? Company = null,
    string? Queue = null,
    string? ConnectionName = null,
    string? PersonKey = null,
    Guid? DepartmentId = null,
    Guid? TeamId = null,
    string? Openness = null,
    bool MineOnly = false,
    bool FollowingOnly = false,
    bool UnassignedOnly = false,
    bool OverdueOnly = false,
    int? RaisedWithinDays = null,
    bool DueSoonOnly = false);

/// <summary>Somebody watching a ticket they do not hold.</summary>
public sealed record TicketFollowerDto(Guid AppUserId, string Name, string? Email, bool IsMe, DateTimeOffset AddedAt);

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
    /// <summary>Null for a ticket that belongs to no PSA: the team's own board, or an RMM alert.</summary>
    ProviderType? Provider,
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
    string? AssignedAppUserName = null,
    // Who the ticket is for, and whether a public reply would reach anyone: a PSA contact with an
    // address, or the client portal user who raised it. The address itself is not sent.
    string? ContactName = null,
    bool HasReachableContact = false,
    /// <summary>The number a board's ticket is quoted by (INT-000123). Null for a PSA ticket.</summary>
    string? Number = null,
    /// <summary>The team it is routed to, before or alongside a person taking it.</summary>
    Guid? AssignedTeamId = null,
    string? AssignedTeamName = null,
    /// <summary>
    /// Who is watching it without holding it. Empty on the client detail: these are colleagues'
    /// names, and a client has no business enumerating the desk.
    /// </summary>
    IReadOnlyList<TicketFollowerDto>? Followers = null,
    /// <summary>The SLA plan the due dates came from, by name, and the reply promise it made.</summary>
    string? SlaPlanName = null,
    DateTimeOffset? SlaDueAt = null,
    DateTimeOffset? FirstResponseDueAt = null,
    DateTimeOffset? FirstRespondedAt = null,
    DateTimeOffset? SlaPausedAt = null,
    /// <summary>The client's rating, for staff. The client reads their own through its own endpoint.</summary>
    TicketRatingDto? Rating = null,
    /// <summary>The device the ticket is about, when one is known.</summary>
    TicketDeviceDto? Device = null,
    /// <summary>
    /// The team's own record of a board ticket: its details as the edit form shows them, what fixed
    /// it, how often it came back, and whether its board asks for a resolution. Staff only.
    /// </summary>
    TicketBoardDetailsDto? BoardDetails = null,
    /// <summary>Who resolved it in the portal - the person productivity credits it to. Staff only.</summary>
    string? ResolvedByName = null,
    /// <summary>What the PSA files the ticket under, in its own three levels. Staff only, and null when it has none.</summary>
    TicketClassificationDto? Classification = null,
    /// <summary>
    /// The PSA's custom fields an administrator chose to bring in, with a value on this ticket.
    /// For staff, every one of them. For a client, only the ones separately marked as theirs to
    /// see; the rest are not sent at all. Null where there are none.
    /// </summary>
    IReadOnlyList<TicketCustomFieldDto>? CustomFields = null);

/// <param name="Label">What the field is called in the portal.</param>
/// <param name="DataType">text, number, date, boolean or list.</param>
/// <param name="ClientVisible">Whether the client sees it too. Always true in what a client is sent.</param>
public sealed record TicketCustomFieldDto(string Label, string Value, string DataType, bool ClientVisible);

/// <summary>
/// The PSA's own classification of a ticket, as it sent it. Which words the three levels go by is
/// the PSA's: Autotask's ticket type, issue type and sub-issue type; ConnectWise's type, subtype
/// and item.
/// </summary>
/// <param name="WorkType">What kind of work it is in the portal's words, where one of the connection's rules says. Staff only.</param>
/// <param name="Subcategory">The portal's subcategory, on the same terms.</param>
/// <param name="Mapped">
/// Whether one of the connection's rules names what the ticket is filed under. False is UNMAPPED:
/// the connection has rules, none names this, and the portal has put nothing in a rule's place.
/// Null where the connection has no rules at all: the desk has not taken them up, and a ticket is
/// not called unmapped against rules that do not exist.
/// </param>
public sealed record TicketClassificationDto(
    string? TicketType, string? IssueType, string? SubIssueType,
    string? WorkType = null, string? Subcategory = null, bool? Mapped = null);

public sealed record TicketBoardDetailsDto(
    Guid BoardId, Guid? BoardTopicId, Guid? DepartmentId, Guid? ClientCompanyId,
    string? Resolution, int ReopenCount, DateTimeOffset? LastReopenedAt, bool RequireResolution,
    /// <summary>Whether this ticket's board or topic reviews resolved work, and where the review stands.</summary>
    bool RequireReview = false, Desk.Domain.Tickets.TicketReviewState ReviewState = Desk.Domain.Tickets.TicketReviewState.None,
    string? ReviewedBy = null, DateTimeOffset? ReviewedAt = null, int ReviewSendBacks = 0);

/// <summary>A ticket's device. The serial and warranty reach staff only: the client's own device list
/// is for their administrators, and a ticket page is read by whoever raised it.</summary>
public sealed record TicketDeviceDto(Guid Id, string Name, string? Type, string? Identifier, bool IsActive, DateTimeOffset? WarrantyExpiresAt);

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
    string? QueueOrBoard,
    /// <summary>The device the ticket is about - one of the company's devices the PSA knows.</summary>
    Guid? DeviceId = null);

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
