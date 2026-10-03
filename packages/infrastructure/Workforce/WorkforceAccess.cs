using Desk.Application.Abstractions;
using Desk.Application.Authorization;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Identity;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Who the caller may see and change in the workforce module - the one place it is decided.
///
/// Seeing follows schedule.view's scope: Own (yourself), Team (people you share a team with),
/// Department (people you share a department with), All (everyone in the organization). Changing
/// needs workforce.manage. People are staff accounts (<see cref="AppUser"/>), which carry no tenant
/// filter of their own, so the organization is applied here explicitly; a client account is a
/// different table and can never appear.
/// </summary>
public sealed class WorkforceAccess(DeskDbContext db, ITenantContext tenant, IEffectivePermissionService permissions)
{
    /// <summary>The staff accounts the caller may see, as a query to filter further.</summary>
    public Task<IQueryable<AppUser>> VisibleStaffAsync(Guid callerId, CancellationToken ct)
        => StaffInScopeAsync(callerId, Permissions.ScheduleView, ct);

    /// <summary>
    /// The staff accounts a scoped workforce permission reaches for the caller: themselves (Own), the
    /// people they share a team or department with, or everyone in the organization.
    /// </summary>
    private async Task<IQueryable<AppUser>> StaffInScopeAsync(Guid callerId, string permission, CancellationToken ct)
    {
        var org = tenant.OrganizationId;
        var staff = tenant.IsPlatformScope ? db.AppUsers.AsQueryable() : db.AppUsers.Where(u => u.MspOrganizationId == org);
        var eff = await permissions.ResolveAsync(callerId, permission, ct);
        switch (eff.Scope)
        {
            case PermissionScope.All:
                return staff;
            case PermissionScope.Department:
                var myDepartments = await db.UserDepartments.Where(m => m.AppUserId == callerId).Select(m => m.DepartmentId).ToListAsync(ct);
                return staff.Where(u => u.Id == callerId || db.UserDepartments.Any(m => m.AppUserId == u.Id && myDepartments.Contains(m.DepartmentId)));
            case PermissionScope.Team:
                var myTeams = await db.UserTeams.Where(m => m.AppUserId == callerId).Select(m => m.TeamId).ToListAsync(ct);
                return staff.Where(u => u.Id == callerId || db.UserTeams.Any(m => m.AppUserId == u.Id && myTeams.Contains(m.TeamId)));
            case PermissionScope.Own:
            case PermissionScope.Assigned:
                return staff.Where(u => u.Id == callerId);
            default:
                return staff.Where(_ => false);
        }
    }

    /// <summary>A person the caller may see, or "not found" - the same answer whether they don't exist or aren't visible.</summary>
    public async Task<AppUser> VisiblePersonAsync(Guid callerId, Guid appUserId, CancellationToken ct)
        => await (await VisibleStaffAsync(callerId, ct)).FirstOrDefaultAsync(u => u.Id == appUserId, ct)
           ?? throw new NotFoundException("Person");

    public async Task<bool> CanManageAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.WorkforceManage, ct)).Scope == PermissionScope.All;

    /// <summary>A person the caller may change: managing the workforce, and the person is in their organization.</summary>
    public async Task<AppUser> ManagedPersonAsync(Guid callerId, Guid appUserId, CancellationToken ct)
    {
        if (!await CanManageAsync(callerId, ct))
            throw new ForbiddenException("Only someone who manages the workforce can change schedules and skills.");
        var org = tenant.OrganizationId;
        return await db.AppUsers.FirstOrDefaultAsync(u => u.Id == appUserId && (tenant.IsPlatformScope || u.MspOrganizationId == org), ct)
               ?? throw new NotFoundException("Person");
    }

    /// <summary>Whether the caller's availability.manage scope reaches this person.</summary>
    public async Task<bool> CanManageAvailabilityAsync(Guid callerId, Guid appUserId, CancellationToken ct)
        => await (await StaffInScopeAsync(callerId, Permissions.AvailabilityManage, ct)).AnyAsync(u => u.Id == appUserId, ct);

    /// <summary>
    /// Of these people, those whose exceptions the caller may read in full (reason and note): the
    /// caller themselves, and anyone their availability.manage scope reaches.
    /// </summary>
    public async Task<HashSet<Guid>> ExceptionDetailsVisibleAsync(Guid callerId, IReadOnlyCollection<Guid> appUserIds, CancellationToken ct)
    {
        var managed = await (await StaffInScopeAsync(callerId, Permissions.AvailabilityManage, ct))
            .Where(u => appUserIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var visible = managed.ToHashSet();
        if (appUserIds.Contains(callerId)) visible.Add(callerId);
        return visible;
    }

    /// <summary>
    /// A person whose time away the caller may record. Someone the caller cannot even see is "not
    /// found"; someone they can see but not manage is refused - saying so tells them nothing new.
    /// </summary>
    public async Task<AppUser> AvailabilityManagedPersonAsync(Guid callerId, Guid appUserId, CancellationToken ct)
    {
        var person = await VisiblePersonAsync(callerId, appUserId, ct);
        if (!await CanManageAvailabilityAsync(callerId, appUserId, ct))
            throw new ForbiddenException("You can't change this person's availability.");
        return person;
    }

    /// <summary>The caller may plan their own work: schedule.manage at any scope.</summary>
    public async Task<bool> MayPlanOwnAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.ScheduleManage, ct)).Scope is not PermissionScope.None;

    /// <summary>
    /// The caller may place, move, fix, give away and take out this person's work as someone who
    /// schedules others: schedule.manage wider than Own, reaching the person. True for a manager on
    /// their own plan too - the authority is theirs either way.
    /// </summary>
    public async Task<bool> CanScheduleOthersAsync(Guid callerId, Guid appUserId, CancellationToken ct)
    {
        var scope = (await permissions.ResolveAsync(callerId, Permissions.ScheduleManage, ct)).Scope;
        if (scope is not (PermissionScope.All or PermissionScope.Department or PermissionScope.Team)) return false;
        return await (await StaffInScopeAsync(callerId, Permissions.ScheduleManage, ct)).AnyAsync(u => u.Id == appUserId, ct);
    }

    /// <summary>Everyone the caller may put work in front of: themselves, and whoever their schedule.manage scope reaches.</summary>
    public Task<IQueryable<AppUser>> SchedulableStaffAsync(Guid callerId, CancellationToken ct)
        => StaffInScopeAsync(callerId, Permissions.ScheduleManage, ct);

    /// <summary>
    /// Which of these people the caller schedules as someone who schedules others (see
    /// <see cref="CanScheduleOthersAsync"/>) - one query for a whole list, for screens that show many
    /// people's work at once.
    /// </summary>
    public async Task<HashSet<Guid>> ScheduledByAsync(Guid callerId, List<Guid> appUserIds, CancellationToken ct)
    {
        if (appUserIds.Count == 0) return [];
        var scope = (await permissions.ResolveAsync(callerId, Permissions.ScheduleManage, ct)).Scope;
        if (scope is not (PermissionScope.All or PermissionScope.Department or PermissionScope.Team)) return [];
        return (await (await StaffInScopeAsync(callerId, Permissions.ScheduleManage, ct))
            .Where(u => appUserIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task<bool> MayOverrideAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.ScheduleOverride, ct)).Scope == PermissionScope.All;

    /// <summary>Narrows a set of staff to a team, a department, and people holding some skills (every one, or any one).</summary>
    public IQueryable<AppUser> Narrow(IQueryable<AppUser> staff, Guid? teamId, Guid? departmentId, IReadOnlyList<Guid>? skillIds, bool matchAllSkills)
    {
        if (teamId is { } team) staff = staff.Where(u => db.UserTeams.Any(m => m.AppUserId == u.Id && m.TeamId == team));
        if (departmentId is { } dept) staff = staff.Where(u => db.UserDepartments.Any(m => m.AppUserId == u.Id && m.DepartmentId == dept));
        if (skillIds is { Count: > 0 })
        {
            var wanted = skillIds.Distinct().ToList();
            staff = matchAllSkills
                ? staff.Where(u => db.StaffSkills.Count(s => s.AppUserId == u.Id && wanted.Contains(s.SkillId)) == wanted.Count)
                : staff.Where(u => db.StaffSkills.Any(s => s.AppUserId == u.Id && wanted.Contains(s.SkillId)));
        }
        return staff;
    }

    public string OrganizationTimeZone()
    {
        var org = tenant.OrganizationId;
        var zone = db.MspOrganizations.Where(o => o.Id == org).Select(o => o.TimeZone).FirstOrDefault();
        return TimeZones.ToIana(zone) ?? "UTC";
    }
}
