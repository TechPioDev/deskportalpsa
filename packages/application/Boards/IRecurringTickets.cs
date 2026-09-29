using Desk.Domain.Tickets;

namespace Desk.Application.Boards;

public sealed record RecurringTicketDto(
    Guid Id, Guid BoardId, string BoardName, string Title, string? Description,
    Guid? BoardTopicId, string? TopicName, string? Priority, Guid? DepartmentId,
    Guid? AssignedAppUserId, string? AssignedName, Guid? ClientCompanyId, string? Checklist,
    RecurrenceFrequency Frequency, int DayOfWeek, int DayOfMonth, int Hour, bool SkipIfOpen, bool IsActive,
    DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt, Guid? LastTicketId, string? LastTicketNumber,
    string? LastOutcome,
    /// <summary>The schedule in words: "Every Monday at 09:00".</summary>
    string Schedule,
    string? CreatedByName);

public sealed record RecurringTicketInput(
    Guid BoardId, string Title, string? Description = null,
    Guid? BoardTopicId = null, string? Priority = null, Guid? DepartmentId = null,
    Guid? AssignedAppUserId = null, Guid? ClientCompanyId = null, string? Checklist = null,
    RecurrenceFrequency Frequency = RecurrenceFrequency.Weekly, int DayOfWeek = 1, int DayOfMonth = 1,
    int Hour = 9, bool SkipIfOpen = true);

/// <summary>What one run did, in words, and the ticket it raised when it raised one.</summary>
public sealed record RecurringRunResult(string Outcome, Guid? TicketId, string? Number);

/// <summary>
/// Work that comes round on a schedule. Leads manage it; the worker raises the tickets, in the name
/// of whoever set the schedule up.
/// </summary>
public interface IRecurringTicketService
{
    Task<IReadOnlyList<RecurringTicketDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<RecurringTicketDto> SaveAsync(Guid? id, RecurringTicketInput input, Guid appUserId, CancellationToken ct = default);
    Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Raises the ticket now, as a check that the schedule does what was meant. The schedule itself is untouched.</summary>
    Task<RecurringRunResult> RunNowAsync(Guid id, CancellationToken ct = default);

    /// <summary>Raises this one if it is due, and moves it to its next time. For the runner, inside a tenant scope.</summary>
    Task<RecurringRunResult?> RunIfDueAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Finds every schedule that has fallen due, in every organization, and runs each in its own scope.</summary>
public interface IRecurringTicketRunner
{
    Task<int> RunDueAsync(CancellationToken ct = default);
}
