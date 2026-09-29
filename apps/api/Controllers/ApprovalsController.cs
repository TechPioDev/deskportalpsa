using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Approvals on tickets. Staff ask the client's named approver and can record an answer given by
/// phone; the approver answers in the portal. Answering in the portal is the approver's act only —
/// staff record, they never click on a client's behalf, and the record says which it was.
/// </summary>
[ApiController]
[Authorize]
public sealed class ApprovalsController(
    ICurrentUser user, IClientAccessResolver accessResolver, IApprovalService approvals) : ControllerBase
{
    // ---- Staff ----

    [HttpGet("api/tickets/{id:guid}/approvals")]
    [RequirePermission(Permissions.TicketsViewAll)]
    public async Task<IActionResult> StaffView(Guid id, CancellationToken ct)
        => Ok(await approvals.StaffViewAsync(StaffId(), id, ct));

    [HttpPost("api/tickets/{id:guid}/approvals")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Ask(Guid id, [FromBody] RequestBody body, CancellationToken ct)
        => Ok(await approvals.RequestAsync(StaffId(), StaffName(), id, new ApprovalRequestInput(body.ApproverId, body.Request), ct));

    [HttpPost("api/approvals/{id:guid}/record")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Record(Guid id, [FromBody] RecordBody body, CancellationToken ct)
        => Ok(await approvals.RecordAsync(StaffId(), StaffName(), id, new ApprovalRecordInput(body.Approved, body.Channel, body.Comment), ct));

    [HttpPost("api/approvals/{id:guid}/cancel")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => Ok(await approvals.CancelAsync(StaffId(), StaffName(), id, ct));

    // ---- Client ----

    [HttpGet("api/client/tickets/{id:guid}/approvals")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> ClientView(Guid id, CancellationToken ct)
        => Ok(await approvals.ClientViewAsync(await AccessAsync(ct), id, ct));

    /// <summary>Requests waiting on the signed-in client user.</summary>
    [HttpGet("api/client/approvals")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> Mine(CancellationToken ct)
        => Ok(await approvals.MineAsync(await AccessAsync(ct), ct));

    [HttpPost("api/client/approvals/{id:guid}/decide")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideBody body, CancellationToken ct)
        => Ok(await approvals.DecideAsync(await AccessAsync(ct), id, body.Approved, body.Comment, ct));

    private Guid StaffId() => user.UserId ?? throw new NotFoundException("Ticket");

    private string StaffName() => user.DisplayName ?? user.Email ?? "Staff";

    private async Task<ClientAccess> AccessAsync(CancellationToken ct)
        => await accessResolver.ResolveAsync(user.Subject ?? "", ct)
           ?? throw new ForbiddenException("Approvals are answered by the client's own approver.");

    public sealed record RequestBody(Guid ApproverId, [Required, StringLength(1000)] string Request);

    public sealed record RecordBody(bool Approved, [Required] string Channel, [StringLength(1000)] string? Comment = null);

    public sealed record DecideBody(bool Approved, [StringLength(1000)] string? Comment = null);
}
