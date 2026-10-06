using Desk.Domain.Common;
using Desk.Domain.Tenancy;

namespace Desk.Domain.Identity;

/// <summary>
/// A decision that a login in a PSA is nobody the portal needs to know as one of its people: an
/// API or service account, someone who left, a shared mailbox with a resource record.
///
/// A PSA technician is otherwise linked to a portal user or not, and "not" reads as something still
/// to do. Without a way to say "leave this one", the list of technicians to link never empties and
/// its count stops meaning anything. Ignoring changes nothing about the login's tickets or time:
/// they are shown under the PSA's own name, as they are for any unlinked login.
/// </summary>
public class PsaTechnicianIgnore : BaseEntity, ITenantScoped
{
    public Guid MspOrganizationId { get; set; }

    public Guid PsaConnectionId { get; set; }
    public PsaConnection? PsaConnection { get; set; }

    /// <summary>The provider's own identifier for the login. A login belongs to one connection.</summary>
    public required string ExternalTechnicianId { get; set; }

    /// <summary>
    /// The name the PSA gave it when it was ignored, so the decision can be read without asking the
    /// PSA. Who decided, and when, is in the audit log (psa.technician.ignored).
    /// </summary>
    public string? ExternalTechnicianName { get; set; }
}
