using Desk.Application.Abstractions;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// The steps inside a ticket. Every call resolves the ticket through the caller's own scope first —
/// reading takes the right to see the ticket, changing takes the right to update it — so a task id
/// cannot be used to reach into a ticket the caller was never shown.
/// </summary>
public sealed class TicketTaskService(
    DeskDbContext db, ITenantContext tenant, ICurrentUser user, ITicketScopeQuery scopeQuery, TimeProvider clock)
    : ITicketTaskService
{
    private const int MaxPerTicket = 100;
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<TicketTaskDto>> ListAsync(Guid ticketId, CancellationToken ct = default)
    {
        await TicketAsync(ticketId, Permissions.TicketsViewAll, ct);
        return await ReadAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<TicketTaskDto>> AddAsync(Guid ticketId, string title, Guid? assignedAppUserId, CancellationToken ct = default)
    {
        var ticket = await TicketAsync(ticketId, Permissions.TicketsUpdate, ct);
        var clean = await ValidateAsync(title, assignedAppUserId, ct);
        var count = await db.TicketTasks.CountAsync(t => t.TicketId == ticketId, ct);
        if (count >= MaxPerTicket)
            throw new ValidationFailedException($"A ticket holds up to {MaxPerTicket} tasks. Work this big wants tickets of its own.");

        var last = await db.TicketTasks.Where(t => t.TicketId == ticketId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
        db.TicketTasks.Add(new TicketTask
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticketId,
            Title = clean,
            AssignedAppUserId = assignedAppUserId,
            SortOrder = last + 10,
            CreatedByUserId = user.UserId,
        });
        await db.SaveChangesAsync(ct);
        return await ReadAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<TicketTaskDto>> UpdateAsync(Guid taskId, string title, Guid? assignedAppUserId, CancellationToken ct = default)
    {
        var task = await TaskAsync(taskId, ct);
        task.Title = await ValidateAsync(title, assignedAppUserId, ct);
        task.AssignedAppUserId = assignedAppUserId;
        await db.SaveChangesAsync(ct);
        return await ReadAsync(task.TicketId, ct);
    }

    public async Task<IReadOnlyList<TicketTaskDto>> SetDoneAsync(Guid taskId, bool done, CancellationToken ct = default)
    {
        var task = await TaskAsync(taskId, ct);
        if (task.IsDone != done)
        {
            // Who ticked it and when: "done" with no name on it is a claim nobody can ask about.
            task.IsDone = done;
            task.DoneAt = done ? clock.GetUtcNow() : null;
            task.DoneByUserId = done ? user.UserId : null;
            await db.SaveChangesAsync(ct);
        }
        return await ReadAsync(task.TicketId, ct);
    }

    public async Task<IReadOnlyList<TicketTaskDto>> MoveAsync(Guid taskId, int offset, CancellationToken ct = default)
    {
        var task = await TaskAsync(taskId, ct);
        var list = await db.TicketTasks.Where(t => t.TicketId == task.TicketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var from = list.FindIndex(t => t.Id == taskId);
        var to = Math.Clamp(from + Math.Sign(offset), 0, list.Count - 1);
        if (from != to)
        {
            list.RemoveAt(from);
            list.Insert(to, task);
            // Renumbered from scratch in steps of ten: swapping two numbers breaks the moment two
            // tasks share one, and a clean sequence costs a handful of rows at most.
            for (var i = 0; i < list.Count; i++) list[i].SortOrder = (i + 1) * 10;
            await db.SaveChangesAsync(ct);
        }
        return await ReadAsync(task.TicketId, ct);
    }

    public async Task<IReadOnlyList<TicketTaskDto>> DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await TaskAsync(taskId, ct);
        db.TicketTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return await ReadAsync(task.TicketId, ct);
    }

    private async Task<Ticket> TicketAsync(Guid ticketId, string permission, CancellationToken ct)
    {
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        return await scopeQuery.FindAsync(db.Tickets, ticketId, uid, permission, ct) ?? throw new NotFoundException("Ticket");
    }

    private async Task<TicketTask> TaskAsync(Guid taskId, CancellationToken ct)
    {
        var task = await db.TicketTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw new NotFoundException("Task");
        // The ticket, through the caller's scope, before the task is touched. Not-found either way,
        // so a task id cannot confirm that a ticket the caller cannot see exists.
        await TicketAsync(task.TicketId, Permissions.TicketsUpdate, ct);
        return task;
    }

    private async Task<string> ValidateAsync(string title, Guid? assignee, CancellationToken ct)
    {
        var clean = (title ?? "").Trim();
        if (clean.Length is 0 or > 300) throw new ValidationFailedException("A task needs a title of up to 300 characters.");
        if (assignee is { } who && !await db.AppUsers.AnyAsync(u => u.Id == who && u.IsActive, ct))
            throw new ValidationFailedException("That person is not an active member of staff.");
        return clean;
    }

    private async Task<IReadOnlyList<TicketTaskDto>> ReadAsync(Guid ticketId, CancellationToken ct)
        => await db.TicketTasks.AsNoTracking()
            .Where(t => t.TicketId == ticketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new TicketTaskDto(
                t.Id, t.Title, t.IsDone, t.DoneAt,
                db.AppUsers.Where(u => u.Id == t.DoneByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                t.AssignedAppUserId,
                db.AppUsers.Where(u => u.Id == t.AssignedAppUserId).Select(u => u.DisplayName).FirstOrDefault(),
                t.SortOrder, t.CreatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// Open tasks on a ticket, for the rule that a ticket with work left on it cannot be closed from
    /// the portal. Static so the status endpoint can ask without owning this service.
    /// </summary>
    public static Task<int> OpenCountAsync(DeskDbContext db, Guid ticketId, CancellationToken ct)
        => db.TicketTasks.CountAsync(t => t.TicketId == ticketId && !t.IsDone, ct);
}
