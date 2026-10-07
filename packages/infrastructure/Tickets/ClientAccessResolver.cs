using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Maps an authenticated subject to its client user and the ONE company this request is about, or
/// null for callers who are not clients.
///
/// Every client endpoint asks here, on every request, and is then held to the company it is given.
/// That makes this the one place a second company can come from, and the rule is kept short:
///
/// - No company named: the person's own.
/// - A company named: their own, or one there is a grant for (<c>client_company_access</c>),
///   read now. A grant taken away a second ago is gone for this request.
/// - Anything else is refused in the same words, whether the company does not exist, belongs to
///   another organization, or is simply not theirs: the answer does not say which.
/// - A company given to be looked at refuses every request that would change something.
///
/// Nothing here is inferred. A person's e-mail address, its domain and the names of the companies
/// are never consulted.
/// </summary>
public sealed class ClientAccessResolver(DeskDbContext db, IActingCompany? acting = null) : IClientAccessResolver
{
    public const string NotYours = "You do not have access to that company.";
    public const string ViewOnly = "You can view this company's tickets but not change them. Ask your service desk if you need to raise or reply to tickets for it.";

    public async Task<ClientAccess?> ResolveAsync(string idpSubject, CancellationToken ct = default)
    {
        var user = await db.ClientUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.IdpSubject == idpSubject && u.IsActive, ct);
        if (user is null) return null;

        // A company that was named and cannot be read is not "no company named": answering with the
        // person's own would show them one company while they believe they are looking at another.
        if (acting?.Malformed == true) throw new ForbiddenException(NotYours);

        var wanted = acting?.RequestedCompanyId;
        if (wanted is null || wanted == user.ClientCompanyId)
            return new ClientAccess(user.MspOrganizationId, user.ClientCompanyId, user.Id, user.IsCompanyAdministrator);

        // The grant, for this person and this company, in this person's organization, to a company
        // that is itself in that organization. Each is named: the answer must not rest on the
        // tenant filter alone.
        var grant = await db.ClientCompanyAccess.AsNoTracking()
            .Where(g => g.ClientUserId == user.Id && g.ClientCompanyId == wanted && g.MspOrganizationId == user.MspOrganizationId
                && db.ClientCompanies.Any(c => c.Id == g.ClientCompanyId && c.MspOrganizationId == user.MspOrganizationId))
            .Select(g => new { g.SeesAllTickets, g.CanCreate })
            .FirstOrDefaultAsync(ct);
        if (grant is null) throw new ForbiddenException(NotYours);
        if (!grant.CanCreate && acting!.IsWrite) throw new ForbiddenException(ViewOnly);

        return new ClientAccess(user.MspOrganizationId, wanted.Value, user.Id, IsCompanyAdministrator: false)
        {
            IsGranted = true, GrantSeesAllTickets = grant.SeesAllTickets, CanWrite = grant.CanCreate,
        };
    }
}
