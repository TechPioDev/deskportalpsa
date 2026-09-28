using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Application.Tickets;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>Replies the desk keeps, offered in the reply box and filled in from the ticket there.</summary>
public sealed class CannedResponseService(
    DeskDbContext db, ITenantContext tenant, ICurrentUser user, ITicketScopeQuery scopeQuery, IAuditWriter audit)
    : ICannedResponseService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<CannedResponseDto>> ListAsync(
        Guid? forTicketId = null, bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.CannedResponses.AsNoTracking();
        if (!includeInactive) q = q.Where(r => r.IsActive);

        if (forTicketId is { } ticketId)
        {
            // Resolved through the caller's own scope, so the list cannot be used to learn which
            // board a ticket they cannot see sits on.
            if (user.UserId is not { } uid) return [];
            var visible = await scopeQuery.VisibleAsync(db.Tickets, uid, Permissions.TicketsViewAll, ct);
            var boardId = await visible.Where(t => t.Id == ticketId).Select(t => t.BoardId).FirstOrDefaultAsync(ct);
            q = q.Where(r => r.BoardId == null || r.BoardId == boardId);
        }

        return await q
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new CannedResponseDto(
                r.Id, r.Name, r.Body, r.BoardId,
                db.Boards.Where(b => b.Id == r.BoardId).Select(b => b.Name).FirstOrDefault(),
                r.IsActive, r.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<CannedResponseDto> SaveAsync(Guid? id, CannedResponseInput input, CancellationToken ct = default)
    {
        var name = (input.Name ?? "").Trim();
        var body = (input.Body ?? "").Trim();
        if (name.Length is 0 or > 80) throw new ValidationFailedException("Give the response a name of up to 80 characters.");
        if (body.Length is 0 or > 10000) throw new ValidationFailedException("The response needs some text, up to 10,000 characters.");
        if (input.BoardId is { } board && !await db.Boards.AnyAsync(b => b.Id == board, ct))
            throw new ValidationFailedException("That board no longer exists.");

        var lowered = name.ToLowerInvariant();
        if (await db.CannedResponses.AnyAsync(r => r.BoardId == input.BoardId && r.Name.ToLower() == lowered && r.Id != id, ct))
            throw new ValidationFailedException($"There is already a response called {name} there.");

        var response = id is { } existing
            ? await db.CannedResponses.FirstOrDefaultAsync(r => r.Id == existing, ct) ?? throw new NotFoundException("Canned response")
            : new CannedResponse { MspOrganizationId = Org, Name = name, Body = body, CreatedByUserId = user.UserId };
        response.Name = name;
        response.Body = body;
        response.BoardId = input.BoardId;
        response.SortOrder = input.SortOrder;
        if (id is null) db.CannedResponses.Add(response);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "canned.created" : "canned.updated", "CannedResponse", response.Id.ToString(),
            new { response.Name, response.BoardId }, ct);
        return (await ListAsync(null, includeInactive: true, ct)).First(r => r.Id == response.Id);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var response = await db.CannedResponses.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Canned response");
        response.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "canned.activated" : "canned.retired", "CannedResponse", id.ToString(), new { response.Name }, ct);
    }
}
