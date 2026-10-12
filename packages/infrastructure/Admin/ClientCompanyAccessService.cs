using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Which companies a client user can see into besides their own: what the desk grants, and what
/// the client is told they have.
///
/// Reads and writes here are the organization's own by the tenant filter, and the organization is
/// named again on every row that is written or matched. A user or a company of another
/// organization is not found.
/// </summary>
public sealed class ClientCompanyAccessService(DeskDbContext db, IAuditWriter audit) : IClientCompanyAccessService
{
    public async Task<IReadOnlyList<MyCompanyDto>> MineAsync(string idpSubject, CancellationToken ct = default)
    {
        var user = await db.ClientUsers.AsNoTracking()
            .Where(u => u.IdpSubject == idpSubject && u.IsActive)
            .Select(u => new { u.Id, u.MspOrganizationId, u.ClientCompanyId, u.IsCompanyAdministrator, Company = u.ClientCompany!.Name })
            .FirstOrDefaultAsync(ct);
        if (user is null) return [];

        var given = await db.ClientCompanyAccess.AsNoTracking()
            .Where(g => g.ClientUserId == user.Id && g.MspOrganizationId == user.MspOrganizationId
                && g.ClientCompany!.MspOrganizationId == user.MspOrganizationId)
            .OrderBy(g => g.ClientCompany!.Name)
            .Select(g => new MyCompanyDto(g.ClientCompanyId, g.ClientCompany!.Name, false, g.SeesAllTickets, g.CanCreate))
            .ToListAsync(ct);
        return [new MyCompanyDto(user.ClientCompanyId, user.Company, true, user.IsCompanyAdministrator, true), .. given];
    }

    public async Task<IReadOnlyList<ClientUserAccessDto>> ListUsersAsync(string? search, int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var users = db.ClientUsers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLower();
            users = users.Where(u => u.DisplayName.ToLower().Contains(needle) || u.Email.ToLower().Contains(needle)
                || u.ClientCompany!.Name.ToLower().Contains(needle));
        }
        var ids = await users.OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Select(u => u.Id).Take(take).ToListAsync(ct);
        return await DtosAsync(ids, ct);
    }

    public async Task<ClientUserAccessDto> GetUserAsync(Guid clientUserId, CancellationToken ct = default)
        => (await DtosAsync([clientUserId], ct)).FirstOrDefault() ?? throw new NotFoundException("Client user");

    public async Task<IReadOnlyList<ClientCompanyChoiceDto>> CompaniesAsync(CancellationToken ct = default)
        => await db.ClientCompanies.AsNoTracking()
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new ClientCompanyChoiceDto(c.Id, c.Name))
            .ToListAsync(ct);

    private async Task<List<ClientUserAccessDto>> DtosAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var users = await db.ClientUsers.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsActive, SignedIn = u.IdpSubject != null, u.ClientCompanyId, Company = u.ClientCompany!.Name })
            .ToListAsync(ct);
        var grants = (await db.ClientCompanyAccess.AsNoTracking()
                .Where(g => ids.Contains(g.ClientUserId))
                .Select(g => new { g.ClientUserId, g.ClientCompanyId, Company = g.ClientCompany!.Name, g.SeesAllTickets, g.CanCreate, g.CreatedAt })
                .ToListAsync(ct))
            .ToLookup(g => g.ClientUserId);
        return users
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id)
            .Select(u => new ClientUserAccessDto(u.Id, u.DisplayName, u.Email, u.IsActive, u.SignedIn, u.ClientCompanyId, u.Company,
                grants[u.Id].OrderBy(g => g.Company, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new CompanyGrantDto(g.ClientCompanyId, g.Company, g.SeesAllTickets, g.CanCreate, g.CreatedAt)).ToList()))
            .ToList();
    }

    public async Task<ClientUserAccessDto> GrantAsync(Guid clientUserId, Guid clientCompanyId, SetCompanyGrantInput input, CancellationToken ct = default)
    {
        var user = await db.ClientUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == clientUserId, ct)
            ?? throw new NotFoundException("Client user");
        // The company, in the user's own organization. One of another organization is not found,
        // whatever the caller's scope would otherwise let them read.
        var company = await db.ClientCompanies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == clientCompanyId && c.MspOrganizationId == user.MspOrganizationId, ct)
            ?? throw new NotFoundException("Client company");
        if (company.Id == user.ClientCompanyId)
            throw new ValidationFailedException($"{company.Name} is {user.DisplayName}'s own company. It needs no grant: what they can do there is set in that company's own users.");

        var grant = await db.ClientCompanyAccess.FirstOrDefaultAsync(g => g.ClientUserId == user.Id && g.ClientCompanyId == company.Id, ct);
        if (grant is null)
        {
            db.ClientCompanyAccess.Add(new ClientCompanyAccess
            {
                MspOrganizationId = user.MspOrganizationId, ClientUserId = user.Id, ClientCompanyId = company.Id,
                SeesAllTickets = input.SeesAllTickets, CanCreate = input.CanCreate,
            });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("client.company_access.granted", nameof(ClientUser), user.Id.ToString(),
                new { user = user.Email, company = company.Name, companyId = company.Id, input.SeesAllTickets, input.CanCreate }, ct);
        }
        else if (grant.SeesAllTickets != input.SeesAllTickets || grant.CanCreate != input.CanCreate)
        {
            var before = new { grant.SeesAllTickets, grant.CanCreate };
            (grant.SeesAllTickets, grant.CanCreate) = (input.SeesAllTickets, input.CanCreate);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("client.company_access.changed", nameof(ClientUser), user.Id.ToString(),
                new { user = user.Email, company = company.Name, companyId = company.Id, before, after = new { input.SeesAllTickets, input.CanCreate } }, ct);
        }
        return await GetUserAsync(user.Id, ct);
    }

    public async Task<ClientUserAccessDto> RevokeAsync(Guid clientUserId, Guid clientCompanyId, CancellationToken ct = default)
    {
        var user = await db.ClientUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == clientUserId, ct)
            ?? throw new NotFoundException("Client user");
        var grant = await db.ClientCompanyAccess.Include(g => g.ClientCompany)
            .FirstOrDefaultAsync(g => g.ClientUserId == user.Id && g.ClientCompanyId == clientCompanyId, ct);
        if (grant is not null)
        {
            var was = new { user = user.Email, company = grant.ClientCompany?.Name, companyId = clientCompanyId, grant.SeesAllTickets, grant.CanCreate };
            db.ClientCompanyAccess.Remove(grant);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("client.company_access.revoked", nameof(ClientUser), user.Id.ToString(), was, ct);
        }
        return await GetUserAsync(user.Id, ct);
    }
}
