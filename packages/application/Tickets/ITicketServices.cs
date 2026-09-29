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
    Task<IReadOnlyList<TicketListItem>> ListAllAsync(CancellationToken ct = default, Guid? boardId = null);

    /// <summary>
    /// The same list, narrowed by a query. Runs in the database rather than over an already-loaded
    /// page, which is the only way free text can reach the conversation as well as the title: the
    /// answer to "which ticket was that disk on" is usually in a note, not a subject line.
    /// </summary>
    /// <param name="access">
    /// A client identity to answer as, or null for the staff scope. The same method serves both so
    /// that one visibility rule decides what a search can find — a second implementation is how a
    /// search ends up returning what the list would not.
    /// </param>
    Task<TicketSearchResult> SearchAsync(TicketQuery query, ClientAccess? access = null, CancellationToken ct = default);

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

    /// <summary>
    /// A note that records something the portal did on the ticket - an approval asked for or
    /// answered - as a public note, so it reaches the PSA and the client's thread like any reply.
    /// The caller has already decided the person may do the thing being recorded; this does not scope.
    /// </summary>
    Task PostTrailNoteAsync(Guid ticketId, string authorName, bool authoredByClient, string body, CancellationToken ct = default);

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


/// <summary>
/// Who is watching a ticket. Distinct from who holds it: a follower is never accountable for the
/// work, which is exactly why adding one is not a change of assignment.
/// </summary>
public interface ITicketFollowerService
{
    Task<IReadOnlyList<TicketFollowerDto>> ListAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Adds a follower. Following an already-followed ticket is a no-op, not an error.</summary>
    Task<IReadOnlyList<TicketFollowerDto>> AddAsync(Guid ticketId, Guid appUserId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketFollowerDto>> RemoveAsync(Guid ticketId, Guid appUserId, CancellationToken ct = default);

    /// <summary>Every ticket the caller follows, for the view that shows exactly those.</summary>
    Task<IReadOnlyList<Guid>> FollowedTicketIdsAsync(CancellationToken ct = default);
}

/// <summary>
/// Saved filter sets. The built-in views are code, so this covers only the ones a desk invents: a
/// caller sees their own and anything a colleague shared, and may change only their own.
/// </summary>
public interface ITicketViewService
{
    Task<IReadOnlyList<SavedViewDto>> ListAsync(Guid? boardId = null, CancellationToken ct = default);
    Task<SavedViewDto> SaveAsync(Guid? id, string name, bool shared, Guid? boardId, SavedViewFilters filters, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
