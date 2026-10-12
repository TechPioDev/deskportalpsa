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
        var ignored = (await db.PsaTechnicianIgnores.AsNoTracking()
                .Where(i => i.PsaConnectionId == psaConnectionId)
                .Select(i => i.ExternalTechnicianId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

                // Left alone on purpose. It can still be added or linked: doing so takes the
                // decision back, so it is not offered as a dead end.
                if (ignored.Contains(t.ExternalId.Trim()))
                    return new PsaTechnicianDto(t.ExternalId, t.DisplayName, t.Email, t.IsActive,
                        PsaTechnicianLink.Ignored, null, hasEmail, null);

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
            // Linked first is the wrong order for a screen whose job is what still needs doing;
            // and what someone has said to leave alone comes after everything else.
            .OrderBy(t => t.Link == PsaTechnicianLink.Ignored ? 2 : t.Link == PsaTechnicianLink.Linked ? 1 : 0)
            .ThenByDescending(t => t.IsActive)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SetIgnoredAsync(Guid psaConnectionId, string externalTechnicianId, bool ignored, string? name, CancellationToken ct = default)
    {
        var connection = await db.PsaConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == psaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        var id = externalTechnicianId.Trim();
        if (id.Length is 0 or > 100) throw new ValidationFailedException("That is not a login from the PSA.");

        var row = (await db.PsaTechnicianIgnores.Where(i => i.PsaConnectionId == psaConnectionId).ToListAsync(ct))
            .FirstOrDefault(i => string.Equals(i.ExternalTechnicianId.Trim(), id, StringComparison.OrdinalIgnoreCase));
        if (ignored == (row is not null)) return;

        if (ignored)
        {
            // Linked says "this login IS this person". Ignored says "it is nobody". Not both.
            var holder = (await db.UserPsaIdentities.AsNoTracking()
                    .Where(i => i.PsaConnectionId == psaConnectionId)
                    .Select(i => new { i.ExternalTechnicianId, i.AppUser!.DisplayName })
                    .ToListAsync(ct))
                .FirstOrDefault(i => string.Equals(i.ExternalTechnicianId.Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (holder is not null)
                throw new ValidationFailedException(
                    $"That login is linked to {holder.DisplayName} on {connection.Name}. Remove the link on their page first, if it is wrong.");

            db.PsaTechnicianIgnores.Add(new Desk.Domain.Identity.PsaTechnicianIgnore
            {
                MspOrganizationId = connection.MspOrganizationId, PsaConnectionId = psaConnectionId,
                ExternalTechnicianId = id, ExternalTechnicianName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            });
        }
        else db.PsaTechnicianIgnores.Remove(row!);

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(ignored ? "psa.technician.ignored" : "psa.technician.unignored", "PsaConnection", psaConnectionId.ToString(),
            new { connection = connection.Name, login = id, name = ignored ? name : row!.ExternalTechnicianName }, ct);
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
        // Linked now, so no longer "nobody": the earlier decision to leave it alone is taken back.
        await PsaIdentityRules.StopIgnoringAsync(db, psaConnectionId, tech.ExternalId, ct);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("user.provisioned-from-psa", "AppUser", userId.ToString(),
            new { connection = connection.Name, tech.ExternalId, tech.Email, createdNewUser = created }, ct);

        return (await users.GetAsync(userId, ct))!;
    }
}
