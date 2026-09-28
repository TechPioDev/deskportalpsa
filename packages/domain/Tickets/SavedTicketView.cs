using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// A filter set somebody uses often enough to give it a name — "Mohali overdue", "Night shift
/// handover", "Unassigned patching". The built-in views (open, mine, unassigned, overdue,
/// following, closed) are code, not rows: they mean the same thing for everyone and nobody should
/// be able to delete them. This table holds the ones a desk invents for itself.
///
/// The filters are stored as named columns rather than an opaque blob, for two reasons. A blob
/// cannot be validated when it is applied — whatever was written is trusted — and it cannot be
/// asked a question: with columns, "which saved views still point at the department we are
/// retiring" is a query rather than a text search.
///
/// Dates are stored as a WINDOW (<see cref="RaisedWithinDays"/>), never as an absolute from-date. A
/// view saved with "since 1 September" means "September" to the person who saved it and means
/// "everything" three months later; it would quietly stop being the view they named.
/// </summary>
public class SavedTicketView : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>Whose view it is. Only the owner may rename or delete it.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Offered to the whole team. Still owned — sharing a view does not hand it over.</summary>
    public bool Shared { get; set; }

    /// <summary>
    /// The board this view belongs to, when it was saved on one. Null means the all-tickets list.
    /// A view saved on a board is offered on that board only: its filters are about that queue.
    /// </summary>
    public Guid? BoardId { get; set; }

    // The filter set, in the same vocabulary the list page puts in its URL — so a saved view and a
    // link someone pasted into chat are the same thing, and neither can express something the other
    // cannot.
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    /// <summary>Company by NAME: the list filters on the name it displays, as its URLs always have.</summary>
    public string? Company { get; set; }
    public string? Queue { get; set; }
    public string? ConnectionName { get; set; }
    /// <summary>A person key ("u:{portal user}" or "x:{PSA resource}") — holds it, or logged time on it.</summary>
    public string? PersonKey { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TeamId { get; set; }

    /// <summary>"open" or "resolved" — each a SET of statuses, which is why it is not Status.</summary>
    public string? Openness { get; set; }

    public bool MineOnly { get; set; }
    public bool FollowingOnly { get; set; }
    public bool UnassignedOnly { get; set; }
    public bool OverdueOnly { get; set; }

    /// <summary>Raised within the last N days. Null means no date window at all.</summary>
    public int? RaisedWithinDays { get; set; }

    public int SortOrder { get; set; }
}
