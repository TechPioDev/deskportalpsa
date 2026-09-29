using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Which device a ticket is about. Staff choose from the ticket's client's devices and the PSA is told;
/// a client names one of their company's devices when raising a ticket.
/// </summary>
[ApiController]
[Authorize]
public sealed class TicketDevicesController(
    ICurrentUser user, IClientAccessResolver accessResolver, ITicketDeviceService devices) : ControllerBase
{
    [HttpGet("api/tickets/{id:guid}/device-choices")]
    [RequirePermission(Permissions.TicketsViewAll)]
    public async Task<IActionResult> Choices(Guid id, CancellationToken ct)
        => Ok(await devices.ChoicesAsync(StaffId(), id, ct));

    [HttpPut("api/tickets/{id:guid}/device")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Set(Guid id, [FromBody] SetDeviceRequest req, CancellationToken ct)
        => Ok(new { device = await devices.SetAsync(StaffId(), id, req.DeviceId, ct) });

    /// <summary>The devices a client can name when raising a ticket. Every client user, not only
    /// administrators: the person whose laptop is broken is the one who knows which laptop it is.</summary>
    [HttpGet("api/client/device-choices")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> ClientChoices(CancellationToken ct)
        => Ok(await devices.ClientChoicesAsync(
            await accessResolver.ResolveAsync(user.Subject ?? "", ct) ?? throw new ForbiddenException("Devices are listed for the client's own users."), ct));

    private Guid StaffId() => user.UserId ?? throw new NotFoundException("Ticket");

    public sealed record SetDeviceRequest(Guid? DeviceId);
}
