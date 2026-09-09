using Desk.Domain.Common;

namespace Desk.Domain.Analytics;

/// <summary>
/// One day's activity, pre-aggregated.
///
/// Dashboards read this; the raw event log stays for drill-down. A busy desk writes tens of
/// thousands of events a week, and a dashboard that scans them gets slower every month until
/// someone quietly stops opening it.
///
/// The grain is (day, source, actor, client). Source is a DIMENSION rather than two measures
/// because PSA-versus-portal is the comparative question the whole layer exists to answer, and
/// folding it into columns would make every future split a schema change.
/// </summary>
public class ActivityDailyFact : BaseEntity, ITenantScoped
{
    public Guid MspOrganizationId { get; set; }

    /// <summary>The UTC day the activity HAPPENED on, never the day it was rolled up.</summary>
    public DateOnly Day { get; set; }

    public ActivitySource Source { get; set; }

    /// <summary>
    /// The PSA's identifier for whoever acted. Portal events resolve to it through the user's PSA
    /// identity; someone unmapped stays null rather than being attributed to a guess.
    /// </summary>
    public string? ActorExternalId { get; set; }

    /// <summary>
    /// The PORTAL user who acted, where one is known. Added beside the PSA id rather than instead
    /// of it: a desk runs both kinds of technician, and the two namespaces cannot be collapsed into
    /// one column without guessing which a value belongs to.
    ///
    /// Before this, a portal event by someone with no PSA identity resolved to a NULL actor - so
    /// every such technician's work landed in one anonymous bucket, indistinguishable from each
    /// other and from work nobody could attribute at all.
    /// </summary>
    public Guid? ActorAppUserId { get; set; }

    public Guid? ClientCompanyId { get; set; }

    public int EventCount { get; set; }

    /// <summary>Summed only over events that genuinely carry a duration. Most do not.</summary>
    public int DurationSeconds { get; set; }
}
