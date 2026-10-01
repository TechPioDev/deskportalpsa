using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// Choosing someone to view the portal as, and starting and ending the view. Administrators only
/// (users AND roles - see <see cref="ViewAs.MayViewAs"/>). The web app's proxy never sends the
/// view-as header to these routes, so they always run as the administrator themselves; the view
/// itself is applied per request by the claims transformation, which re-checks everything here.
/// </summary>
[Authorize]
[ApiController]
[Route("api/view-as")]
[RequirePermission(Permissions.UsersManage)]
public sealed class ViewAsController(DeskDbContext db, ICurrentUser user, ITenantContext tenant, IAuditWriter audit) : ControllerBase
{
    /// <summary>Staff and client portal users the administrator may view as, matching the search.</summary>
    [HttpGet("people")]
    public async Task<IActionResult> People([FromQuery] string? q, CancellationToken ct)
    {
        var me = Caller();
        var needle = (q ?? "").Trim().ToLowerInvariant();

        var staff = await db.AppUsers.AsNoTracking()
            .Where(u => u.IsActive && u.Id != me && (tenant.IsPlatformScope || u.MspOrganizationId == tenant.OrganizationId))
            .Where(u => needle == "" || u.DisplayName.ToLower().Contains(needle) || u.Email.ToLower().Contains(needle))
            .OrderBy(u => u.DisplayName).Take(25)
            .Select(u => new
            {
                key = "u:" + u.Id, kind = "staff", name = u.DisplayName, email = u.Email,
                detail = db.Roles.Where(r => u.Roles.Select(x => x.RoleId).Contains(r.Id)).Select(r => r.Name).FirstOrDefault(),
                available = true, reason = (string?)null,
            })
            .ToListAsync(ct);
        var clients = await db.ClientUsers.AsNoTracking()
            .Where(u => u.IsActive)
            .Where(u => needle == "" || u.DisplayName.ToLower().Contains(needle) || u.Email.ToLower().Contains(needle))
            .OrderBy(u => u.DisplayName).Take(25)
            .Select(u => new
            {
                key = "c:" + u.Id, kind = "client", name = u.DisplayName, email = u.Email,
                detail = db.ClientCompanies.Where(c => c.Id == u.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                available = u.IdpSubject != null,
                reason = u.IdpSubject == null ? "Has not signed in yet" : null,
            })
            .ToListAsync(ct);
        return Ok(staff.Concat(clients).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Checks the person may be viewed, and records that the view began.</summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest req, CancellationToken ct)
    {
        var me = Caller();
        var (target, refusal) = await ViewAs.ResolveAsync(db, req.Key, me, tenant.OrganizationId, tenant.IsPlatformScope, ct);
        if (target is null) throw new ValidationFailedException(refusal ?? "Choose who to view as.");
        await audit.WriteAsync("user.view_as.started", target.Kind == "staff" ? "AppUser" : "ClientUser", target.Id.ToString(),
            new { viewed = target.Name, kind = target.Kind }, ct);
        return Ok(new { key = target.Key, name = target.Name, kind = target.Kind });
    }

    /// <summary>Records that the view ended. The web app drops its cookie whatever this answers.</summary>
    [HttpPost("stop")]
    public async Task<IActionResult> Stop([FromBody] StopRequest req, CancellationToken ct)
    {
        Caller();
        await audit.WriteAsync("user.view_as.ended", "ViewAs", req.Key, new { key = req.Key }, ct);
        return NoContent();
    }

    /// <summary>
    /// The administrator themselves - never someone being viewed, and only one who manages roles too.
    /// A request that is already a view cannot start another or look for people.
    /// </summary>
    private Guid Caller()
    {
        if (user.ViewedByUserId is not null)
            throw new ForbiddenException("Exit the current view first.");
        if (!ViewAs.MayViewAs(user.Permissions) || user.UserId is not { } me)
            throw new ForbiddenException("Only an administrator can view the portal as someone else.");
        return me;
    }

    public sealed record StartRequest(string Key);

    public sealed record StopRequest(string? Key = null);
}
