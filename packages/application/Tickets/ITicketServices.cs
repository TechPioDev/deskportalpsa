namespace Desk.Application.Tickets;

/// <summary>Resolves the client identity (company + user) for an authenticated subject, if any.</summary>
public interface IClientAccessResolver
{
    Task<ClientAccess?> ResolveAsync(string idpSubject, CancellationToken ct = default);
}

/// <summary>
/// Read side of the client portal. Every method is constrained to the caller's company (and, for
/// non-admins, their own tickets). Detail returns only the public conversation.
/// </summary>
public interface ITicketReadService
{
    Task<IReadOnlyList<TicketListItem>> ListAsync(ClientAccess access, CancellationToken ct = default);
    Task<TicketDetailDto?> GetDetailAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default);

    /// <summary>Every ticket in the tenant, for staff holding TicketsViewAll.</summary>
    Task<IReadOnlyList<TicketListItem>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Any ticket in the tenant, for staff holding TicketsViewAll.</summary>
    Task<TicketDetailDto?> GetDetailForStaffAsync(Guid ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<NotificationDto>> RecentActivityAsync(ClientAccess access, int take = 10, CancellationToken ct = default);

    /// <summary>Recent ticket activity for staff, over the tickets their TicketsViewAll scope reaches.</summary>
    Task<IReadOnlyList<NotificationDto>> RecentActivityForStaffAsync(int take = 10, CancellationToken ct = default);

    /// <summary>
    /// Notification history for the client: a merged, dated feed of what actually happened on
    /// their visible tickets — created, publicly replied to, resolved. Derived from real records,
    /// company-scoped (own tickets only for non-admins), and internal notes never appear.
    /// </summary>
    Task<IReadOnlyList<ActivityEventDto>> ActivityHistoryAsync(ClientAccess access, int take = 50, CancellationToken ct = default);
}

/// <summary>
/// Write side. Creating a ticket or adding a comment goes to the PSA (system of record) first, then
/// the portal projection is updated and a portal-origin sync event is recorded so the inbound sync
/// recognises and skips its own echo.
/// </summary>
public interface ITicketCommandService
{
    Task<CreateTicketResultDto> CreateAsync(ClientAccess access, CreateTicketInput input, CancellationToken ct = default);
    Task<TicketNoteDto> AddCommentAsync(ClientAccess access, Guid ticketId, string body, CancellationToken ct = default);

    /// <summary>A technician reply on a ticket within the caller's effective TicketsAddPublicNote
    /// scope, attributed by display name.</summary>
    Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic = true, CancellationToken ct = default);

    /// <summary>As above, additionally asking the PSA to email the ticket's contact and copy
    /// <paramref name="emailCc"/>. Addresses are validated against the ticket's own company.</summary>
    Task<TicketNoteDto> AddStaffCommentAsync(
        Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic,
        bool emailContact, IReadOnlyList<string> emailCc, CancellationToken ct = default);

    /// <summary>Who a public reply on this ticket can go to, and whether the provider lets the
    /// caller choose. Same resolution the write validates against.</summary>
    Task<ReplyRecipientsDto> ListReplyRecipientsAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default);

    /// <summary>
    /// Reads the ticket's contact live from the PSA and stores it; returns whether a public reply
    /// would now reach someone. For tickets the scheduled sync has not revisited since contacts
    /// began to be captured - it only re-reads recently active tickets.
    /// </summary>
    Task<bool> RefreshContactAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default);
}
