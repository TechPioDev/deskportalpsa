using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Notifications;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Push notifications on a staff member's own devices: turn them on or off, choose the events, send a
/// test. Everything here is the caller's own - nobody signs up or reads another person's devices.
/// Staff means either ticket-view permission: a technician who sees only their assigned tickets is
/// exactly who needs to hear when one is assigned.
/// </summary>
[ApiController]
[Authorize]
[Route("api/me/push")]
public sealed class PushController(ICurrentUser user, IPushService push) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await push.StatusAsync(Me(), ct));

    [HttpPost("devices")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeBody body, CancellationToken ct)
        => Ok(await push.SubscribeAsync(Me(), new PushSubscribeInput(body.Endpoint, body.P256dh, body.Auth, body.DeviceLabel), ct));

    [HttpDelete("devices/{id:guid}")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await push.RemoveDeviceAsync(Me(), id, ct);
        return NoContent();
    }

    [HttpPut("preferences")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Preferences([FromBody] PushPreferencesDto body, CancellationToken ct)
        => Ok(await push.SavePreferencesAsync(Me(), body, ct));

    [HttpPost("test")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Test(CancellationToken ct) => Ok(new { delivered = await push.SendTestAsync(Me(), ct) });

    private Guid Me() => user.UserId ?? throw new ForbiddenException("Notifications are for staff accounts.");

    public sealed record SubscribeBody(
        [Required, StringLength(1000)] string Endpoint, [Required, StringLength(200)] string P256dh,
        [Required, StringLength(100)] string Auth, [StringLength(80)] string? DeviceLabel);
}
