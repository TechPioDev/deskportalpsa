using Desk.Domain.Common;

namespace Desk.Domain.Tenancy;

/// <summary>
/// A client user's access to a company that is not their own.
///
/// A client user belongs to one company and sees that company's tickets. Some people answer for
/// more than one: a finance lead of a group, an IT manager of three sister firms. They are given
/// each further company one at a time, by somebody at the desk who is allowed to, and by nothing
/// else. No address, no domain of an e-mail and no resemblance between two company names ever
/// creates one of these: a row here is the whole of the reason a person can see into a second
/// company, and taking it away is the whole of undoing it.
///
/// What a grant gives is the least it can and is said in so many words. By default the person sees
/// in that company only tickets they themselves raised, and can change nothing. Seeing every ticket
/// of the company, and raising tickets and replying in it, are each said separately. Running the
/// company's own control panel (its users, its settings) is not something a grant can give.
/// </summary>
public class ClientCompanyAccess : TenantEntity
{
    public Guid ClientUserId { get; set; }
    public ClientUser? ClientUser { get; set; }

    /// <summary>The further company. Never the user's own: that one needs no grant.</summary>
    public Guid ClientCompanyId { get; set; }
    public ClientCompany? ClientCompany { get; set; }

    /// <summary>Every ticket of the company, and not only the ones this person raised.</summary>
    public bool SeesAllTickets { get; set; }

    /// <summary>May raise tickets in the company and reply on the ones they can see. Off, the company is read-only to them.</summary>
    public bool CanCreate { get; set; }
}
