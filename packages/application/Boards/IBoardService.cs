using Desk.Domain.Enums;

namespace Desk.Application.Boards;

/// <param name="Key">The prefix in front of every ticket number on the board, e.g. INT.</param>
/// <param name="MemberCount">0 means the board is open to the whole team, which is the usual case.</param>
public sealed record BoardDto(
    Guid Id, string Name, string Key, string? Description, BoardKind Kind,
    bool ClientVisible, bool IsActive, int SortOrder, int MemberCount, int OpenTickets,
    Guid? DefaultSlaPlanId = null, string? DefaultSlaPlanName = null, bool RequireResolution = false,
    bool RequireReview = false);

public sealed record BoardInput(
    string Name, string Key, string? Description, BoardKind Kind = BoardKind.Internal,
    bool ClientVisible = false, int SortOrder = 0, Guid? DefaultSlaPlanId = null,
    bool RequireResolution = false, bool RequireReview = false);

public sealed record BoardMemberDto(Guid AppUserId, string DisplayName, string Email);

/// <summary>
/// The team's own boards: what exists, and who may see each one. Creating and configuring a board is
/// a lead's decision (boards.manage); raising a ticket on one, or handing it to a colleague, is not.
/// </summary>
public interface IBoardService
{
    /// <summary>Boards this member of staff may work on, with the count of what is open on each.</summary>
    Task<IReadOnlyList<BoardDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default);

    Task<BoardDto> CreateAsync(BoardInput input, CancellationToken ct = default);
    Task<BoardDto> UpdateAsync(Guid boardId, BoardInput input, CancellationToken ct = default);

    /// <summary>Deactivates a board. Its tickets are kept and stay readable; nothing new is raised on it.</summary>
    Task SetActiveAsync(Guid boardId, bool active, CancellationToken ct = default);

    Task<IReadOnlyList<BoardMemberDto>> MembersAsync(Guid boardId, CancellationToken ct = default);

    /// <summary>Replaces the membership. An empty list reopens the board to the whole team.</summary>
    Task<IReadOnlyList<BoardMemberDto>> SetMembersAsync(Guid boardId, IReadOnlyList<Guid> appUserIds, CancellationToken ct = default);

    /// <summary>What tickets on this board can be about, and what each kind fills in.</summary>
    Task<IReadOnlyList<BoardTopicDto>> TopicsAsync(Guid boardId, bool includeInactive = false, CancellationToken ct = default);

    Task<BoardTopicDto> AddTopicAsync(Guid boardId, BoardTopicInput input, CancellationToken ct = default);
    Task<BoardTopicDto> UpdateTopicAsync(Guid topicId, BoardTopicInput input, CancellationToken ct = default);

    /// <summary>Retires a topic. Tickets raised under it keep it; nothing new can be.</summary>
    Task SetTopicActiveAsync(Guid topicId, bool active, CancellationToken ct = default);
}

/// <param name="ClientCompanyId">
/// The customer this work concerns, when it concerns one. It never shows the ticket to that customer.
/// </param>
/// <param name="AssignedAppUserId">Who should pick it up. Anybody on the team may name anybody.</param>
public sealed record InternalTicketInput(
    Guid BoardId, string Title, string? Description, string? Priority = null, string? Category = null,
    Guid? ClientCompanyId = null, Guid? AssignedAppUserId = null, DateTimeOffset? DueAt = null,
    /// <summary>What it is about. Fills in department, priority, assignee and due date unless the caller said otherwise.</summary>
    Guid? BoardTopicId = null,
    /// <summary>Which department owns it. Overrides whatever the topic would have chosen.</summary>
    Guid? DepartmentId = null,
    /// <summary>How the work reached us: Phone, Email, Chat, Walk-in, Meeting, Monitoring, Other.</summary>
    string? Source = null);

/// <param name="MemberCount">0 means the board is open to the whole team.</param>
public sealed record BoardTopicDto(
    Guid Id, Guid BoardId, string Name, Guid? DefaultDepartmentId, string? DefaultDepartmentName,
    string? DefaultPriority, Guid? DefaultAssigneeUserId, string? DefaultAssigneeName,
    int? DueInHours, bool IsActive, int SortOrder, Guid? SlaPlanId = null, string? SlaPlanName = null,
    bool RequireReview = false);

public sealed record BoardTopicInput(
    string Name, Guid? DefaultDepartmentId = null, string? DefaultPriority = null,
    Guid? DefaultAssigneeUserId = null, int? DueInHours = null, int SortOrder = 0, Guid? SlaPlanId = null,
    bool RequireReview = false);

public sealed record InternalTicketCreatedDto(Guid TicketId, string Number, string Title, Guid BoardId);

/// <summary>Raising a ticket on a board that belongs to no PSA. Never pushed anywhere.</summary>
/// <summary>
/// The editable details of a ticket on the team's own board, sent whole: what the form shows is what
/// is saved. Status, assignment and time have their own paths and are not here.
/// </summary>
public sealed record InternalTicketEdit(
    string Title, string? Description, string Priority, DateTimeOffset? DueAt = null,
    Guid? BoardTopicId = null, string? Category = null, Guid? DepartmentId = null, Guid? ClientCompanyId = null);

public interface IInternalTicketService
{
    Task<InternalTicketCreatedDto> CreateAsync(Guid appUserId, InternalTicketInput input, CancellationToken ct = default);

    /// <summary>
    /// Changes a board ticket's details. The caller has already found the ticket through the
    /// person's own scope; a PSA ticket is refused, because its details belong to the provider.
    /// </summary>
    Task EditAsync(Desk.Domain.Tickets.Ticket ticket, InternalTicketEdit input, CancellationToken ct = default);
}
