namespace Desk.Application.Sync;

/// <summary>Tally of one inbound sync run.</summary>
/// <summary>Outcome of one inbound run. <paramref name="Notes"/> counts conversation entries
/// mirrored from the provider, and <paramref name="Attachments"/> the files pulled down with
/// them, so an admin can see import actually doing something.</summary>
/// <param name="MoreToRead">The run used its page budget and the read is not finished; the next run continues it.</param>
/// <param name="Failed">Records that could not be read or applied this run. They are kept and tried again.</param>
/// <param name="Recovered">Earlier failures that went through this time.</param>
public sealed record SyncRunResult(int Fetched, int Created, int Updated, int Skipped, int Pages, int Notes = 0, int Attachments = 0, int AttachmentsRemoved = 0, int NotesRemoved = 0,
    bool MoreToRead = false, int Failed = 0, int Recovered = 0);

/// <summary>Who is asking for a run, and for how much.</summary>
/// <param name="Full">Ignore the cursor and read every ticket again.</param>
/// <param name="Manual">A person asked, rather than the schedule.</param>
/// <param name="RequestedBy">That person, for the run's record.</param>
public sealed record SyncRunRequest(bool Full = false, bool Manual = false, string? RequestedBy = null);

/// <summary>
/// Runs a full inbound sync for one PSA connection: pages tickets from the provider connector,
/// maps and upserts each into the portal projection, and updates the connection's health and
/// sync cursor. Used by the manual "sync now" trigger and can back a scheduled poll.
/// </summary>
public interface IConnectionSyncRunner
{
    /// <param name="full">
    /// Ignore the incremental cursor and re-pull every ticket. Use after changing field mappings so
    /// existing tickets are re-translated with the new rules (an incremental run would skip them,
    /// since nothing changed on the provider side).
    /// </param>
    Task<SyncRunResult> RunAsync(Guid psaConnectionId, bool full = false, CancellationToken ct = default);

    /// <summary>
    /// The same run, saying who asked. Throws <see cref="Desk.Application.Common.ConflictException"/>
    /// when a run is already in progress for the connection: one connection is synced by one run at
    /// a time.
    /// </summary>
    Task<SyncRunResult> RunAsync(Guid psaConnectionId, SyncRunRequest request, CancellationToken ct = default);

    /// <summary>
    /// Re-reads the notes of tickets the portal already holds and heals them exactly as a sync does
    /// (author id, body, side, deletions), whatever the connection's import window says. Returns how
    /// many tickets were read.
    ///
    /// A sync - full or not - only reads the tickets its import window returns. The window decides
    /// which tickets are IMPORTED, but it also meant a ticket that went quiet never had its thread
    /// read again, so a correction the import learned later could never reach it.
    /// </summary>
    Task<int> RefreshNotesAsync(Guid psaConnectionId, IReadOnlyCollection<string> externalTicketIds, CancellationToken ct = default);

    /// <summary>
    /// One undated attachment sweep, for providers that can answer one: every file the provider holds
    /// for tickets the portal projects is read, and those already here are healed (author id). It is
    /// the sweep a full sync runs, so it also brings in any file that never arrived and removes files
    /// deleted in the PSA. A no-op for per-ticket providers and when attachment sync is off.
    /// </summary>
    Task<(int Added, int Removed)> RefreshAttachmentsAsync(Guid psaConnectionId, CancellationToken ct = default);
}
