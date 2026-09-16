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
/// Client-portal ticket endpoints. Every action resolves the caller's client identity and is scoped
/// to their company (and their own tickets unless they are a company administrator). Detail returns
/// only the public conversation — internal PSA notes are never exposed.
/// </summary>
[ApiController]
[Route("api/tickets")]
[Authorize]
public sealed class TicketsController(
    ICurrentUser user,
    IClientAccessResolver accessResolver,
    ITicketReadService reads,
    ITicketCommandService commands) : ControllerBase
{
    /// <summary>
    /// Staff with tickets.view.all see the whole tenant — every company, every connection. Client
    /// users see their company's tickets. Staff-first: the local dev admin is both, and an admin
    /// looking at one company's slice concluded an entire PSA was missing from the portal.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => user.HasPermission(Permissions.TicketsViewAll)
            ? Ok(await reads.ListAllAsync(ct))
            : Ok(await reads.ListAsync(await AccessAsync(ct), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var detail = user.HasPermission(Permissions.TicketsViewAll)
            ? await reads.GetDetailForStaffAsync(id, ct)
            : await reads.GetDetailAsync(await AccessAsync(ct), id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequest req, CancellationToken ct)
    {
        var result = await commands.CreateAsync(await AccessAsync(ct),
            new CreateTicketInput(req.Title, req.Description, req.Priority, req.Category, req.QueueOrBoard), ct);
        return CreatedAtAction(nameof(Detail), new { id = result.Id }, result);
    }

    /// <summary>
    /// A client replies to their own company's tickets; staff with view-all reply to any ticket in
    /// the tenant, attributed as a technician. Client-scoped resolution is tried FIRST so the dual
    /// dev identity still posts as the client on its own company's tickets.
    /// </summary>
    [HttpPost("{id:guid}/comments")]
    [RequirePermission(Permissions.TicketsAddPublicNote)]
    public async Task<IActionResult> Comment(Guid id, [FromBody] AddCommentRequest req, CancellationToken ct)
    {
        var access = await accessResolver.ResolveAsync(user.Subject ?? "", ct);
        if (access is not null)
        {
            try { return Ok(await commands.AddCommentAsync(access, id, req.Body, ct)); }
            catch (NotFoundException) when (user.HasPermission(Permissions.TicketsViewAll))
            { /* not their company's ticket — fall through to the staff path */ }
        }
        if (!user.HasPermission(Permissions.TicketsViewAll))
            throw new ForbiddenException("This endpoint is for client portal users.");
        if (user.UserId is not { } uid)
            throw new ForbiddenException("This endpoint is for client portal users.");
        // isPublic reaches only this STAFF path — the client branch above never passes it, so a
        // client cannot post (or request) an internal note.
        return Ok(await commands.AddStaffCommentAsync(
            uid, user.DisplayName ?? user.Email ?? "Staff", id, req.Body, req.IsPublic ?? true,
            req.EmailContact ?? false, req.EmailCc ?? [], ct));
    }

    /// <summary>
    /// Who a public reply on this ticket can be sent to. Staff only: the list is the customer's
    /// contacts, and a client has no business enumerating their colleagues' addresses here.
    /// </summary>
    [HttpGet("{id:guid}/recipients")]
    [RequirePermission(Permissions.TicketsViewAll)]
    public async Task<IActionResult> Recipients(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new ForbiddenException("Staff only.");
        return Ok(await commands.ListReplyRecipientsAsync(uid, id, ct));
    }

    [HttpPost("{id:guid}/contact/refresh")]
    [RequirePermission(Permissions.TicketsViewAll)]
    public async Task<IActionResult> RefreshContact(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new ForbiddenException("Staff only.");
        return Ok(new { hasReachableContact = await commands.RefreshContactAsync(uid, id, ct) });
    }

    // Resolves the client identity or refuses the request — staff use the dashboard endpoints instead.
    private async Task<ClientAccess> AccessAsync(CancellationToken ct)
        => await accessResolver.ResolveAsync(user.Subject ?? "", ct)
           ?? throw new ForbiddenException("This endpoint is for client portal users.");

    public sealed record CreateTicketRequest(
        [Required, StringLength(500, MinimumLength = 3)] string Title,
        string? Description,
        string? Priority,
        string? Category,
        string? QueueOrBoard);

    public sealed record AddCommentRequest(
        [Required, StringLength(10000, MinimumLength = 1)] string Body,
        // Staff only; the client path ignores it. Null means public.
        bool? IsPublic = null,
        // Staff only, public notes only. Recipients are re-validated server-side against the
        // ticket's own company — a request naming someone else's contact is refused, not filtered.
        bool? EmailContact = null,
        [MaxLength(25)] IReadOnlyList<string>? EmailCc = null);
}
