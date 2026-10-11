namespace Desk.Application.Sync;

/// <summary>A sync someone asked for, waiting for the worker or being run by it.</summary>
public sealed record SyncRequestDto(Guid ConnectionId, DateTimeOffset RequestedAt, bool Full);

/// <summary>What became of a requested sync when the worker came to it.</summary>
public enum RequestedSyncOutcome
{
    /// <summary>Nobody is asking any more: it was withdrawn, or another worker ran it.</summary>
    NothingAsked,
    /// <summary>Another run has the connection. The request waits for it.</summary>
    Busy,
    /// <summary>Run, and the read it started is complete.</summary>
    Done,
    /// <summary>Run, with more to read. The request stands, and the next turn carries on from where this stopped.</summary>
    More,
    /// <summary>The run failed and said why on the connection. The request is spent: asking again is a person's to do.</summary>
    Failed,
}

/// <summary>
/// Asking for a sync, as opposed to running one. "Sync now" used to run the sync inside the web
/// request that asked for it. A first import of a large PSA takes minutes, and a request that long
/// is cut off by whatever stands in front of the API: the person saw an error and the sync, which
/// had not stopped, went on unseen. Asking is now a note on the connection that the worker reads
/// within seconds.
/// </summary>
public interface ISyncRequestService
{
    /// <summary>
    /// Asks for a sync of the connection. Asking again before it has finished changes nothing,
    /// except that asking for everything after asking for what changed is asking for everything.
    /// </summary>
    Task<SyncRequestDto> RequestAsync(Guid connectionId, bool full, string? requestedBy, CancellationToken ct = default);
}
