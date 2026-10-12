using Desk.Domain.Common;
using Desk.Domain.Tenancy;

namespace Desk.Domain.Mapping;

/// <summary>
/// What one of a PSA's classifications means in the portal's own words.
///
/// A PSA files a ticket under up to three levels (Autotask's ticket type, issue type and sub-issue
/// type; ConnectWise's type, subtype and item). A rule names some of those levels and says what a
/// ticket filed there is in the portal: its category, its work type, its subcategory, or any of them.
///
/// Rules belong to one connection. Two connections to the same kind of PSA are two companies' ways
/// of filing work, and "Hardware" in one is not "Hardware" in the other.
///
/// Nothing is worked out from the PSA's words. A classification no rule names is unmapped and stays
/// so until someone writes a rule; there is no rule for "everything else", so an unknown value
/// cannot be given a meaning by accident. The PSA's own words are kept on the ticket either way.
/// </summary>
public class ClassificationMapping : BaseEntity, ITenantScoped
{
    public Guid MspOrganizationId { get; set; }

    public Guid PsaConnectionId { get; set; }
    public PsaConnection? PsaConnection { get; set; }

    /// <summary>
    /// The levels a ticket must be filed under for this rule to speak for it, as the PSA words
    /// them. Empty means "whatever it is": a rule naming only the first level speaks for every
    /// ticket under it. At least one is named.
    /// </summary>
    public string TicketType { get; set; } = "";
    public string IssueType { get; set; } = "";
    public string SubIssueType { get; set; } = "";

    /// <summary>What such a ticket is in the portal. Null leaves that one to a broader rule, or unsaid. At least one is given.</summary>
    public string? Category { get; set; }
    public string? WorkType { get; set; }
    public string? Subcategory { get; set; }
}
