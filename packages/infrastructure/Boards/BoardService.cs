using System.Text.RegularExpressions;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// The team's own boards. Every change is audited, because a board's visibility decides whether a
/// customer can read work recorded against them, and that is not a setting to change unnoticed.
/// </summary>
public sealed partial class BoardService(DeskDbContext db, ITenantContext tenant, IAuditWriter audit) : IBoardService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,7}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<BoardDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.Boards.AsNoTracking();
        if (!includeInactive) q = q.Where(b => b.IsActive);
        return await q
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new BoardDto(
                b.Id, b.Name, b.Key, b.Description, b.Kind, b.ClientVisible, b.IsActive, b.SortOrder,
                db.BoardMembers.Count(m => m.BoardId == b.Id),
                db.Tickets.Count(t => t.BoardId == b.Id && t.ClosedAt == null)))
            .ToListAsync(ct);
    }

    public async Task<BoardDto> CreateAsync(BoardInput input, CancellationToken ct = default)
    {
        var (name, key) = Validate(input);
        if (await db.Boards.AnyAsync(b => b.Key == key, ct))
            throw new ValidationFailedException($"Another board already uses the prefix {key}.");

        var board = new Board
        {
            MspOrganizationId = Org,
            Name = name,
            Key = key,
            Description = Blank(input.Description),
            Kind = input.Kind,
            // An internal board is the team's own record and is never published to a customer,
            // whatever the request asked for. Only an RMM board can be shown to the client it names.
            ClientVisible = input.Kind == BoardKind.Rmm && input.ClientVisible,
            SortOrder = input.SortOrder,
        };
        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("board.created", "Board", board.Id.ToString(),
            new { board.Name, board.Key, board.Kind, board.ClientVisible }, ct);
        return await OneAsync(board.Id, ct);
    }

    public async Task<BoardDto> UpdateAsync(Guid boardId, BoardInput input, CancellationToken ct = default)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct) ?? throw new NotFoundException("Board");
        var (name, key) = Validate(input);
        if (!string.Equals(board.Key, key, StringComparison.Ordinal))
        {
            // The prefix is printed on every ticket already raised here, and those numbers are how
            // people refer to the work. Changing it would leave the board and its tickets disagreeing.
            if (await db.Tickets.AnyAsync(t => t.BoardId == boardId, ct))
                throw new ValidationFailedException("This board already has tickets, so its prefix cannot change.");
            if (await db.Boards.AnyAsync(b => b.Key == key && b.Id != boardId, ct))
                throw new ValidationFailedException($"Another board already uses the prefix {key}.");
            board.Key = key;
        }

        var wasClientVisible = board.ClientVisible;
        board.Name = name;
        board.Description = Blank(input.Description);
        board.SortOrder = input.SortOrder;
        board.ClientVisible = board.Kind == BoardKind.Rmm && input.ClientVisible;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("board.updated", "Board", board.Id.ToString(),
            new { board.Name, board.Key, board.ClientVisible, clientVisibilityChanged = wasClientVisible != board.ClientVisible }, ct);
        return await OneAsync(board.Id, ct);
    }

    public async Task SetActiveAsync(Guid boardId, bool active, CancellationToken ct = default)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct) ?? throw new NotFoundException("Board");
        board.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "board.activated" : "board.deactivated", "Board", board.Id.ToString(), new { board.Name }, ct);
    }

    public async Task<IReadOnlyList<BoardMemberDto>> MembersAsync(Guid boardId, CancellationToken ct = default)
        => await db.BoardMembers.AsNoTracking()
            .Where(m => m.BoardId == boardId)
            .Join(db.AppUsers.AsNoTracking(), m => m.AppUserId, u => u.Id,
                (m, u) => new BoardMemberDto(u.Id, u.DisplayName, u.Email))
            .OrderBy(m => m.DisplayName)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BoardMemberDto>> SetMembersAsync(Guid boardId, IReadOnlyList<Guid> appUserIds, CancellationToken ct = default)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct) ?? throw new NotFoundException("Board");
        var wanted = appUserIds.Distinct().ToList();
        var known = await db.AppUsers.AsNoTracking().Where(u => wanted.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        if (known.Count != wanted.Count)
            throw new ValidationFailedException("One of those people is not an active member of staff.");

        var existing = await db.BoardMembers.Where(m => m.BoardId == boardId).ToListAsync(ct);
        db.BoardMembers.RemoveRange(existing.Where(m => !known.Contains(m.AppUserId)));
        foreach (var id in known.Where(id => existing.All(m => m.AppUserId != id)))
            db.BoardMembers.Add(new BoardMember { MspOrganizationId = Org, BoardId = boardId, AppUserId = id });
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("board.members.set", "Board", boardId.ToString(),
            new { board.Name, members = known.Count, openToEveryone = known.Count == 0 }, ct);
        return await MembersAsync(boardId, ct);
    }

    private async Task<BoardDto> OneAsync(Guid boardId, CancellationToken ct)
        => (await ListAsync(includeInactive: true, ct)).First(b => b.Id == boardId);

    private static (string Name, string Key) Validate(BoardInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 120) throw new ValidationFailedException("Give the board a name of up to 120 characters.");
        var key = (input.Key ?? "").Trim().ToUpperInvariant();
        if (!KeyPattern().IsMatch(key))
            throw new ValidationFailedException("The prefix is 2 to 8 letters or digits, starting with a letter, for example INT.");
        return (name, key);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
