using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Identity;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Desk.Api.Controllers;

/// <summary>
/// The public side of an invitation or a password reset: what the page at a link shows, and
/// using the link. Anonymous by design (the person has no sign-in yet, or has lost it), and
/// rate-limited like the other public forms. Nothing here says whether an address exists.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public")]
[EnableRateLimiting("public-forms")]
public sealed class InvitationsController(IInvitationService invitations) : ControllerBase
{
    public sealed record AcceptRequest(string Password);
    public sealed record ResetRequest(string Email);

    /// <summary>What the invitation page shows. An unknown or revoked link is not found.</summary>
    [HttpGet("invitations/{token}")]
    public async Task<IActionResult> Lookup(string token, CancellationToken ct)
        => await invitations.LookupAsync(token, ct) is { } found ? Ok(found) : NotFound(new { message = "This link is not valid." });

    /// <summary>Sets the password and links the sign-in. The link is spent.</summary>
    [HttpPost("invitations/{token}/accept")]
    public async Task<IActionResult> Accept(string token, [FromBody] AcceptRequest request, CancellationToken ct)
        => Ok(await invitations.AcceptAsync(token, request.Password ?? "", ct));

    /// <summary>Asks for a password reset. Always accepted; a mail goes only where there is a sign-in to reset.</summary>
    [HttpPost("password-reset")]
    public async Task<IActionResult> RequestReset([FromBody] ResetRequest request, CancellationToken ct)
    {
        await invitations.RequestPasswordResetAsync(request.Email ?? "", ct);
        return Accepted(new { message = "If that address has a sign-in, a link is on its way." });
    }

    /// <summary>The reset page: the same lookup, by the same rules.</summary>
    [HttpGet("password-reset/{token}")]
    public async Task<IActionResult> LookupReset(string token, CancellationToken ct)
        => await invitations.LookupAsync(token, ct) is { } found ? Ok(found) : NotFound(new { message = "This link is not valid." });

    [HttpPost("password-reset/{token}")]
    public async Task<IActionResult> Reset(string token, [FromBody] AcceptRequest request, CancellationToken ct)
        => Ok(await invitations.AcceptAsync(token, request.Password ?? "", ct));
}

/// <summary>
/// Inviting staff, and seeing where invitations stand. The same right that creates a staff user
/// sends them their way in.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminInvitationsController(IInvitationService invitations) : ControllerBase
{
    [HttpPost("users/{id:guid}/invite")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> InviteStaff(Guid id, CancellationToken ct)
        => Ok(await invitations.InviteStaffAsync(id, ct));

    /// <summary>Everyone active who has never signed in and has no open invitation.</summary>
    [HttpPost("users/invite-pending")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> InvitePending(CancellationToken ct)
        => Ok(await invitations.InviteAllPendingStaffAsync(ct));

    [HttpGet("invitations")]
    [RequirePermission(Permissions.UsersManage, Permissions.ClientUsersManage)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await invitations.ListAsync(ct));

    [HttpDelete("invitations/{id:guid}")]
    [RequirePermission(Permissions.UsersManage, Permissions.ClientUsersManage)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await invitations.RevokeAsync(id, ct);
        return NoContent();
    }

    /// <summary>A client user, invited by the desk (the client's own administrators invite from the Control Panel).</summary>
    [HttpPost("client-users/{id:guid}/invite")]
    [RequirePermission(Permissions.ClientUsersManage)]
    public async Task<IActionResult> InviteClient(Guid id, CancellationToken ct)
        => Ok(await invitations.InviteClientAsync(id, ct));
}

/// <summary>The organization's e-mail wording. Reading needs the health view; changing it is the organization's to manage.</summary>
[ApiController]
[Route("api/admin/email/templates")]
public sealed class AdminEmailTemplatesController(IEmailTemplateService templates) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.OrgManage, Permissions.IntegrationHealthView)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await templates.ListAsync(ct));

    [HttpGet("log")]
    [RequirePermission(Permissions.OrgManage, Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Log([FromQuery] int take = 100, CancellationToken ct = default) => Ok(await templates.RecentLogAsync(take, ct));

    [HttpGet("{key}")]
    [RequirePermission(Permissions.OrgManage, Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Get(string key, CancellationToken ct) => Ok(await templates.GetAsync(key, ct));

    [HttpPut("{key}")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> Save(string key, [FromBody] EmailTemplateInput input, CancellationToken ct) => Ok(await templates.SaveAsync(key, input, ct));

    [HttpDelete("{key}")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> Reset(string key, CancellationToken ct) => Ok(await templates.ResetAsync(key, ct));

    /// <summary>Rendered with sample values; the body may be unsaved text, so a change can be seen before it is kept.</summary>
    [HttpPost("{key}/preview")]
    [RequirePermission(Permissions.OrgManage, Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Preview(string key, [FromBody] EmailTemplateInput? unsaved, CancellationToken ct) => Ok(await templates.PreviewAsync(key, unsaved, ct));

    [HttpPost("{key}/test")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> SendTest(string key, CancellationToken ct) => Ok(await templates.SendTestAsync(key, ct));
}
