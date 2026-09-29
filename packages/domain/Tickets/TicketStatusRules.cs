using System.Linq.Expressions;

namespace Desk.Domain.Tickets;

/// <summary>
/// Which statuses count as finished. Deliberately not an enum comparison: a ticket synced before its
/// statuses were mapped carries the RAW provider value ("New (not responded)", "Closed - No charge"),
/// so the classification has to tolerate both the portal-neutral values and whatever a PSA calls it.
///
/// The same two markers are applied in apps/web/src/lib/status.ts, and both must stay in step — a
/// list that calls a ticket open while the filter behind it calls it closed is a list that lies. As
/// expressions, so the database applies them rather than the answer being decided in memory after
/// the rows have already been read.
/// </summary>
public static class TicketStatusRules
{
    public const string ResolvedMarker = "RESOLV";

    /// <summary>
    /// "Due soon" wherever a list or view says it: due within this many hours. The board list's
    /// "soon" label has always meant eight hours; the view and the filter mean the same.
    /// </summary>
    public const int DueSoonHours = 8;

    /// <summary>The needs-attention list's tighter warning: a ticket that will breach within this many hours.</summary>
    public const int AtRiskHours = 2;
    public const string ClosedMarker = "CLOSED";

    /// <summary>Finished: resolved or closed, however the provider spells it.</summary>
    public static Expression<Func<Ticket, bool>> Resolved() =>
        t => t.PortalStatus.ToUpper().Contains(ResolvedMarker) || t.PortalStatus.ToUpper().Contains(ClosedMarker);

    /// <summary>The same rule for a status already in hand, rather than in a query.</summary>
    public static bool Finished(string status)
    {
        var s = status.ToUpperInvariant();
        return s.Contains(ResolvedMarker) || s.Contains(ClosedMarker);
    }

    /// <summary>Still work to do. Written out rather than negated at the call site, so the two can
    /// never disagree about a status neither of them anticipated.</summary>
    public static Expression<Func<Ticket, bool>> Open() =>
        t => !t.PortalStatus.ToUpper().Contains(ResolvedMarker) && !t.PortalStatus.ToUpper().Contains(ClosedMarker);
}
