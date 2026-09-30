using Desk.Domain.Common;

namespace Desk.Domain.Analytics;

/// <summary>Where an activity was observed. The whole PSA-versus-portal question is this one field.</summary>
public enum ActivitySource
{
    /// <summary>Someone did it in this portal, and we watched them do it.</summary>
    Portal = 0,
    /// <summary>The PSA reported it during a sync. We infer it happened; we did not see it.</summary>
    Psa = 1,
    /// <summary>A monitoring tool reported it. Neither a PSA nor a person: an alert opened the work.</summary>
    Monitoring = 2,
}

/// <summary>
/// What happened. Deliberately a closed set: every member is something a metric can count, and a
/// kind nobody aggregates is a row nobody reads.
/// </summary>
public enum ActivityKind
{
    NoteAdded = 0,
    TimeLogged = 1,
    StatusChanged = 2,
    TicketCreated = 3,
    /// <summary>Observed on sync: the PSA now names an assignee it did not name before.</summary>
    TicketAssigned = 4,
    /// <summary>Observed on sync: the PSA now reports a closure date it did not report before.</summary>
    TicketClosed = 5,
    /// <summary>Observed on sync: the PSA now reports a resolution date it did not report before.</summary>
    TicketResolved = 6,
    /// <summary>
    /// Reserved, not yet emitted. Views are by far the highest-volume event a portal produces, and
    /// nothing consumes them yet — recording them now would buy storage cost and no answers.
    /// </summary>
    TicketViewed = 7,
}

/// <summary>
/// One thing that happened, appended and never changed.
///
/// This is the portal's own record of activity, and it is NOT the audit log. Audit answers "who
/// changed what, for compliance": written rarely, read almost never, retained because it must be.
/// Activity is the opposite — written constantly and read on every dashboard load. Putting
/// analytics traffic into the audit table would make the compliance record expensive to query and
/// tempting to prune, and a prunable audit log is not an audit log.
///
/// It is also not <c>SyncEvent</c>, which exists to recognise the portal's own writes coming back
/// and is a de-duplication mechanism with a short life.
/// </summary>
public class ActivityEvent : BaseEntity, ITenantScoped
{
    public Guid MspOrganizationId { get; set; }

    /// <summary>
    /// When it HAPPENED, not when this row was written. Technicians log Friday's work on Monday,
    /// and aggregating by row-creation would make every Monday look heroic.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    public ActivitySource Source { get; set; }
    public ActivityKind Kind { get; set; }

    /// <summary>The portal user who acted, when the portal saw them act. Null for PSA-observed events.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// The PSA's own identifier for whoever acted. Carried alongside <see cref="ActorUserId"/>
    /// rather than instead of it, because the two answer different questions: a PSA-side event has
    /// only this, and a portal-side event by an unmapped user has only the other.
    /// </summary>
    public string? ActorExternalId { get; set; }

    public Guid? PsaConnectionId { get; set; }
    public Guid? TicketId { get; set; }

    /// <summary>
    /// Denormalized so client analytics never needs a join back to tickets — and so the event
    /// survives its ticket being reconciled away.
    /// </summary>
    public Guid? ClientCompanyId { get; set; }

    /// <summary>Only for events that genuinely have a duration. Null is the common case.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Kind-specific detail for a human reading the activity feed. Metrics MUST NOT aggregate on
    /// this: anything a dashboard counts gets its own typed column, because a metric that depends
    /// on JSON shape breaks silently the day the shape changes.
    /// </summary>
    public string? Detail { get; set; }
}
