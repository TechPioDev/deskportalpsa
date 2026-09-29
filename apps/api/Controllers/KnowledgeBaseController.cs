using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Knowledge;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// The knowledge base. The team reads every article and writes them; a client reads the ones meant for
/// them beside their own FAQ, is offered some while typing a ticket, and can say one solved it.
/// </summary>
[ApiController]
[Authorize]
public sealed class KnowledgeBaseController(
    ICurrentUser user, IClientAccessResolver accessResolver, IKnowledgeBaseService kb) : ControllerBase
{
    // ---- Staff ----

    [HttpGet("api/kb")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> List([FromQuery] string? search, CancellationToken ct)
        => Ok(await kb.ListAsync(search, ct));

    [HttpGet("api/kb/stats")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Stats([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await kb.StatsAsync(days, ct));

    [HttpGet("api/kb/{id:guid}")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Ok(await kb.GetAsync(id, ct));

    [HttpPost("api/kb")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Create([FromBody] ArticleBody body, CancellationToken ct)
        => Ok(await kb.SaveAsync(null, body.ToInput(), StaffId(), StaffName(), ct));

    [HttpPut("api/kb/{id:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ArticleBody body, CancellationToken ct)
        => Ok(await kb.SaveAsync(id, body.ToInput(), StaffId(), StaffName(), ct));

    [HttpDelete("api/kb/{id:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await kb.DeleteAsync(id, ct);
        return NoContent();
    }

    // ---- Client ----

    [HttpGet("api/client/help")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Help([FromQuery] string? search, CancellationToken ct)
        => Ok(await kb.HelpAsync(await AccessAsync(ct), search, ct));

    [HttpGet("api/client/help/suggest")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Suggest([FromQuery] string? q, CancellationToken ct)
        => Ok(await kb.SuggestAsync(await AccessAsync(ct), q, ct));

    [HttpGet("api/client/help/{source}/{id:guid}")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> HelpArticle(string source, Guid id, CancellationToken ct)
        => Ok(await kb.HelpArticleAsync(await AccessAsync(ct), source, id, ct));

    [HttpPost("api/client/help/{source}/{id:guid}/solved")]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> Solved(string source, Guid id, [FromBody] SolvedBody body, CancellationToken ct)
    {
        await kb.SolvedAsync(await AccessAsync(ct), source, id, body.Query, ct);
        return NoContent();
    }

    private Guid StaffId() => user.UserId ?? throw new NotFoundException("Article");

    private string StaffName() => user.DisplayName ?? user.Email ?? "Staff";

    private async Task<ClientAccess> AccessAsync(CancellationToken ct)
        => await accessResolver.ResolveAsync(user.Subject ?? "", ct)
           ?? throw new ForbiddenException("The help pages are for the client's own users.");

    public sealed record ArticleBody(
        [Required, StringLength(200)] string Title, [StringLength(20000)] string? Body, [StringLength(60)] string? Category,
        [Required] string Audience, IReadOnlyList<Guid>? ClientIds, bool IsPublished)
    {
        public KbArticleInput ToInput() => new(Title, Body, Category, Audience, ClientIds, IsPublished);
    }

    public sealed record SolvedBody([StringLength(500)] string? Query);
}
