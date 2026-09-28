using Desk.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>Echoes the resolved identity for the current token — a minimal authenticated probe.</summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(ICurrentUser user, ITenantContext tenant, Desk.Application.Boards.BoardFeatureOptions features) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        subject = user.Subject,
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
