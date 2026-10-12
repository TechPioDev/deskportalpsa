namespace Desk.Application.Admin;

/// <summary>One run of a connection's inbound sync, as an administrator reads it.</summary>
public sealed record SyncRunDto(
    Guid Id, string Trigger, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Fetched, int Created, int Updated, int Skipped, int Pages, int Notes, int Attachments,
    int FailedRecords, int Retried, int Recovered, string? Error, string? Notice, string? RequestedBy);

/// <summary>A record the sync could not read or apply, and where it stands.</summary>
public sealed record SyncFailureDto(
    Guid Id, string Entity, string ExternalId, string Operation, string Category, string Message,
    int Attempts, DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt, DateTimeOffset? NextAttemptAt, string Status);

/// <summary>Where a connection's sync stands: its cursor, its recent runs, and what it still owes.</summary>
/// <param name="Watermark">Everything changed in the PSA before this has been read.</param>
/// <param name="ReadInProgress">A read ran out of pages and is being continued run by run.</param>
/// <param name="PagesReadSoFar">Pages of that read done so far.</param>
/// <param name="Running">A run is in progress right now.</param>
public sealed record SyncStateDto(
    Guid ConnectionId, DateTimeOffset? Watermark, bool ReadInProgress, int PagesReadSoFar, bool Running,
    int OpenFailures, int NeedsReview, IReadOnlyList<SyncRunDto> Runs,
    // A sync someone asked for that has not finished: waiting for the worker, or being run by it.
    DateTimeOffset? RequestedAt = null, bool RequestedFull = false);

/// <summary>
/// What a connection's sync has done and what it could not do. Read-only apart from the two things
/// a person may decide about a failed record: try it again now, or stop trying.
/// </summary>
public interface ISyncHealthService
{
    Task<SyncStateDto> StateAsync(Guid connectionId, int runs = 20, CancellationToken ct = default);
    Task<IReadOnlyList<SyncFailureDto>> FailuresAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Puts a failed record at the front of the next run. Audited.</summary>
    Task RetryAsync(Guid connectionId, Guid failureId, CancellationToken ct = default);

    /// <summary>Stops trying a failed record. It is kept, marked dismissed. Audited.</summary>
    Task DismissAsync(Guid connectionId, Guid failureId, CancellationToken ct = default);
}
