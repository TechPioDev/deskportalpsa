using Desk.Domain.Common;
using Desk.Domain.Tenancy;

namespace Desk.Domain.Mapping;

/// <summary>
/// What an administrator has decided about one of a PSA's custom fields, on one connection.
///
/// A PSA account has fields of its own that no other has: an asset tag, a cost centre, a site code.
/// The portal knows none of them by name and reads none of them until somebody says to. A row here
/// is that decision for one field of one connection: bring it in or leave it, what to call it in
/// the portal, and whether the client may see it.
///
/// A field with no row is ignored, like one whose row says so: nothing of it is stored on a ticket.
/// A field that is brought in is for staff only until it is separately said to be the client's to
/// see as well. What tenants put in such fields is theirs to know; the portal does not guess that a
/// field called "Notes for the engineer" is fit for a customer.
///
/// Every imported field is read-only here. The portal writes no custom field to a PSA.
/// </summary>
public class PsaCustomField : BaseEntity, ITenantScoped
{
    public Guid MspOrganizationId { get; set; }

    public Guid PsaConnectionId { get; set; }
    public PsaConnection? PsaConnection { get; set; }

    /// <summary>How the PSA knows the field, and how a ticket carries its value: Autotask's field name, ConnectWise's field id.</summary>
    public required string ExternalKey { get; set; }

    /// <summary>What the PSA's own screen called it when this was last saved, kept so the decision can be read while the PSA cannot be asked.</summary>
    public required string ExternalLabel { get; set; }

    /// <summary>One of <c>CustomFieldTypes</c>: text, number, date, boolean or list.</summary>
    public string DataType { get; set; } = "text";

    /// <summary>Brought in. False is "leave it": its values are not stored and are shown to nobody.</summary>
    public bool Import { get; set; }

    /// <summary>What it is called in the portal. The PSA's own label unless somebody chose another.</summary>
    public required string PortalLabel { get; set; }

    /// <summary>The client may see it on their own tickets. False, the default, is staff only.</summary>
    public bool ClientVisible { get; set; }
}
