using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// The skill catalogue and who holds which skill. The catalogue is the organization's own - nothing
/// is hard-coded - and a skill is retired rather than deleted, so history and holders survive.
/// </summary>
public sealed class SkillService(DeskDbContext db, WorkforceAccess access, ITenantContext tenant, ICurrentUser user, IAuditWriter audit)
    : ISkillService
{
    public const int NameMax = 80;
    public const int DescriptionMax = 300;

    public async Task<IReadOnlyList<SkillDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
        => await db.Skills.AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new SkillDto(s.Id, s.Name, s.Description, s.IsActive,
                db.StaffSkills.Count(x => x.SkillId == s.Id && x.AppUser!.IsActive)))
            .ToListAsync(ct);

    public async Task<SkillDto> CreateAsync(string name, string? description, CancellationToken ct = default)
    {
        await RequireManageAsync(ct);
        var (clean, normalized) = CleanName(name);
        if (await db.Skills.AnyAsync(s => s.NormalizedName == normalized, ct))
            throw new ValidationFailedException($"A skill called \"{clean}\" already exists. Reactivate it instead of adding it again.");
        var skill = new Skill
        {
            MspOrganizationId = tenant.OrganizationId ?? throw new TenantScopeMissingException(),
            Name = clean, NormalizedName = normalized, Description = CleanDescription(description),
        };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("skill.created", "Skill", skill.Id.ToString(), new { name = clean }, ct);
        return new SkillDto(skill.Id, skill.Name, skill.Description, true, 0);
    }

    public async Task<SkillDto> UpdateAsync(Guid id, string name, string? description, bool isActive, CancellationToken ct = default)
    {
        await RequireManageAsync(ct);
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Skill");
        var (clean, normalized) = CleanName(name);
        if (normalized != skill.NormalizedName && await db.Skills.AnyAsync(s => s.Id != id && s.NormalizedName == normalized, ct))
            throw new ValidationFailedException($"A skill called \"{clean}\" already exists.");
        var before = new { name = skill.Name, active = skill.IsActive };
        skill.Name = clean;
        skill.NormalizedName = normalized;
        skill.Description = CleanDescription(description);
        skill.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("skill.updated", "Skill", skill.Id.ToString(), new { before, after = new { name = clean, active = isActive } }, ct);
        var holders = await db.StaffSkills.CountAsync(x => x.SkillId == id && x.AppUser!.IsActive, ct);
        return new SkillDto(skill.Id, skill.Name, skill.Description, skill.IsActive, holders);
    }

    public async Task<IReadOnlyList<StaffSkillDto>> ForPersonAsync(Guid callerId, Guid appUserId, CancellationToken ct = default)
    {
        await access.VisiblePersonAsync(callerId, appUserId, ct);
        return await HeldAsync(appUserId, ct);
    }

    public async Task<IReadOnlyList<StaffSkillDto>> AssignAsync(Guid callerId, Guid appUserId, Guid skillId, SkillLevel level, CancellationToken ct = default)
    {
        var person = await access.ManagedPersonAsync(callerId, appUserId, ct);
        if (!Enum.IsDefined(level)) throw new ValidationFailedException("Choose a level: basic, proficient or expert.");
        // Found through the tenant filter: another organization's skill is simply not found.
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, ct) ?? throw new NotFoundException("Skill");

        var held = await db.StaffSkills.FirstOrDefaultAsync(s => s.AppUserId == appUserId && s.SkillId == skillId, ct);
        if (held is null)
        {
            if (!skill.IsActive)
                throw new ValidationFailedException($"\"{skill.Name}\" is retired. Reactivate it before giving it to anyone.");
            db.StaffSkills.Add(new StaffSkill
            {
                MspOrganizationId = skill.MspOrganizationId, AppUserId = appUserId, SkillId = skillId, Level = level, AssignedByUserId = callerId,
            });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("skill.assigned", "AppUser", appUserId.ToString(), new { person = person.DisplayName, skill = skill.Name, level = level.ToString() }, ct);
        }
        else if (held.Level != level)
        {
            var from = held.Level;
            held.Level = level;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("skill.level_changed", "AppUser", appUserId.ToString(),
                new { person = person.DisplayName, skill = skill.Name, from = from.ToString(), to = level.ToString() }, ct);
        }
        else
            throw new ValidationFailedException($"{person.DisplayName} already holds \"{skill.Name}\" at that level.");
        return await HeldAsync(appUserId, ct);
    }

    public async Task<IReadOnlyList<StaffSkillDto>> RemoveAsync(Guid callerId, Guid appUserId, Guid skillId, CancellationToken ct = default)
    {
        var person = await access.ManagedPersonAsync(callerId, appUserId, ct);
        var held = await db.StaffSkills.Include(s => s.Skill).FirstOrDefaultAsync(s => s.AppUserId == appUserId && s.SkillId == skillId, ct)
            ?? throw new NotFoundException("Skill");
        db.StaffSkills.Remove(held);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("skill.removed", "AppUser", appUserId.ToString(), new { person = person.DisplayName, skill = held.Skill?.Name }, ct);
        return await HeldAsync(appUserId, ct);
    }

    private async Task RequireManageAsync(CancellationToken ct)
    {
        if (user.UserId is not { } me || !await access.CanManageAsync(me, ct))
            throw new ForbiddenException("Only someone who manages the workforce can change the skill catalogue.");
    }

    private async Task<IReadOnlyList<StaffSkillDto>> HeldAsync(Guid appUserId, CancellationToken ct)
        => await db.StaffSkills.AsNoTracking().Where(s => s.AppUserId == appUserId).OrderBy(s => s.Skill!.Name)
            .Select(s => new StaffSkillDto(s.SkillId, s.Skill!.Name, s.Level, s.Skill.IsActive)).ToListAsync(ct);

    private static (string Clean, string Normalized) CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length < 2) throw new ValidationFailedException("Give the skill a name of at least 2 characters.");
        if (clean.Length > NameMax) throw new ValidationFailedException($"Keep the skill name to {NameMax} characters.");
        if (clean.Any(char.IsControl)) throw new ValidationFailedException("The skill name contains characters that can't be shown.");
        return (clean, Skill.Normalize(clean));
    }

    private static string? CleanDescription(string? description)
    {
        var d = description?.Trim();
        if (string.IsNullOrEmpty(d)) return null;
        if (d.Length > DescriptionMax) throw new ValidationFailedException($"Keep the description to {DescriptionMax} characters.");
        return d;
    }
}
