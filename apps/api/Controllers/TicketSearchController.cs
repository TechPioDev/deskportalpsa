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
/// Search, saved views and followers — the three things that turn a list of tickets into a desk
/// somebody can work all day.
///
/// Search answers as the caller: staff get the tenant within their own view scope, a client gets
/// their company's tickets, and both go through the one visibility rule the lists use. Nothing here
/// reaches a PSA; it is all reads over the portal's own projection.
/// </summary>
[Authorize]
[ApiController]
[Route("api/tickets")]
public sealed class TicketSearchController(
    ICurrentUser user,
    IClientAccessResolver accessResolver,
    ITicketReadService reads,
    ITicketFollowerService followers,
    ITicketViewService views) : ControllerBase
{
    /// <summary>
    /// Free-text search with the same filters the list page carries in its URL. Deliberately a GET
    /// with query parameters rather than a POST body: the result is then a link somebody can send to
    /// a colleague, which is most of the value of having a search at all.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] SearchRequest req, CancellationToken ct)
    {
        var query = new TicketQuery(
            Q: req.Q, BoardId: req.BoardId, DepartmentId: req.DepartmentId, TeamId: req.TeamId,
            ClientCompanyId: req.ClientCompanyId, Status: req.Status, Priority: req.Priority,
            Openness: req.Openness, MineOnly: req.Mine ?? false, FollowingOnly: req.Following ?? false,
            UnassignedOnly: req.Unassigned ?? false, OverdueOnly: req.Overdue ?? false,
            RaisedWithinDays: req.WithinDays,
            // Searching the conversation is the expensive half, so the caller asks for it. The
            // header search asks for it once the shorter search has been typed out; a two-letter
            // prefix does not need to read every note in the tenant to prove it matches everything.
            IncludeNotes: req.Notes ?? false,
            DueSoonOnly: req.DueSoon ?? false,
            Take: req.Take ?? 25);

        // Staff first, exactly as the list endpoint does: the local dev admin is both, and a staff
        // caller resolved as a client would search one company and conclude the rest had vanished.
        var access = user.SeesStaffTickets()
            ? null
            : await accessResolver.ResolveAsync(user.Subject ?? "", ct)
              ?? throw new ForbiddenException("This endpoint is for client portal users.");

        return Ok(await reads.SearchAsync(query, access, ct));
    }

    /// <summary>
    /// The ticket list, one page at a time, filtered in the database with the filters the list page
    /// shows. Replaces loading every ticket and filtering in the browser, which slows with every ticket
    /// the desk ever holds. Same caller rule as search: staff first, then the client's own company.
    /// </summary>
    [HttpGet("page")]
    public async Task<IActionResult> Page([FromQuery] PageRequest req, CancellationToken ct)
    {
        var query = new TicketQuery(
            Q: req.Q, BoardId: req.BoardId, Status: req.Status, Priority: req.Priority, Openness: req.Openness,
            MineOnly: req.Mine ?? false, FollowingOnly: req.Following ?? false, UnassignedOnly: req.Unassigned ?? false,
            OverdueOnly: req.Overdue ?? false, DueSoonOnly: req.DueSoon ?? false,
            CompanyName: req.Company, QueueName: req.Queue, ConnectionName: req.Source, PersonKey: req.Person,
            RaisedSince: req.From, Kind: req.Kind, Skip: req.Skip ?? 0, Take: req.Take ?? 50,
            ReviewPending: string.Equals(req.Review, "pending", StringComparison.OrdinalIgnoreCase));
        return Ok(await reads.PageAsync(query, await CallerAsync(ct), ct));
    }

    /// <summary>What the list's filters can be set to, across everything the caller can see.</summary>
    [HttpGet("facets")]
    public async Task<IActionResult> Facets(CancellationToken ct) => Ok(await reads.FacetsAsync(await CallerAsync(ct), ct));

    /// <summary>Tickets raised since a date (or ever), counted by status, priority and queue.</summary>
    [HttpGet("breakdown")]
    public async Task<IActionResult> Breakdown([FromQuery] DateTimeOffset? from, CancellationToken ct)
        => Ok(await reads.BreakdownAsync(from, await CallerAsync(ct), ct));

    /// <summary>Open work counted: all the caller can see, or with mine=true what they hold (My Work).</summary>
    [HttpGet("summary")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Summary([FromQuery] bool? mine, CancellationToken ct)
        => Ok(await reads.SummaryAsync(mine ?? false, ct));

    /// <summary>Open work per person, for balancing the team. Counts only tickets the caller can see.</summary>
    [HttpGet("workload")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Workload(CancellationToken ct) => Ok(await reads.WorkloadAsync(ct));

    /// <summary>Null for staff; the client's access otherwise. Anyone else is refused.</summary>
    private async Task<ClientAccess?> CallerAsync(CancellationToken ct)
        => user.SeesStaffTickets()
            ? null
            : await accessResolver.ResolveAsync(user.Subject ?? "", ct)
              ?? throw new ForbiddenException("This endpoint is for client portal users.");

    /// <summary>The ids of the tickets the caller follows, for the view that shows exactly those.</summary>
    [HttpGet("following")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Following(CancellationToken ct)
        => Ok(await followers.FollowedTicketIdsAsync(ct));

    [HttpGet("{id:guid}/followers")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Followers(Guid id, CancellationToken ct)
        => Ok(await followers.ListAsync(id, ct));

    /// <param name="req">A user id, or none at all to follow it yourself.</param>
    [HttpPost("{id:guid}/followers")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> AddFollower(Guid id, [FromBody] FollowerRequest? req, CancellationToken ct)
    {
        var appUserId = req?.AppUserId ?? user.UserId
            ?? throw new ForbiddenException("Following a ticket needs a portal user.");
        return Ok(await followers.AddAsync(id, appUserId, ct));
    }

    [HttpDelete("{id:guid}/followers/{appUserId:guid}")]
    [RequirePermission(Permissions.TicketsUpdate)]
    public async Task<IActionResult> RemoveFollower(Guid id, Guid appUserId, CancellationToken ct)
        => Ok(await followers.RemoveAsync(id, appUserId, ct));

    // ---- saved views ----------------------------------------------------------------------------
    // A saved view is a filter set with a name on it: it grants nothing, reaches nothing, and is
    // applied by the same scoped query as an unsaved filter. So viewing and saving take the
    // permission to READ tickets, not the one to change them.

    [HttpGet("views")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Views([FromQuery] Guid? boardId, CancellationToken ct)
        => Ok(await views.ListAsync(boardId, ct));

    [HttpPost("views")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> SaveView([FromBody] SaveViewRequest req, CancellationToken ct)
        => Ok(await views.SaveAsync(req.Id, req.Name, req.Shared ?? false, req.BoardId, req.Filters ?? new SavedViewFilters(), ct));

    [HttpDelete("views/{id:guid}")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> DeleteView(Guid id, CancellationToken ct)
    {
        await views.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <param name="Q">Free text: a ticket number, a provider reference, a subject, a customer, a requester.</param>
    /// <param name="Openness">"open" or "resolved" — a set of statuses rather than one.</param>
    /// <param name="Notes">Search the conversation as well. Slower, and worth it.</param>
    public sealed record SearchRequest(
        [StringLength(200)] string? Q = null,
        Guid? BoardId = null,
        Guid? DepartmentId = null,
        Guid? TeamId = null,
        Guid? ClientCompanyId = null,
        [StringLength(50)] string? Status = null,
        [StringLength(20)] string? Priority = null,
        [StringLength(20)] string? Openness = null,
        bool? Mine = null,
        bool? Following = null,
        bool? Unassigned = null,
        bool? Overdue = null,
        [Range(1, 3650)] int? WithinDays = null,
        bool? Notes = null,
        bool? DueSoon = null,
        [Range(1, 200)] int? Take = null);

    /// <param name="Company">A company by the NAME the list shows. Queue and Source likewise.</param>
    /// <param name="Person">A PersonKey (u:... or x:...): tickets that person holds or logged time on.</param>
    /// <param name="Kind">psa, internal or monitoring.</param>
    public sealed record PageRequest(
        [StringLength(200)] string? Q = null,
        Guid? BoardId = null,
        [StringLength(50)] string? Status = null,
        [StringLength(20)] string? Priority = null,
        [StringLength(20)] string? Openness = null,
        bool? Mine = null,
        bool? Following = null,
        bool? Unassigned = null,
        bool? Overdue = null,
        bool? DueSoon = null,
        [StringLength(200)] string? Company = null,
        [StringLength(200)] string? Queue = null,
        [StringLength(200)] string? Source = null,
        [StringLength(200)] string? Person = null,
        DateTimeOffset? From = null,
        [StringLength(20)] string? Kind = null,
        [Range(0, 1_000_000)] int? Skip = null,
        [Range(1, 200)] int? Take = null,
        [StringLength(20)] string? Review = null);

    public sealed record FollowerRequest(Guid? AppUserId = null);

    /// <param name="Id">Null to create; a view's id to change it. Only its owner may change it.</param>
    public sealed record SaveViewRequest(
        [Required, StringLength(60, MinimumLength = 1)] string Name,
        Guid? Id = null,
        bool? Shared = null,
        Guid? BoardId = null,
        SavedViewFilters? Filters = null);
}
