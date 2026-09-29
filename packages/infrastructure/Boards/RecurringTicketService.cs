using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Boards;

/// <summary>
/// Recurring tickets. A schedule raises its ticket through the same path a person raising it by hand
/// takes — <see cref="IInternalTicketService"/> — so the number, topic defaults, SLA plan and activity
/// record are exactly what they would have been, and nothing about a scheduled ticket is special once
/// it exists.
/// </summary>
public sealed class RecurringTicketService(
    DeskDbContext db,
    ITenantContext tenant,
    IInternalTicketService tickets,
    IAuditWriter audit,
    TimeProvider clock) : IRecurringTicketService
{
    private const int MaxChecklistItems = 50;
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<RecurringTicketDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.RecurringTickets.AsNoTracking();
        if (!includeInactive) q = q.Where(r => r.IsActive);
        var rows = await q
            .OrderBy(r => r.NextRunAt)
            .Select(r => new
            {
                R = r,
                BoardName = db.Boards.Where(b => b.Id == r.BoardId).Select(b => b.Name).FirstOrDefault(),
                Topic = db.BoardTopics.Where(t => t.Id == r.BoardTopicId).Select(t => t.Name).FirstOrDefault(),
                Assignee = db.AppUsers.Where(u => u.Id == r.AssignedAppUserId).Select(u => u.DisplayName).FirstOrDefault(),
                LastNumber = db.Tickets.Where(t => t.Id == r.LastTicketId).Select(t => t.Number).FirstOrDefault(),
                CreatedBy = db.AppUsers.Where(u => u.Id == r.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(ct);
        // Described in memory: the words are built from the enum, which the database has no business doing.
        return rows.Select(x => new RecurringTicketDto(
            x.R.Id, x.R.BoardId, x.BoardName ?? "(board removed)", x.R.Title, x.R.Description,
            x.R.BoardTopicId, x.Topic, x.R.Priority, x.R.DepartmentId, x.R.AssignedAppUserId, x.Assignee,
            x.R.ClientCompanyId, x.R.Checklist, x.R.Frequency, x.R.DayOfWeek, x.R.DayOfMonth, x.R.Hour,
            x.R.SkipIfOpen, x.R.IsActive, x.R.NextRunAt, x.R.LastRunAt, x.R.LastTicketId, x.LastNumber,
            x.R.LastOutcome, RecurrenceSchedule.Describe(x.R), x.CreatedBy)).ToList();
    }

    public async Task<RecurringTicketDto> SaveAsync(Guid? id, RecurringTicketInput input, Guid appUserId, CancellationToken ct = default)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length is 0 or > 500) throw new ValidationFailedException("Give the ticket a title of up to 500 characters.");
        if (input.Hour is < 0 or > 23) throw new ValidationFailedException("Pick an hour between 00:00 and 23:00.");
        if (input.DayOfWeek is < 0 or > 6) throw new ValidationFailedException("Pick a day of the week.");
        // 29-31 would silently skip the months without one; the last day of the month is offered instead.
        if (input.DayOfMonth is < 0 or > 28) throw new ValidationFailedException("Pick a day between 1 and 28, or the last day of the month.");
        if (!Enum.IsDefined(input.Frequency)) throw new ValidationFailedException("Pick how often it repeats.");

        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == input.BoardId, ct)
            ?? throw new ValidationFailedException("That board does not exist.");
        if (!board.IsActive) throw new ValidationFailedException("That board is closed, so nothing can be scheduled on it.");
        if (input.BoardTopicId is { } topicId
            && !await db.BoardTopics.AnyAsync(t => t.Id == topicId && t.BoardId == board.Id && t.IsActive, ct))
            throw new ValidationFailedException("That topic is not an active topic on this board.");
        if (input.AssignedAppUserId is { } who && !await db.AppUsers.AnyAsync(u => u.Id == who && u.IsActive, ct))
            throw new ValidationFailedException("That person is not an active member of staff.");
        if (input.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.IsActive, ct))
            throw new ValidationFailedException("That department does not exist, or is closed.");
        if (input.ClientCompanyId is { } company && !await db.ClientCompanies.AnyAsync(c => c.Id == company, ct))
            throw new ValidationFailedException("That client does not exist.");
        var checklist = Lines(input.Checklist);
        if (checklist.Count > MaxChecklistItems)
            throw new ValidationFailedException($"A checklist holds up to {MaxChecklistItems} steps.");
        if (checklist.Any(l => l.Length > 300)) throw new ValidationFailedException("Each checklist step is up to 300 characters.");

        var item = id is { } existing
            ? await db.RecurringTickets.FirstOrDefaultAsync(r => r.Id == existing, ct) ?? throw new NotFoundException("Recurring ticket")
            : new RecurringTicket { MspOrganizationId = Org, Title = title, CreatedByUserId = appUserId };
        item.BoardId = board.Id;
        item.Title = title;
        item.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        item.BoardTopicId = input.BoardTopicId;
        item.Priority = string.IsNullOrWhiteSpace(input.Priority) ? null : input.Priority.Trim().ToUpperInvariant();
        item.DepartmentId = input.DepartmentId;
        item.AssignedAppUserId = input.AssignedAppUserId;
        item.ClientCompanyId = input.ClientCompanyId;
        item.Checklist = checklist.Count == 0 ? null : string.Join('\n', checklist);
        item.Frequency = input.Frequency;
        item.DayOfWeek = input.DayOfWeek;
        item.DayOfMonth = input.DayOfMonth;
        item.Hour = input.Hour;
        item.SkipIfOpen = input.SkipIfOpen;
        // Whoever last saved it takes it over: the tickets are raised in their name, and a schedule
        // still raising work as someone who has since left is the thing that goes quietly wrong.
        item.CreatedByUserId = appUserId;
        // Rescheduled from now on every save, so an edited schedule never fires on its old time.
        var (zone, _) = await SlaPlanner.CalendarAsync(db, Org, ct);
        item.NextRunAt = RecurrenceSchedule.Next(item, clock.GetUtcNow(), zone);

        if (id is null) db.RecurringTickets.Add(item);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "recurring.created" : "recurring.updated", "RecurringTicket", item.Id.ToString(),
            new { item.Title, item.Frequency, item.Hour, board = board.Name }, ct);
        return (await ListAsync(includeInactive: true, ct)).First(r => r.Id == item.Id);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var item = await db.RecurringTickets.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Recurring ticket");
        item.IsActive = active;
        if (active)
        {
            // Resumed from now: a schedule paused for a month must not fire the moment it is switched
            // back on because its old time is long past.
            var (zone, _) = await SlaPlanner.CalendarAsync(db, item.MspOrganizationId, ct);
            item.NextRunAt = RecurrenceSchedule.Next(item, clock.GetUtcNow(), zone);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "recurring.resumed" : "recurring.paused", "RecurringTicket", id.ToString(), new { item.Title }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.RecurringTickets.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Recurring ticket");
        db.RecurringTickets.Remove(item);
        await db.SaveChangesAsync(ct);
        // The tickets it raised stay exactly as they are; only the schedule goes.
        await audit.WriteAsync("recurring.deleted", "RecurringTicket", id.ToString(), new { item.Title }, ct);
    }

    public async Task<RecurringRunResult> RunNowAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.RecurringTickets.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Recurring ticket");
        // Asked for by a person, so "skip if the last one is open" does not apply: they can see it.
        return await RaiseAsync(item, advance: false, honourSkip: false, ct);
    }

    public async Task<RecurringRunResult?> RunIfDueAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.RecurringTickets.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (item is null || !item.IsActive || item.NextRunAt > clock.GetUtcNow()) return null;
        return await RaiseAsync(item, advance: true, honourSkip: true, ct);
    }

    private async Task<RecurringRunResult> RaiseAsync(RecurringTicket item, bool advance, bool honourSkip, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        RecurringRunResult result;

        var last = item.LastTicketId is { } lastId
            ? await db.Tickets.AsNoTracking().Where(t => t.Id == lastId).Select(t => new { t.Number, t.PortalStatus }).FirstOrDefaultAsync(ct)
            : null;
        var creator = await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == item.CreatedByUserId, ct);

        if (honourSkip && item.SkipIfOpen && last is not null && !TicketStatusRules.Finished(last.PortalStatus))
        {
            result = new RecurringRunResult($"Skipped — {last.Number} from last time is still open.", null, null);
        }
        else if (creator is null || !creator.IsActive)
        {
            result = new RecurringRunResult(
                "Not raised — the person who set this up is no longer active. Edit and save it to take it over.", null, null);
        }
        else
        {
            try
            {
                var created = await tickets.CreateAsync(creator.Id, new InternalTicketInput(
                    item.BoardId, item.Title, item.Description, item.Priority,
                    ClientCompanyId: item.ClientCompanyId, AssignedAppUserId: item.AssignedAppUserId,
                    BoardTopicId: item.BoardTopicId, DepartmentId: item.DepartmentId), ct);

                var order = 10;
                foreach (var step in Lines(item.Checklist))
                {
                    db.TicketTasks.Add(new TicketTask
                    {
                        MspOrganizationId = item.MspOrganizationId, TicketId = created.TicketId, Title = step,
                        SortOrder = order, CreatedByUserId = creator.Id,
                    });
                    order += 10;
                }
                item.LastTicketId = created.TicketId;
                result = new RecurringRunResult($"Raised {created.Number}.", created.TicketId, created.Number);
            }
            catch (DeskException ex)
            {
                // A closed board, a retired topic: said in the schedule's own words so a lead can fix
                // it, and the schedule moves on rather than retrying the same failure every few minutes.
                result = new RecurringRunResult($"Not raised — {ex.Message}", null, null);
            }
        }

        item.LastRunAt = now;
        item.LastOutcome = result.Outcome.Length > 500 ? result.Outcome[..500] : result.Outcome;
        if (advance)
        {
            var (zone, _) = await SlaPlanner.CalendarAsync(db, item.MspOrganizationId, ct);
            // From now, not from the missed time: a worker that was down over a weekend raises one
            // late ticket, not one for every occurrence it slept through.
            item.NextRunAt = RecurrenceSchedule.Next(item, now, zone);
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static List<string> Lines(string? text)
        => (text ?? "").Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•').Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>
/// Runs every recurring ticket that has fallen due, across every organization. Each runs in a scope
/// of its own with that organization's tenant set, so one tenant's schedule can only ever raise a
/// ticket on its own boards, and one failing schedule cannot stop the rest.
/// </summary>
public sealed class RecurringTicketRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RecurringTicketRunner> logger)
    : IRecurringTicketRunner
{
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        List<(Guid Id, Guid Org)> due;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            due = (await db.RecurringTickets.AsNoTracking()
                    .Where(r => r.IsActive && r.NextRunAt <= now)
                    .OrderBy(r => r.NextRunAt)
                    .Select(r => new { r.Id, r.MspOrganizationId })
                    .Take(200)
                    .ToListAsync(ct))
                .Select(r => (r.Id, r.MspOrganizationId)).ToList();
        }

        var ran = 0;
        foreach (var (id, org) in due)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<IRecurringTicketService>().RunIfDueAsync(id, ct);
                if (result is null) continue;
                ran++;
                logger.LogInformation("Recurring ticket {Id}: {Outcome}", id, result.Outcome);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recurring ticket {Id} failed", id);
            }
        }
        return ran;
    }
}
