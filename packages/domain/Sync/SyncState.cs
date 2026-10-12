using Desk.Domain.Common;

namespace Desk.Domain.Sync;

/// <summary>How far one kind of data has been read from one connection.</summary>
/// <remarks>
/// The whole of a connection's sync state used to be one timestamp on the connection row, set to
/// the moment a run ENDED. A ticket read on the first page and changed again while the last page
/// was being read was therefore behind the new cursor, and never asked for again. The watermark
/// here is the moment the run STARTED, less an overlap, so anything changed during a run is read
/// by the next one. Reading a ticket twice costs nothing - an unchanged ticket is a no-op.
/// </remarks>
public class SyncCursor : TenantEntity
{
    public const string Tickets = "tickets";

    public Guid PsaConnectionId { get; set; }

    /// <summary>What this cursor is for, e.g. <see cref="Tickets"/>. One row per connection and entity.</summary>
    public required string Entity { get; set; }

    /// <summary>Everything changed before this has been read and applied. Null until a first read completes.</summary>
    public DateTimeOffset? Watermark { get; set; }

    // ---- a read that ran out of pages -----------------------------------------------------------
    //
    // A run reads a bounded number of pages. When there is more, it used to stop, log a warning and
    // move the cursor to "now" anyway: the rest was never requested again, and a full re-sync read
    // the same first pages. Instead the place it stopped is kept here and the next run carries on
    // from it, with the same filter. The watermark moves only when the whole read has finished, and
    // then to when its FIRST run started.

    /// <summary>The provider's own page cursor to resume from. Null when no read is in progress.</summary>
    public string? Continuation { get; set; }

    /// <summary>The "changed since" the interrupted read was asking for. Null means everything.</summary>
    public DateTimeOffset? ContinuationSince { get; set; }

    /// <summary>When the first run of the interrupted read started.</summary>
    public DateTimeOffset? ContinuationStartedAt { get; set; }

    /// <summary>Whether the interrupted read was a full one (attachments are reconciled only then).</summary>
    public bool ContinuationFull { get; set; }

    /// <summary>Pages read so far across the runs of the interrupted read.</summary>
    public int ContinuationPages { get; set; }
}

public enum SyncRunStatus
{
    /// <summary>In progress. At most one per connection: this row is the lock.</summary>
    Running = 0,
    Succeeded = 1,
    /// <summary>Finished its page budget with more to read; the next run continues.</summary>
    Partial = 2,
    Failed = 3,
    /// <summary>Its lease ran out without a finish - the process stopped mid-run - and another run took over.</summary>
    Abandoned = 4,
}

public enum SyncRunTrigger
{
    Scheduled = 0,
    Manual = 1,
    ManualFull = 2,
}

/// <summary>
/// One run of a connection's inbound sync: when, how long, how much, and how it ended. While it is
/// <see cref="SyncRunStatus.Running"/> it is also the connection's lock - the scheduled sync and a
/// manual "Sync now" used to be able to run over the same connection at once.
/// </summary>
public class SyncRun : TenantEntity
{
    public Guid PsaConnectionId { get; set; }
    public SyncRunTrigger Trigger { get; set; }
    public SyncRunStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Until when this run holds the connection. Pushed forward as it works; a run whose lease has
    /// lapsed is taken to have died with its process, and the next one may take over.
    /// </summary>
    public DateTimeOffset LeaseExpiresAt { get; set; }

    public int Fetched { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Pages { get; set; }
    public int Notes { get; set; }
    public int NotesRemoved { get; set; }
    public int Attachments { get; set; }
    public int AttachmentsRemoved { get; set; }

    /// <summary>Records that could not be read or applied this run and are held in <see cref="SyncFailure"/>.</summary>
    public int FailedRecords { get; set; }

    /// <summary>Earlier failures tried again this run, and how many of those now went through.</summary>
    public int Retried { get; set; }
    public int Recovered { get; set; }

    /// <summary>Why it failed, in words an administrator can act on. Never a credential or a payload.</summary>
    public string? Error { get; set; }

    /// <summary>Something worth knowing about a run that did not fail: it stopped early, or stopped asking for one kind of data.</summary>
    public string? Notice { get; set; }

    /// <summary>The person who asked for a manual run; null for the scheduler.</summary>
    public string? RequestedBy { get; set; }
}

public enum SyncFailureStatus
{
    /// <summary>Will be tried again by a later run, after <see cref="SyncFailure.NextAttemptAt"/>.</summary>
    Pending = 0,
    /// <summary>A later attempt went through.</summary>
    Resolved = 1,
    /// <summary>Tried as often as it will be. Stays until a person retries or dismisses it.</summary>
    NeedsReview = 2,
    Dismissed = 3,
}

/// <summary>
/// One record that could not be read from the PSA or applied here. A failure used to end in one of
/// two ways: a ticket that could not be saved stopped the whole run, every run; and a rate limit
/// while reading a ticket's notes was swallowed, the run reported healthy, and the notes were not
/// read again until the ticket next changed. Kept here instead, a failure costs one record rather
/// than the run, is retried with a growing delay, and is visible until it is resolved.
/// </summary>
public class SyncFailure : TenantEntity
{
    public const string Ticket = "ticket";

    public static class Operations
    {
        /// <summary>The ticket itself could not be saved.</summary>
        public const string Apply = "apply";
        public const string Notes = "notes";
        public const string Time = "time";
        public const string Attachments = "attachments";
    }

    public Guid PsaConnectionId { get; set; }

    /// <summary>The kind of record, e.g. <see cref="Ticket"/>.</summary>
    public required string Entity { get; set; }

    /// <summary>The PSA's id for the record.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Which part of handling it failed - one of <see cref="Operations"/>.</summary>
    public required string Operation { get; set; }

    /// <summary>The failure's kind: a connector failure kind (RateLimited, Timeout...) or "Apply".</summary>
    public required string Category { get; set; }

    /// <summary>What went wrong, shortened. Never a credential, a header or a payload.</summary>
    public required string Message { get; set; }

    public int Attempts { get; set; }
    public DateTimeOffset FirstFailedAt { get; set; }
    public DateTimeOffset LastFailedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    public SyncFailureStatus Status { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
