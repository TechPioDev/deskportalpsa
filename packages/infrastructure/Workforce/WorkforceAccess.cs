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
    public async Task<IQueryable<AppUser>> VisibleStaffAsync(Guid callerId, CancellationToken ct)
    {
        var org = tenant.OrganizationId;
        var staff = tenant.IsPlatformScope ? db.AppUsers.AsQueryable() : db.AppUsers.Where(u => u.MspOrganizationId == org);
        var eff = await permissions.ResolveAsync(callerId, Permissions.ScheduleView, ct);
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

    public string OrganizationTimeZone()
    {
        var org = tenant.OrganizationId;
        var zone = db.MspOrganizations.Where(o => o.Id == org).Select(o => o.TimeZone).FirstOrDefault();
        return TimeZones.ToIana(zone) ?? "UTC";
    }
}
