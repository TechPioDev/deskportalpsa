using Desk.Domain.Enums;

namespace Desk.Application.Boards;

/// <param name="Key">The prefix in front of every ticket number on the board, e.g. INT.</param>
/// <param name="MemberCount">0 means the board is open to the whole team, which is the usual case.</param>
public sealed record BoardDto(
    Guid Id, string Name, string Key, string? Description, BoardKind Kind,
    bool ClientVisible, bool IsActive, int SortOrder, int MemberCount, int OpenTickets);

public sealed record BoardInput(
    string Name, string Key, string? Description, BoardKind Kind = BoardKind.Internal,
    bool ClientVisible = false, int SortOrder = 0);

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
}

/// <param name="ClientCompanyId">
/// The customer this work concerns, when it concerns one. It never shows the ticket to that customer.
/// </param>
/// <param name="AssignedAppUserId">Who should pick it up. Anybody on the team may name anybody.</param>
public sealed record InternalTicketInput(
    Guid BoardId, string Title, string? Description, string? Priority = null, string? Category = null,
    Guid? ClientCompanyId = null, Guid? AssignedAppUserId = null, DateTimeOffset? DueAt = null);

public sealed record InternalTicketCreatedDto(Guid TicketId, string Number, string Title, Guid BoardId);

/// <summary>Raising a ticket on a board that belongs to no PSA. Never pushed anywhere.</summary>
public interface IInternalTicketService
{
    Task<InternalTicketCreatedDto> CreateAsync(Guid appUserId, InternalTicketInput input, CancellationToken ct = default);
}
