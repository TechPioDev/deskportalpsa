using System.ComponentModel.DataAnnotations;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Abstractions;
using Desk.Application.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Client control panel. Every action resolves the caller's client identity (company + user) and is
/// scoped to their company; the service layer enforces per-section/administrator authorization.
/// Staff/MSP callers are refused — they use the dashboard instead.
/// </summary>
[ApiController]
[Route("api/control-panel")]
[Authorize]
public sealed class ControlPanelController(
    ICurrentUser user,
    IClientAccessResolver accessResolver,
    IControlPanelService svc,
    Desk.Application.Identity.IInvitationService invitations) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken ct)
        => Ok(await svc.GetCapabilitiesAsync(await AccessAsync(ct), ct));

    // ---- Ticket instructions ----
    [HttpGet("instructions")]
    public async Task<IActionResult> GetInstructions(CancellationToken ct)
        => Ok(await svc.GetInstructionsAsync(await AccessAsync(ct), ct));

    [HttpPut("instructions")]
    public async Task<IActionResult> SaveInstruction([FromBody] SaveInstructionRequest req, CancellationToken ct)
        => Ok(await svc.SaveInstructionAsync(await AccessAsync(ct), req.ClientCompanyId, req.Body ?? "", ct));

    // ---- Users & access (admin only, enforced in the service) ----
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
        => Ok(await svc.ListUsersAsync(await AccessAsync(ct), ct));

    [HttpPost("users")]
    public async Task<IActionResult> InviteUser([FromBody] InviteUserRequest req, CancellationToken ct)
    {
        // The row first, then the mail: the person exists in the account whether or not the mail goes.
        var created = await svc.InviteUserAsync(await AccessAsync(ct),
            new InviteClientUserInput(req.Email, req.DisplayName, req.IsCompanyAdministrator), ct);
        var sent = await invitations.InviteClientAsync(created.Id, ct);
        return Ok(new { user = created, invitation = sent });
    }

    /// <summary>Sends the invitation again (a new link; the old one stops working). Only for a user of this company.</summary>
    [HttpPost("users/{id:guid}/invite")]
    public async Task<IActionResult> ReinviteUser(Guid id, CancellationToken ct)
    {
        var access = await AccessAsync(ct);
        if (!(await svc.ListUsersAsync(access, ct)).Any(u => u.Id == id)) throw new Desk.Application.Common.NotFoundException("User");
        return Ok(await invitations.InviteClientAsync(id, ct));
    }

    [HttpPost("users/{id:guid}/active")]
    public async Task<IActionResult> SetUserActive(Guid id, [FromBody] SetActiveRequest req, CancellationToken ct)
    {
        await svc.SetUserActiveAsync(await AccessAsync(ct), id, req.Active, ct);
        return NoContent();
    }

    [HttpPut("users/{id:guid}/access")]
    public async Task<IActionResult> SetUserAccess(Guid id, [FromBody] SetAccessRequest req, CancellationToken ct)
        => Ok(await svc.SetUserAccessAsync(await AccessAsync(ct), id,
            new SetAccessInput(req.IsCompanyAdministrator,
                (req.Grants ?? []).Select(g => new AccessGrantDto(g.Section, g.ClientCompanyId)).ToList()), ct));

    private async Task<ClientAccess> AccessAsync(CancellationToken ct)
        => await accessResolver.ResolveAsync(user.Subject ?? "", ct)
           ?? throw new ForbiddenException("The control panel is for client portal users.");

    public sealed record SaveInstructionRequest(Guid? ClientCompanyId, [StringLength(20000)] string? Body);

    public sealed record InviteUserRequest(
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(200, MinimumLength = 1)] string DisplayName,
        bool IsCompanyAdministrator);

    public sealed record SetActiveRequest(bool Active);

    public sealed record SetAccessRequest(bool IsCompanyAdministrator, List<GrantRequest>? Grants);

    public sealed record GrantRequest([Required] string Section, Guid? ClientCompanyId);
}
