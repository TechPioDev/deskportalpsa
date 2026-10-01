using Desk.Domain.Common;
using Desk.Domain.Identity;

namespace Desk.Domain.Workforce;

/// <summary>
/// A skill the desk plans work by ("Microsoft 365", "SonicWall"). Administered per organization -
/// nothing in the product hard-codes a skill. Retired rather than deleted, so the people who hold it
/// keep their history and a retired skill can come back.
/// </summary>
public class Skill : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>The name trimmed and upper-cased: two skills differing only in case or spacing are one.</summary>
    public required string NormalizedName { get; set; }

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public static string Normalize(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

/// <summary>How well someone knows a skill. Used to rank who is best suited, never to grade people.</summary>
public enum SkillLevel
{
    Basic = 1,
    Proficient = 2,
    Expert = 3,
}

/// <summary>A person holding a skill, at a level.</summary>
public class StaffSkill : TenantEntity
{
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    public Guid SkillId { get; set; }
    public Skill? Skill { get; set; }

    public SkillLevel Level { get; set; } = SkillLevel.Proficient;
    public Guid? AssignedByUserId { get; set; }
}
