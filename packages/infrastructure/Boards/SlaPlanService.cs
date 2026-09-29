using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Boards;

/// <summary>SLA plans, and the one place a plan becomes a ticket's due dates.</summary>
public sealed class SlaPlanService(DeskDbContext db, ITenantContext tenant, IAuditWriter audit) : ISlaPlanService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<IReadOnlyList<SlaPlanDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.SlaPlans.AsNoTracking();
        if (!includeInactive) q = q.Where(p => p.IsActive);
        return await q
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new SlaPlanDto(
                p.Id, p.Name, p.ResolveWithinHours, p.FirstResponseWithinHours, p.BusinessHoursOnly,
                p.WorkdayStartHour, p.WorkdayEndHour, p.WorkingDays, p.IsActive, p.SortOrder,
                db.Boards.Count(b => b.DefaultSlaPlanId == p.Id) + db.BoardTopics.Count(t => t.SlaPlanId == p.Id),
                p.SkipHolidays, p.PauseWhileWaiting))
            .ToListAsync(ct);
    }

    public async Task<SlaPlanDto> SaveAsync(Guid? id, SlaPlanInput input, CancellationToken ct = default)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 80) throw new ValidationFailedException("Give the plan a name of up to 80 characters.");
        if (input.ResolveWithinHours is < 1 or > 8760)
            throw new ValidationFailedException("Resolve within is between 1 hour and a year.");
        if (input.FirstResponseWithinHours is { } first && (first < 1 || first > input.ResolveWithinHours))
            throw new ValidationFailedException("A first reply is due within 1 hour and no later than the resolve time.");
        if (input.BusinessHoursOnly)
        {
            // Overnight windows (22:00-06:00) are a night shift, and a night shift is a 24x7 desk:
            // refused rather than silently read as a window that never opens.
            if (input.WorkdayStartHour is < 0 or > 23 || input.WorkdayEndHour is < 1 or > 24
                || input.WorkdayStartHour >= input.WorkdayEndHour)
                throw new ValidationFailedException("The working day must start before it ends, within one day. For shifts through the night, use round the clock.");
            if ((input.WorkingDays & 0b1111111) == 0)
                throw new ValidationFailedException("Pick at least one working day.");
        }

        var lowered = name.ToLowerInvariant();
        if (await db.SlaPlans.AnyAsync(p => p.Name.ToLower() == lowered && p.Id != id, ct))
            throw new ValidationFailedException($"There is already a plan called {name}.");

        var plan = id is { } existing
            ? await db.SlaPlans.FirstOrDefaultAsync(p => p.Id == existing, ct) ?? throw new NotFoundException("SLA plan")
            : new SlaPlan { MspOrganizationId = Org, Name = name };
        plan.Name = name;
        plan.ResolveWithinHours = input.ResolveWithinHours;
        plan.FirstResponseWithinHours = input.FirstResponseWithinHours;
        plan.BusinessHoursOnly = input.BusinessHoursOnly;
        plan.WorkdayStartHour = input.WorkdayStartHour;
        plan.WorkdayEndHour = input.WorkdayEndHour;
        plan.WorkingDays = input.WorkingDays & 0b1111111;
        plan.SkipHolidays = input.SkipHolidays;
        plan.PauseWhileWaiting = input.PauseWhileWaiting;
        plan.SortOrder = input.SortOrder;
        if (id is null) db.SlaPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(id is null ? "sla.plan.created" : "sla.plan.updated", "SlaPlan", plan.Id.ToString(),
            new { plan.Name, plan.ResolveWithinHours, plan.FirstResponseWithinHours, plan.BusinessHoursOnly }, ct);
        return (await ListAsync(includeInactive: true, ct)).First(p => p.Id == plan.Id);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var plan = await db.SlaPlans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("SLA plan");
        plan.IsActive = active;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(active ? "sla.plan.activated" : "sla.plan.retired", "SlaPlan", plan.Id.ToString(), new { plan.Name }, ct);
    }

    public async Task<IReadOnlyList<DeskHolidayDto>> HolidaysAsync(DateOnly? from = null, CancellationToken ct = default)
    {
        var q = db.DeskHolidays.AsNoTracking();
        if (from is { } f) q = q.Where(h => h.Date >= f);
        return await q.OrderBy(h => h.Date).Select(h => new DeskHolidayDto(h.Id, h.Date, h.Name)).ToListAsync(ct);
    }

    public async Task<DeskHolidayDto> AddHolidayAsync(DateOnly date, string name, CancellationToken ct = default)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > 80) throw new ValidationFailedException("Name the holiday, in up to 80 characters.");
        if (await db.DeskHolidays.AnyAsync(h => h.Date == date, ct))
            throw new ValidationFailedException($"{date:d MMM yyyy} is already a holiday.");
        var holiday = new DeskHoliday { MspOrganizationId = Org, Date = date, Name = clean };
        db.DeskHolidays.Add(holiday);
        await db.SaveChangesAsync(ct);
        // Tickets already raised keep their dates, for the same reason an edited plan does not
        // re-date them: the promise was made on the calendar as it stood.
        await audit.WriteAsync("desk.holiday.added", "DeskHoliday", holiday.Id.ToString(), new { date, clean }, ct);
        return new DeskHolidayDto(holiday.Id, holiday.Date, holiday.Name);
    }

    public async Task RemoveHolidayAsync(Guid id, CancellationToken ct = default)
    {
        var holiday = await db.DeskHolidays.FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw new NotFoundException("Holiday");
        db.DeskHolidays.Remove(holiday);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("desk.holiday.removed", "DeskHoliday", id.ToString(), new { holiday.Date, holiday.Name }, ct);
    }
}

/// <summary>
/// Which plan a new board ticket runs on, and what that makes its due dates. The topic's plan wins
/// over the board's; a retired plan is skipped as if it were not set, so retiring one stops it being
/// applied without anyone having to find every board and topic that names it.
/// </summary>
public static class SlaPlanner
{
    public sealed record Times(Guid? PlanId, DateTimeOffset? ResolveBy, DateTimeOffset? RespondBy);

    public static async Task<Times> ForAsync(
        DeskDbContext db, Board board, BoardTopic? topic, DateTimeOffset raisedAt, CancellationToken ct)
    {
        var planId = topic?.SlaPlanId ?? board.DefaultSlaPlanId;
        if (planId is null) return new Times(null, null, null);

        var plan = await db.SlaPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct);
        if (plan is null && topic?.SlaPlanId is not null && board.DefaultSlaPlanId is { } fallback)
            plan = await db.SlaPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == fallback && p.IsActive, ct);
        if (plan is null) return new Times(null, null, null);

        var (zone, holidays) = await CalendarAsync(db, board.MspOrganizationId, ct);

        return new Times(
            plan.Id,
            SlaClock.Due(raisedAt, plan.ResolveWithinHours, plan, zone, holidays),
            plan.FirstResponseWithinHours is { } first ? SlaClock.Due(raisedAt, first, plan, zone, holidays) : null);
    }

    /// <summary>The organization's time zone and closed days: everything the SLA clock needs besides the plan.</summary>
    public static async Task<(TimeZoneInfo Zone, IReadOnlySet<DateOnly> Holidays)> CalendarAsync(
        DeskDbContext db, Guid organizationId, CancellationToken ct)
    {
        var zone = SlaClock.Zone(await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.TimeZone).FirstOrDefaultAsync(ct));
        var holidays = (await db.DeskHolidays.AsNoTracking().Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        return (zone, holidays);
    }

    /// <summary>
    /// Stops or restarts a board ticket's clock for a status it is about to take. Called before the
    /// status is saved, so the pause and the status land in the same write.
    /// </summary>
    public static async Task ApplyStatusAsync(DeskDbContext db, Ticket ticket, string newStatus, DateTimeOffset now, CancellationToken ct)
    {
        if (ticket.SlaPlanId is not { } planId) return;
        var plan = await db.SlaPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
        var (zone, holidays) = await CalendarAsync(db, ticket.MspOrganizationId, ct);
        SlaClock.OnStatusChange(ticket, newStatus, now, plan, zone, holidays);
    }
}
