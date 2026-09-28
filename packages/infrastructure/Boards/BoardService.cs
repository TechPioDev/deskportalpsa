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

    public async Task<IReadOnlyList<BoardTopicDto>> TopicsAsync(Guid boardId, bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.BoardTopics.AsNoTracking().Where(t => t.BoardId == boardId);
        if (!includeInactive) q = q.Where(t => t.IsActive);
        return await q
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new BoardTopicDto(
                t.Id, t.BoardId, t.Name, t.DefaultDepartmentId,
                db.Departments.Where(d => d.Id == t.DefaultDepartmentId).Select(d => d.Name).FirstOrDefault(),
                t.DefaultPriority, t.DefaultAssigneeUserId,
                db.AppUsers.Where(u => u.Id == t.DefaultAssigneeUserId).Select(u => u.DisplayName).FirstOrDefault(),
                t.DueInHours, t.IsActive, t.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<BoardTopicDto> AddTopicAsync(Guid boardId, BoardTopicInput input, CancellationToken ct = default)
    {
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Id == boardId, ct) ?? throw new NotFoundException("Board");
        var name = await ValidateTopicAsync(input, boardId, null, ct);

        var topic = new BoardTopic
        {
            MspOrganizationId = Org,
            BoardId = board.Id,
            Name = name,
            DefaultDepartmentId = input.DefaultDepartmentId,
            DefaultPriority = Priority(input.DefaultPriority),
            DefaultAssigneeUserId = input.DefaultAssigneeUserId,
            DueInHours = input.DueInHours,
            SortOrder = input.SortOrder,
        };
        db.BoardTopics.Add(topic);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("board.topic.added", "Board", board.Id.ToString(), new { Board = board.Name, Topic = topic.Name }, ct);
        return (await TopicsAsync(boardId, includeInactive: true, ct)).First(t => t.Id == topic.Id);
    }

    public async Task<BoardTopicDto> UpdateTopicAsync(Guid topicId, BoardTopicInput input, CancellationToken ct = default)
    {
        var topic = await db.BoardTopics.FirstOrDefaultAsync(t => t.Id == topicId, ct) ?? throw new NotFoundException("Topic");
        topic.Name = await ValidateTopicAsync(input, topic.BoardId, topicId, ct);
        topic.DefaultDepartmentId = input.DefaultDepartmentId;
        topic.DefaultPriority = Priority(input.DefaultPriority);
        topic.DefaultAssigneeUserId = input.DefaultAssigneeUserId;
        topic.DueInHours = input.DueInHours;
        topic.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("board.topic.updated", "Board", topic.BoardId.ToString(), new { topic.Name }, ct);
        return (await TopicsAsync(topic.BoardId, includeInactive: true, ct)).First(t => t.Id == topicId);
    }

    public async Task SetTopicActiveAsync(Guid topicId, bool active, CancellationToken ct = default)
    {
        var topic = await db.BoardTopics.FirstOrDefaultAsync(t => t.Id == topicId, ct) ?? throw new NotFoundException("Topic");
        topic.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "board.topic.activated" : "board.topic.retired", "Board",
            topic.BoardId.ToString(), new { topic.Name }, ct);
    }

    private async Task<string> ValidateTopicAsync(BoardTopicInput input, Guid boardId, Guid? self, CancellationToken ct)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 120) throw new ValidationFailedException("Give the topic a name of up to 120 characters.");
        // Case-insensitively: "Patching" and "patching" in one list is a mistake waiting to be made,
        // not two kinds of work.
        var lowered = name.ToLowerInvariant();
        if (await db.BoardTopics.AnyAsync(t => t.BoardId == boardId && t.Name.ToLower() == lowered && t.Id != self, ct))
            throw new ValidationFailedException($"This board already has a topic called {name}.");
        if (input.DefaultDepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept, ct))
            throw new ValidationFailedException("That department does not exist.");
        if (input.DefaultAssigneeUserId is { } who && !await db.AppUsers.AnyAsync(u => u.Id == who && u.IsActive, ct))
            throw new ValidationFailedException("That person is not an active member of staff.");
        if (input.DueInHours is < 1 or > 8760)
            throw new ValidationFailedException("A due time is between 1 hour and a year.");
        return name;
    }

    private static string? Priority(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

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
