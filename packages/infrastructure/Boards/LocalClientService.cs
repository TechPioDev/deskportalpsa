using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// Clients added by hand: in no PSA, kept by the desk so that its own tickets can say who the
/// work was for. See <see cref="ILocalClientService"/> for what such a client is and is not.
/// </summary>
public sealed class LocalClientService(DeskDbContext db, ITenantContext tenant, IAuditWriter audit) : ILocalClientService
{
    public const int MaxNameLength = 200;
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<ClientChoiceDto>> ChoicesAsync(CancellationToken ct = default)
        => await db.ClientCompanies.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new ClientChoiceDto(c.Id, c.Name, c.PsaConnectionId == null))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LocalClientDto>> ListAsync(CancellationToken ct = default)
        => await db.ClientCompanies.AsNoTracking()
            .Where(c => c.PsaConnectionId == null)
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new LocalClientDto(c.Id, c.Name, c.IsActive, db.Tickets.Count(t => t.ClientCompanyId == c.Id), c.CreatedAt))
            .ToListAsync(ct);

    public async Task<LocalClientDto> AddAsync(LocalClientInput input, CancellationToken ct = default)
    {
        var name = Clean(input.Name);
        await RefuseIfTakenAsync(name, except: null, ct);
        var company = new ClientCompany
        {
            MspOrganizationId = Org,
            PsaConnectionId = null,
            Name = name,
            ExternalCompanyId = ClientCompany.LocalExternalId(),
            IsActive = true,
        };
        db.ClientCompanies.Add(company);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("client.local.added", nameof(ClientCompany), company.Id.ToString(), new { name }, ct);
        return new LocalClientDto(company.Id, company.Name, true, 0, company.CreatedAt);
    }

    public async Task<LocalClientDto> UpdateAsync(Guid id, LocalClientInput input, CancellationToken ct = default)
    {
        // Only one added by hand. A PSA's client is named by the PSA, and renaming it here would
        // be undone at the next sync or, worse, not be.
        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == id && c.PsaConnectionId == null, ct)
            ?? throw new NotFoundException("Client");
        var name = Clean(input.Name);
        var renamed = !string.Equals(company.Name, name, StringComparison.Ordinal);
        var (was, wasActive) = (company.Name, company.IsActive);
        if (renamed) await RefuseIfTakenAsync(name, except: company.Id, ct);

        (company.Name, company.IsActive) = (name, input.IsActive);
        await db.SaveChangesAsync(ct);

        if (renamed) await audit.WriteAsync("client.local.renamed", nameof(ClientCompany), company.Id.ToString(), new { from = was, to = name }, ct);
        if (wasActive != input.IsActive)
            await audit.WriteAsync(input.IsActive ? "client.local.restored" : "client.local.retired", nameof(ClientCompany), company.Id.ToString(), new { name }, ct);

        var tickets = await db.Tickets.CountAsync(t => t.ClientCompanyId == company.Id, ct);
        return new LocalClientDto(company.Id, company.Name, company.IsActive, tickets, company.CreatedAt);
    }

    private static string Clean(string? name)
    {
        var cleaned = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length < 2) throw new ValidationFailedException("Give the client a name of at least two characters.");
        if (cleaned.Length > MaxNameLength) throw new ValidationFailedException($"A client's name can be up to {MaxNameLength} characters.");
        return cleaned;
    }

    /// <summary>
    /// One name, one client: two entries called the same would split a client's work between them
    /// in every report. Compared without regard to case, against the PSA's clients as well.
    /// </summary>
    private async Task RefuseIfTakenAsync(string name, Guid? except, CancellationToken ct)
    {
        var lowered = name.ToLower();
        var taken = await db.ClientCompanies.AsNoTracking()
            .Where(c => c.Id != except && c.Name.ToLower() == lowered)
            .Select(c => new { c.Name, c.IsActive, c.PsaConnectionId, Connection = db.PsaConnections.Where(p => p.Id == c.PsaConnectionId).Select(p => p.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (taken is null) return;
        throw new ValidationFailedException(taken.PsaConnectionId is not null
            ? $"\"{taken.Name}\" is already in the list: it comes from your PSA ({taken.Connection ?? "a PSA connection"}). Choose it there instead of adding it again."
            : taken.IsActive
                ? $"\"{taken.Name}\" is already in the list."
                : $"\"{taken.Name}\" was added before and has been retired. Bring it back instead of adding it again.");
    }
}
