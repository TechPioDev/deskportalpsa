namespace Desk.Application.Boards;

// The desk's working tools: SLA plans that set when a board ticket is owed, canned responses for
// the reply box, and the task list inside a ticket.

public sealed record SlaPlanDto(
    Guid Id, string Name, int ResolveWithinHours, int? FirstResponseWithinHours,
    bool BusinessHoursOnly, int WorkdayStartHour, int WorkdayEndHour, int WorkingDays,
    bool IsActive, int SortOrder,
    /// <summary>How many boards and topics use it, so retiring one is not a surprise.</summary>
    int UsedBy);

public sealed record SlaPlanInput(
    string Name, int ResolveWithinHours, int? FirstResponseWithinHours = null,
    bool BusinessHoursOnly = false, int WorkdayStartHour = 9, int WorkdayEndHour = 18,
    int WorkingDays = 0b0111110, int SortOrder = 0);

/// <summary>
/// SLA plans. Leads and administrators manage them; a plan is applied when a board ticket is
/// raised and never re-dates a ticket afterwards.
/// </summary>
public interface ISlaPlanService
{
    Task<IReadOnlyList<SlaPlanDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<SlaPlanDto> SaveAsync(Guid? id, SlaPlanInput input, CancellationToken ct = default);
    Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
}

public sealed record CannedResponseDto(
    Guid Id, string Name, string Body, Guid? BoardId, string? BoardName, bool IsActive, int SortOrder);

public sealed record CannedResponseInput(string Name, string Body, Guid? BoardId = null, int SortOrder = 0);

/// <summary>
/// Replies the desk keeps. Leads manage them; anybody who can reply can insert one.
/// </summary>
public interface ICannedResponseService
{
    /// <param name="forTicketId">
    /// The ticket a reply is being written on: returns the responses for everywhere plus those for
    /// that ticket's own board. Null returns every response, for the page that manages them.
    /// </param>
    Task<IReadOnlyList<CannedResponseDto>> ListAsync(Guid? forTicketId = null, bool includeInactive = false, CancellationToken ct = default);
    Task<CannedResponseDto> SaveAsync(Guid? id, CannedResponseInput input, CancellationToken ct = default);
    Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
}

public sealed record TicketTaskDto(
    Guid Id, string Title, bool IsDone, DateTimeOffset? DoneAt, string? DoneByName,
    Guid? AssignedAppUserId, string? AssignedName, int SortOrder, DateTimeOffset CreatedAt);

/// <summary>
/// The steps inside a ticket. Staff only, portal only, never pushed; a ticket with open tasks
/// cannot be closed from the portal.
/// </summary>
public interface ITicketTaskService
{
    Task<IReadOnlyList<TicketTaskDto>> ListAsync(Guid ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<TicketTaskDto>> AddAsync(Guid ticketId, string title, Guid? assignedAppUserId, CancellationToken ct = default);
    Task<IReadOnlyList<TicketTaskDto>> UpdateAsync(Guid taskId, string title, Guid? assignedAppUserId, CancellationToken ct = default);
    Task<IReadOnlyList<TicketTaskDto>> SetDoneAsync(Guid taskId, bool done, CancellationToken ct = default);
    Task<IReadOnlyList<TicketTaskDto>> MoveAsync(Guid taskId, int offset, CancellationToken ct = default);
    Task<IReadOnlyList<TicketTaskDto>> DeleteAsync(Guid taskId, CancellationToken ct = default);
}
