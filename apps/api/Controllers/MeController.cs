using Desk.Application.Abstractions;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>Echoes the resolved identity for the current token — a minimal authenticated probe.</summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(
    ICurrentUser user,
    ITenantContext tenant,
    DeskDbContext db,
    Desk.Application.Boards.BoardFeatureOptions features) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(new
    {
        subject = user.Subject,
        // The caller's own portal user id and the teams they are in. "My tickets" and "my team's
        // queue" are questions about the person reading the page, and without these the page has to
        // ask the server to answer each one for it — a round trip to learn a fact that never changes
        // within a session.
        userId = user.UserId,
        teamIds = user.UserId is { } uid
            ? await db.UserTeams.AsNoTracking().Where(m => m.AppUserId == uid).Select(m => m.TeamId).ToListAsync(ct)
            : [],
        email = user.Email,
        displayName = user.DisplayName,
        organizationId = tenant.OrganizationId,
        isPlatformScope = tenant.IsPlatformScope,
        permissions = user.Permissions.OrderBy(p => p),
        // What this installation has switched on, so the interface offers only what the API will
        // answer rather than showing a page that returns "not found".
        features = new { internalBoards = features.InternalBoards },
    });
}
