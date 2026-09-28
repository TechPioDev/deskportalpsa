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
                db.Boards.Count(b => b.DefaultSlaPlanId == p.Id) + db.BoardTopics.Count(t => t.SlaPlanId == p.Id)))
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

        var zone = SlaClock.Zone(await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == board.MspOrganizationId).Select(o => o.TimeZone).FirstOrDefaultAsync(ct));

        return new Times(
            plan.Id,
            SlaClock.Due(raisedAt, plan.ResolveWithinHours, plan, zone),
            plan.FirstResponseWithinHours is { } first ? SlaClock.Due(raisedAt, first, plan, zone) : null);
    }
}
