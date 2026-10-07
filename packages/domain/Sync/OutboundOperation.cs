using Desk.Domain.Common;

namespace Desk.Domain.Sync;

/// <summary>What a change waiting to reach a PSA is.</summary>
public enum OutboundKind
{
    /// <summary>A reply or an internal note written in the portal.</summary>
    Note = 1,

    /// <summary>A status somebody asked for. The ticket keeps the status it has until the PSA accepts the new one.</summary>
    StatusChange = 2,

    /// <summary>An hour logged in the portal.</summary>
    TimeEntry = 3,

    /// <summary>A ticket raised in the portal.</summary>
    TicketCreate = 4,
}

/// <summary>
/// Where a change stands. The three the owner named, and no fourth: nothing is called synced
/// before the PSA has confirmed it.
/// </summary>
public enum OutboundState
{
    /// <summary>Pending Sync: waiting to be sent, or to be tried again.</summary>
    Pending = 0,

    /// <summary>Synced: the PSA has it, and said so.</summary>
    Synced = 1,

    /// <summary>Sync Failed: the PSA refused it, or every try ran out. Kept, for somebody to retry or let go of.</summary>
    Failed = 2,
}

/// <summary>
/// A change made in the portal that could not be given to the PSA at the moment it was made,
/// kept until it can be.
///
/// Before this, a PSA that did not answer meant the change was lost: a reply somebody had typed
/// came back as an error and was nowhere. Now the change is kept here and tried again, with a
/// longer wait each time, until the PSA takes it or the tries run out.
///
/// What is kept of the change on the portal's own side follows who owns what. A note is the
/// author's own words and is stored at once, marked as not yet sent. A status is the PSA's to
/// hold: asked for while the PSA is away, it is kept HERE as something asked for and the ticket
/// goes on showing the status the PSA last gave it, until the PSA accepts the new one.
///
/// An operation is only ever one of three things, and is synced only when the PSA said so.
/// A failed one is not deleted: it is somebody's work, to be retried or deliberately let go of.
/// </summary>
public class OutboundOperation : TenantEntity
{
    public Guid PsaConnectionId { get; set; }
    public Guid TicketId { get; set; }

    public OutboundKind Kind { get; set; }
    public OutboundState State { get; set; } = OutboundState.Pending;

    /// <summary>What the change is, in a few words a person can read on the ticket: "Reply", "Status to RESOLVED".</summary>
    public required string Summary { get; set; }

    /// <summary>What is needed to send it, for its kind.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>The note or time entry this is about, where there is one.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>Sent with every try, so a PSA that honours such keys takes a retry as the same change.</summary>
    public required string IdempotencyKey { get; set; }

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 8;

    /// <summary>When to try next. Null on a new operation (now) and on one that is not pending.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>What went wrong last, in words fit for the person who made the change.</summary>
    public string? LastError { get; set; }

    /// <summary>Whether the last failure left it unknown if the PSA took the change: before it is sent again, the PSA is asked.</summary>
    public bool Uncertain { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>The PSA's own id for what it created, once it has.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Who made the change: a member of staff, or a client user. For the record and for credit; never for access.</summary>
    public Guid? RequestedByAppUserId { get; set; }
    public Guid? RequestedByClientUserId { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>Until when the worker that took this holds it. Past that with it still pending, the worker stopped.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Raised on every change and checked on save: of two workers taking one operation, one is refused.</summary>
    public int Version { get; set; }
}
