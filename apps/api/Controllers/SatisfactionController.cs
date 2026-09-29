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
/// Customer satisfaction. A client rates a finished ticket of their own; staff with the team view read
/// the ratings back. Rating is a client's act only — staff cannot answer on a client's behalf, which is
/// the one thing that would make the numbers worthless.
/// </summary>
[ApiController]
[Authorize]
public sealed class SatisfactionController(
    ICurrentUser user, IClientAccessResolver accessResolver, ISatisfactionService satisfaction) : ControllerBase
{
    [HttpGet("api/tickets/{id:guid}/satisfaction")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> State(Guid id, CancellationToken ct)
        => Ok(await satisfaction.StateAsync(await AccessAsync(ct), id, ct));

    [HttpPost("api/tickets/{id:guid}/satisfaction")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> Rate(Guid id, [FromBody] RateRequest req, CancellationToken ct)
        => Ok(await satisfaction.RateAsync(await AccessAsync(ct), id, req.Rating, req.Comment, ct));

    /// <summary>Ratings given between two moments, overall and per technician and client.</summary>
    [HttpGet("api/dashboard/satisfaction")]
    [RequirePermission(Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> Summary([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);
        if (start > end) throw new ValidationFailedException("The period must start before it ends.");
        return Ok(await satisfaction.SummaryAsync(start, end, ct));
    }

    private async Task<ClientAccess> AccessAsync(CancellationToken ct)
        => await accessResolver.ResolveAsync(user.Subject ?? "", ct)
           ?? throw new ForbiddenException("Ratings are given by the client the ticket was for.");

    public sealed record RateRequest([Range(1, 5)] int Rating, [StringLength(1000)] string? Comment = null);
}
