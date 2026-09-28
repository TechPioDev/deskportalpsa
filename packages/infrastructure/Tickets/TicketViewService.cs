using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Saved filter sets. A caller sees their own views and anything a colleague shared, and may change
/// only their own — sharing a view offers it to the team, it does not hand it over.
///
/// The built-in views (open, mine, unassigned, overdue, following, closed) are code rather than
/// seeded rows: they mean the same thing on every desk, nobody should be able to delete them, and a
/// seeded row would drift per tenant the first time somebody edited it.
/// </summary>
public sealed class TicketViewService(DeskDbContext db, ICurrentUser user, ITenantContext tenant) : ITicketViewService
{
    private const int MaxPerUser = 40;

    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<SavedViewDto>> ListAsync(Guid? boardId = null, CancellationToken ct = default)
    {
        if (user.UserId is not { } uid) return [];
        return await db.SavedTicketViews.AsNoTracking()
            .Where(v => v.BoardId == boardId && (v.OwnerUserId == uid || v.Shared))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name)
            .Select(v => new SavedViewDto(
                v.Id, v.Name, v.Shared, v.OwnerUserId == uid,
                db.AppUsers.Where(u => u.Id == v.OwnerUserId).Select(u => u.DisplayName).FirstOrDefault(),
                v.BoardId,
                new SavedViewFilters(
                    v.Search, v.Status, v.Priority, v.Company, v.Queue, v.ConnectionName, v.PersonKey,
                    v.DepartmentId, v.TeamId, v.Openness,
                    v.MineOnly, v.FollowingOnly, v.UnassignedOnly, v.OverdueOnly, v.RaisedWithinDays),
                v.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<SavedViewDto> SaveAsync(
        Guid? id, string name, bool shared, Guid? boardId, SavedViewFilters filters, CancellationToken ct = default)
    {
        if (user.UserId is not { } uid) throw new ForbiddenException("Saved views belong to a portal user.");
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 60) throw new ValidationFailedException("Give the view a name of up to 60 characters.");

        // A view that narrows by nothing is the unfiltered list under another name, and a list of
        // those is how the view bar stops being useful.
        if (!Narrows(filters)) throw new ValidationFailedException("Set at least one filter before saving the view.");

        if (boardId is { } b && !await db.Boards.AnyAsync(x => x.Id == b, ct))
            throw new ValidationFailedException("That board no longer exists.");

        var view = id is { } existing
            ? await db.SavedTicketViews.FirstOrDefaultAsync(v => v.Id == existing, ct)
                ?? throw new NotFoundException("Saved view")
            : new SavedTicketView { MspOrganizationId = Org, Name = name, OwnerUserId = uid };

        // A shared view is offered to everyone but still belongs to whoever made it: a colleague
        // editing it would silently change the view under everybody else who uses it.
        if (view.OwnerUserId != uid) throw new ForbiddenException("This view belongs to somebody else.");

        var lowered = name.ToLowerInvariant();
        if (await db.SavedTicketViews.AnyAsync(
                v => v.OwnerUserId == uid && v.BoardId == boardId && v.Id != view.Id && v.Name.ToLower() == lowered, ct))
            throw new ValidationFailedException($"You already have a view called “{name}” here.");

        if (id is null && await db.SavedTicketViews.CountAsync(v => v.OwnerUserId == uid, ct) >= MaxPerUser)
            throw new ValidationFailedException($"You already have {MaxPerUser} saved views. Delete one to add another.");

        view.Name = name;
        view.Shared = shared;
        view.BoardId = boardId;
        view.Search = Trim(filters.Search, 200);
        view.Status = Trim(filters.Status, 50);
        view.Priority = Trim(filters.Priority, 20);
        view.Company = Trim(filters.Company, 300);
        view.Queue = Trim(filters.Queue, 200);
        view.ConnectionName = Trim(filters.ConnectionName, 200);
        view.PersonKey = Trim(filters.PersonKey, 80);
        view.DepartmentId = filters.DepartmentId;
        view.TeamId = filters.TeamId;
        // Anything else would be applied by neither the list nor the search, so it would read as a
        // filter that quietly does nothing.
        view.Openness = filters.Openness is "open" or "resolved" ? filters.Openness : null;
        view.MineOnly = filters.MineOnly;
        view.FollowingOnly = filters.FollowingOnly;
        view.UnassignedOnly = filters.UnassignedOnly;
        view.OverdueOnly = filters.OverdueOnly;
        view.RaisedWithinDays = filters.RaisedWithinDays is { } d && d > 0 ? Math.Min(d, 3650) : null;

        if (id is null) db.SavedTicketViews.Add(view);
        await db.SaveChangesAsync(ct);

        return (await ListAsync(boardId, ct)).First(v => v.Id == view.Id);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (user.UserId is not { } uid) throw new ForbiddenException("Saved views belong to a portal user.");
        var view = await db.SavedTicketViews.FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new NotFoundException("Saved view");
        if (view.OwnerUserId != uid) throw new ForbiddenException("This view belongs to somebody else.");
        db.SavedTicketViews.Remove(view);
        await db.SaveChangesAsync(ct);
    }

    private static bool Narrows(SavedViewFilters f) =>
        !string.IsNullOrWhiteSpace(f.Search) || !string.IsNullOrWhiteSpace(f.Status)
        || !string.IsNullOrWhiteSpace(f.Priority) || !string.IsNullOrWhiteSpace(f.Company)
        || !string.IsNullOrWhiteSpace(f.Queue) || !string.IsNullOrWhiteSpace(f.ConnectionName)
        || !string.IsNullOrWhiteSpace(f.PersonKey) || !string.IsNullOrWhiteSpace(f.Openness)
        || f.DepartmentId is not null || f.TeamId is not null
        || f.MineOnly || f.FollowingOnly || f.UnassignedOnly || f.OverdueOnly
        || f.RaisedWithinDays is > 0;

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }
}
