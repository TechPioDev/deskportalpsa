using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Domain.Authorization;
using Desk.Domain.Identity;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// Brings PSA technicians into the portal, one confirmed decision at a time.
///
/// Its own service rather than another method on the user admin service: this is the only thing in
/// user administration that needs to talk to a provider, and pushing a connector dependency into
/// that service would put a PSA call behind every user screen in the product.
/// </summary>
public sealed class TechnicianProvisioningService(
    DeskDbContext db,
    IConnectorResolver connectors,
    IUserAdminService users,
    IAuditWriter audit,
    ITenantContext tenant) : ITechnicianProvisioningService
{
    public async Task<IReadOnlyList<PsaTechnicianDto>> ListAsync(Guid psaConnectionId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var connector = await connectors.ResolveAsync(connection.Id, ct);
        var technicians = await connector.GetTechniciansAsync(ct);

        var linked = await db.UserPsaIdentities.AsNoTracking()
            .Where(i => i.PsaConnectionId == psaConnectionId)
            .ToDictionaryAsync(i => i.ExternalTechnicianId, i => i.AppUserId, ct);
        var byEmail = await db.AppUsers.AsNoTracking()
            .Where(u => u.MspOrganizationId == tenant.OrganizationId)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(ct);
        var emailIndex = byEmail
            .GroupBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        return technicians
            .Select(t =>
            {
                var hasEmail = !string.IsNullOrWhiteSpace(t.Email);
                if (linked.TryGetValue(t.ExternalId, out var linkedUser))
                    return new PsaTechnicianDto(t.ExternalId, t.DisplayName, t.Email, t.IsActive,
                        PsaTechnicianLink.Linked, linkedUser, false, null);

                if (hasEmail && emailIndex.TryGetValue(t.Email.Trim(), out var existing))
                    return new PsaTechnicianDto(t.ExternalId, t.DisplayName, t.Email, t.IsActive,
                        PsaTechnicianLink.MatchedByEmail, existing, true, null);

                // No email means no sign-in: the portal binds an account to a person by their
                // verified email at first login, so an account without one could never be used.
                // Almost always an API user or a service account, which is exactly what should not
                // be created here.
                return new PsaTechnicianDto(t.ExternalId, t.DisplayName, t.Email, t.IsActive,
                    PsaTechnicianLink.NotInPortal, null, hasEmail,
                    hasEmail ? null : "No email in the PSA — this is usually an API or service account.");
            })
            // Linked first is the wrong order for a screen whose job is what still needs doing.
            .OrderBy(t => t.Link == PsaTechnicianLink.Linked ? 1 : 0)
            .ThenByDescending(t => t.IsActive)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<UserSummary> ProvisionAsync(Guid psaConnectionId, string externalTechnicianId, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");

        var connector = await connectors.ResolveAsync(connection.Id, ct);
        var tech = (await connector.GetTechniciansAsync(ct))
            .FirstOrDefault(t => t.ExternalId == externalTechnicianId)
            ?? throw new NotFoundException("PSA technician");

        if (string.IsNullOrWhiteSpace(tech.Email))
            throw new ValidationFailedException(
                $"{tech.DisplayName} has no email address in {connection.Name}. Sign-in binds by verified "
                + "email, so an account without one could never be logged into — add the address in the PSA first.");

        var email = tech.Email.Trim();
        var existing = await db.AppUsers
            .FirstOrDefaultAsync(u => u.MspOrganizationId == tenant.OrganizationId && u.Email.ToLower() == email.ToLower(), ct);

        // Before anyone is created: a login somebody else already holds is refused outright, not
        // after a new account has been made for it.
        await PsaIdentityRules.EnsureLoginIsFreeAsync(db, psaConnectionId, tech.ExternalId, existing?.Id ?? Guid.Empty, ct);

        Guid userId;
        var created = false;
        if (existing is not null)
        {
            userId = existing.Id;
        }
        else
        {
            var technicianRole = await db.Roles
                .Where(r => r.IsSystemRole && r.BuiltInType == Desk.Domain.Enums.RoleType.Technician)
                .FirstOrDefaultAsync(ct)
                ?? throw new ValidationFailedException("No Technician role exists to assign.");

            // Through the same creation path an administrator uses by hand — one set of validation
            // rules, one audit event shape, no second way to make a user.
            var summary = await users.CreateAsync(
                new CreateStaffUserInput(
                    string.IsNullOrWhiteSpace(tech.DisplayName) ? email : tech.DisplayName.Trim(),
                    email,
                    [technicianRole.Id]), ct);
            userId = summary.Id;
            created = true;
        }

        // Idempotent: re-running on someone already mapped rewrites the same values.
        var identity = await db.UserPsaIdentities
            .FirstOrDefaultAsync(i => i.AppUserId == userId && i.PsaConnectionId == psaConnectionId, ct);
        if (identity is null)
        {
            db.UserPsaIdentities.Add(new UserPsaIdentity
            {
                MspOrganizationId = tenant.OrganizationId ?? Guid.Empty,
                AppUserId = userId,
                PsaConnectionId = psaConnectionId,
                ExternalTechnicianId = tech.ExternalId,
                ExternalTechnicianName = tech.DisplayName,
            });
        }
        else
        {
            identity.ExternalTechnicianId = tech.ExternalId;
            identity.ExternalTechnicianName = tech.DisplayName;
        }
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("user.provisioned-from-psa", "AppUser", userId.ToString(),
            new { connection = connection.Name, tech.ExternalId, tech.Email, createdNewUser = created }, ct);

        return (await users.GetAsync(userId, ct))!;
    }
}
