using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.ControlPanel;

/// <summary>
/// Control panel implementation. All queries are already tenant-scoped by the DbContext global
/// filter; this service adds the client-company and per-user authorization on top and audits
/// every mutation. Company administrators implicitly hold every section for their own company.
/// </summary>
public sealed class ControlPanelService(DeskDbContext db, IAuditWriter audit) : IControlPanelService
{
    public async Task<ControlPanelCapabilities> GetCapabilitiesAsync(ClientAccess access, CancellationToken ct = default)
    {
        var company = await db.ClientCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Client company");

        IReadOnlyList<string> sections = access.IsCompanyAdministrator
            ? AllSectionKeys
            : await EffectiveSectionsAsync(access.ClientUserId, ct);

        return new ControlPanelCapabilities(
            access.IsCompanyAdministrator, access.ClientCompanyId, company.Name, sections);
    }

    // ---- Ticket instructions ------------------------------------------------------------------

    public async Task<InstructionsView> GetInstructionsAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.TicketInstructions, write: false, ct);

        var rows = await db.TicketInstructions.AsNoTracking().ToListAsync(ct);
        var company = await db.ClientCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct);

        var global = rows.FirstOrDefault(r => r.ClientCompanyId == null);
        var account = rows.FirstOrDefault(r => r.ClientCompanyId == access.ClientCompanyId);

        return new InstructionsView(
            Global: ToDto(global, null, "All accounts (default)"),
            Accounts: new[] { ToDto(account, access.ClientCompanyId, company?.Name ?? "This account") });
    }

    public async Task<InstructionDto> SaveInstructionAsync(ClientAccess access, Guid? clientCompanyId, string body, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.TicketInstructions, write: true, ct);

        // A client user may only edit the org-wide default or their OWN account — never another account.
        if (clientCompanyId is { } id && id != access.ClientCompanyId)
            throw new ForbiddenException("You can only edit instructions for your own account.");

        var editor = await db.ClientUsers.AsNoTracking()
            .Where(u => u.Id == access.ClientUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

        var row = await db.TicketInstructions
            .FirstOrDefaultAsync(r => r.ClientCompanyId == clientCompanyId, ct);
        if (row is null)
        {
            row = new TicketInstruction { ClientCompanyId = clientCompanyId };
            db.TicketInstructions.Add(row);
        }
        row.Body = body ?? string.Empty;
        row.LastEditedBy = editor;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.instructions.save", nameof(TicketInstruction), row.Id.ToString(),
            new { scope = clientCompanyId is null ? "global" : "account", clientCompanyId }, ct);

        var name = clientCompanyId is null
            ? "All accounts (default)"
            : (await db.ClientCompanies.AsNoTracking().Where(c => c.Id == clientCompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "This account");
        return ToDto(row, clientCompanyId, name);
    }

    // ---- Users & access -----------------------------------------------------------------------

    public async Task<IReadOnlyList<ClientUserDto>> ListUsersAsync(ClientAccess access, CancellationToken ct = default)
    {
        RequireAdmin(access);

        var users = await db.ClientUsers.AsNoTracking()
            .Where(u => u.ClientCompanyId == access.ClientCompanyId)
            .OrderByDescending(u => u.IsCompanyAdministrator).ThenBy(u => u.DisplayName)
            .ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var grants = await db.ClientAccessGrants.AsNoTracking()
            .Where(g => userIds.Contains(g.ClientUserId)).ToListAsync(ct);

        return users.Select(u => ToUserDto(u, grants.Where(g => g.ClientUserId == u.Id))).ToList();
    }

    public async Task<ClientUserDto> InviteUserAsync(ClientAccess access, InviteClientUserInput input, CancellationToken ct = default)
    {
        RequireAdmin(access);

        var email = (input.Email ?? "").Trim();
        var name = (input.DisplayName ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@')) throw new ValidationFailedException("A valid email address is required.");
        if (name.Length == 0) throw new ValidationFailedException("A display name is required.");

        var exists = await db.ClientUsers
            .AnyAsync(u => u.ClientCompanyId == access.ClientCompanyId && u.Email == email, ct);
        if (exists) throw new ValidationFailedException("A user with that email already exists in this account.");

        var user = new ClientUser
        {
            ClientCompanyId = access.ClientCompanyId,
            Email = email,
            DisplayName = name,
            IsCompanyAdministrator = input.IsCompanyAdministrator,
            IsActive = true,
        };
        db.ClientUsers.Add(user);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.user.invite", nameof(ClientUser), user.Id.ToString(),
            new { email, isAdmin = input.IsCompanyAdministrator }, ct);

        return ToUserDto(user, Array.Empty<ClientAccessGrant>());
    }

    public async Task SetUserActiveAsync(ClientAccess access, Guid clientUserId, bool active, CancellationToken ct = default)
    {
        RequireAdmin(access);

        var user = await LoadCompanyUserAsync(access, clientUserId, ct);
        if (!active && user.IsCompanyAdministrator && await IsLastActiveAdminAsync(access.ClientCompanyId, user.Id, ct))
            throw new ValidationFailedException("You cannot disable the last active administrator.");

        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.user.set_active", nameof(ClientUser), user.Id.ToString(), new { active }, ct);
    }

    public async Task<ClientUserDto> SetUserAccessAsync(ClientAccess access, Guid clientUserId, SetAccessInput input, CancellationToken ct = default)
    {
        RequireAdmin(access);

        var user = await LoadCompanyUserAsync(access, clientUserId, ct);

        // Demoting the last admin would strip the company of any administrator — block it.
        if (user.IsCompanyAdministrator && !input.IsCompanyAdministrator
            && await IsLastActiveAdminAsync(access.ClientCompanyId, user.Id, ct))
            throw new ValidationFailedException("You cannot remove the last active administrator.");

        user.IsCompanyAdministrator = input.IsCompanyAdministrator;

        var existing = await db.ClientAccessGrants.Where(g => g.ClientUserId == user.Id).ToListAsync(ct);
        db.ClientAccessGrants.RemoveRange(existing);

        if (!input.IsCompanyAdministrator)
        {
            // Deduplicate (section, company-scope) pairs; an admin needs no grants at all.
            var seen = new HashSet<(ControlPanelSection, Guid?)>();
            foreach (var g in input.Grants ?? Array.Empty<AccessGrantDto>())
            {
                if (!TryParseSection(g.Section, out var section)) continue;
                // Any account scope must be the user's own company (single-company model in CP-1).
                var scope = g.ClientCompanyId is { } cid && cid == access.ClientCompanyId ? cid : (Guid?)null;
                if (!seen.Add((section, scope))) continue;
                db.ClientAccessGrants.Add(new ClientAccessGrant
                {
                    ClientUserId = user.Id,
                    Section = section,
                    ClientCompanyId = scope,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.user.set_access", nameof(ClientUser), user.Id.ToString(),
            new { isAdmin = input.IsCompanyAdministrator, sections = (input.Grants ?? Array.Empty<AccessGrantDto>()).Select(g => g.Section) }, ct);

        var grants = await db.ClientAccessGrants.AsNoTracking().Where(g => g.ClientUserId == user.Id).ToListAsync(ct);
        return ToUserDto(user, grants);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static readonly IReadOnlyList<string> AllSectionKeys =
        Enum.GetValues<ControlPanelSection>().Select(SectionKey).ToList();

    private async Task<IReadOnlyList<string>> EffectiveSectionsAsync(Guid clientUserId, CancellationToken ct)
    {
        var sections = await db.ClientAccessGrants.AsNoTracking()
            .Where(g => g.ClientUserId == clientUserId)
            .Select(g => g.Section).Distinct()
            .ToListAsync(ct);
        return sections.Select(SectionKey).ToList();
    }

    private async Task EnsureSectionAsync(ClientAccess access, ControlPanelSection section, bool write, CancellationToken ct)
    {
        if (access.IsCompanyAdministrator) return;
        var granted = await db.ClientAccessGrants.AsNoTracking()
            .AnyAsync(g => g.ClientUserId == access.ClientUserId && g.Section == section, ct);
        if (!granted)
            throw new ForbiddenException($"You do not have access to the {SectionKey(section)} section.");
    }

    private static void RequireAdmin(ClientAccess access)
    {
        if (!access.IsCompanyAdministrator)
            throw new ForbiddenException("Only a company administrator can manage users and access.");
    }

    private async Task<ClientUser> LoadCompanyUserAsync(ClientAccess access, Guid clientUserId, CancellationToken ct)
        => await db.ClientUsers.FirstOrDefaultAsync(u => u.Id == clientUserId && u.ClientCompanyId == access.ClientCompanyId, ct)
           ?? throw new NotFoundException("Client user");

    private async Task<bool> IsLastActiveAdminAsync(Guid companyId, Guid excludingUserId, CancellationToken ct)
        => !await db.ClientUsers.AnyAsync(u =>
            u.ClientCompanyId == companyId && u.IsCompanyAdministrator && u.IsActive && u.Id != excludingUserId, ct);

    private static InstructionDto ToDto(TicketInstruction? row, Guid? companyId, string accountName)
        => new(companyId,
               companyId is null ? "global" : "account",
               accountName,
               row?.Body ?? "",
               row?.LastEditedBy,
               row?.UpdatedAt);

    private static ClientUserDto ToUserDto(ClientUser u, IEnumerable<ClientAccessGrant> grants)
        => new(u.Id, u.Email, u.DisplayName, u.IsCompanyAdministrator, u.IsActive,
               grants.Select(g => new AccessGrantDto(SectionKey(g.Section), g.ClientCompanyId)).ToList(),
               SignInLinked: u.IdpSubject != null);

    // Wire format: camelCase section key (e.g. "ticketInstructions").
    private static string SectionKey(ControlPanelSection s)
    {
        var n = s.ToString();
        return char.ToLowerInvariant(n[0]) + n[1..];
    }

    private static bool TryParseSection(string? key, out ControlPanelSection section)
    {
        section = default;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var normalized = char.ToUpperInvariant(key[0]) + key[1..];
        return Enum.TryParse(normalized, ignoreCase: true, out section);
    }
}
