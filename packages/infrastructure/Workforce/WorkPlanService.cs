using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Notifications;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Work in people's time: planning your own, scheduling others, moving, giving away and taking out.
///
/// The one rule about assignment: PLANNING NEVER CHANGES WHAT THE PSA SAYS. An allocation records who
/// is planned to do the work and when; the ticket's provider-side assignee, due date, estimate and
/// status are never touched, and cancelling an allocation never touches the ticket. The one bridge:
/// when someone is scheduled on a ticket that nobody in the portal holds yet, they become its portal
/// holder - the same portal-only fact "Take it" records - so the ticket shows in their work and
/// opens for them. A ticket already held by someone else is left alone; if the person could not see
/// it, the scheduler is told to hand it over first rather than have work planned for someone who
/// cannot open it.
///
/// Who may do what follows schedule.manage's scope: Own is your own plan; a wider scope reaching the
/// person lets you place, move, fix, give away and remove their work. Every write holds the person's
/// gate, checks conflicts against what is really there, and is audited.
/// </summary>
public sealed class WorkPlanService(
    DeskDbContext db, WorkforceAccess access, ICapacityService capacity, ITicketScopeQuery tickets,
    IInternalTicketService internalTickets, PlanningGate gate, IAuditWriter audit, TimeProvider clock)
    : IWorkPlanService
{
    public const int MaxPlannedHours = 24;
    public const int NoteMax = 300;
    public const int ReasonMax = 300;
    private const int MaxUnscheduled = 100;

    // ---- reading ----------------------------------------------------------------------------

    public async Task<PersonPlanDto> PlanAsync(Guid callerId, Guid appUserId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, appUserId, ct);
        // Capacity per day, with planned work already counted, and the person's own "today".
        var days = await capacity.ForPersonAsync(callerId, person.Id, from, to, ct);
        var first = days.Days.Count > 0 ? days.Days[0].Date : days.Today;
        var last = days.Days.Count > 0 ? days.Days[^1].Date : days.Today;
        // Instants wide enough for the person's zone, then matched to the dates by the zone itself.
        var lo = new DateTimeOffset(first.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var hi = new DateTimeOffset(last.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var zone = TimeZones.Resolve(days.TimeZone);
        var rows = await db.WorkAllocations.AsNoTracking().Include(a => a.Ticket)
            .Where(a => a.AppUserId == person.Id && a.StartsAt < hi && a.EndsAt > lo && a.Status == WorkAllocationStatus.Planned)
            .OrderBy(a => a.StartsAt).ToListAsync(ct);
        rows = rows.Where(a => { var d = WorkforceCalendar.LocalDate(a.StartsAt, zone); return d >= first && d <= last; }).ToList();

        var self = person.Id == callerId;
        var others = await access.CanScheduleOthersAsync(callerId, person.Id, ct);
        var canPlan = others || (self && await access.MayPlanOwnAsync(callerId, ct));
        var allocations = await DtosAsync(callerId, rows, ct);
        return new PersonPlanDto(person.Id, person.DisplayName, days.TimeZone, days.Today, days.Days, allocations,
            canPlan, others, await access.MayOverrideAsync(callerId, ct));
    }

    public async Task<IReadOnlyList<UnscheduledWorkDto>> UnscheduledAsync(Guid callerId, CancellationToken ct = default)
    {
        if (!await access.MayPlanOwnAsync(callerId, ct)) return [];
        var now = clock.GetUtcNow();
        // "Mine" as My work means it: what I hold, or what sits with a team I am in - narrowed to
        // what I may see, exactly as the ticket list is.
        var myTeams = await db.UserTeams.AsNoTracking().Where(m => m.AppUserId == callerId).Select(m => m.TeamId).ToListAsync(ct);
        var mine = (await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct))
            .Where(TicketStatusRules.Open())
            .Where(t => t.AssignedAppUserId == callerId || (t.AssignedTeamId != null && myTeams.Contains(t.AssignedTeamId.Value)))
            .Where(t => !db.WorkAllocations.Any(a => a.TicketId == t.Id && a.AppUserId == callerId && a.Status == WorkAllocationStatus.Planned && a.EndsAt > now));
        var rows = await mine
            .OrderBy(t => t.SlaDueAt == null).ThenBy(t => t.SlaDueAt).ThenByDescending(t => t.PsaCreatedAt ?? t.CreatedAt)
            .Take(MaxUnscheduled)
            .Select(t => new
            {
                t.Id, t.Number, t.Provider, t.ExternalTicketId, t.Title, t.PortalPriority, t.PortalStatus, t.Origin, t.SlaDueAt,
                AssignedToMe = t.AssignedAppUserId == callerId,
                ClientName = db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                TeamName = db.Teams.Where(x => x.Id == t.AssignedTeamId).Select(x => x.Name).FirstOrDefault(),
                PlannedSoFar = db.WorkAllocations.Where(a => a.TicketId == t.Id && a.Status == WorkAllocationStatus.Planned).Sum(a => (int?)a.PlannedMinutes) ?? 0,
            })
            .ToListAsync(ct);
        return rows.Select(t => new UnscheduledWorkDto(t.Id, Reference(t.Number, t.Provider, t.ExternalTicketId), t.Title, t.ClientName,
            t.PortalPriority, t.PortalStatus, Source(t.Origin, t.Provider), t.SlaDueAt, t.AssignedToMe, t.TeamName, t.PlannedSoFar)).ToList();
    }

    public async Task<TeamPlanDto> TeamAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default)
    {
        var orgZone = access.OrganizationTimeZone();
        var today = WorkforceCalendar.LocalDate(clock.GetUtcNow(), TimeZones.Resolve(orgZone));
        var from = query.From ?? today;
        var to = query.To ?? from;
        // The rows and their days come from the capacity engine (planned work already counted);
        // the pieces of work themselves are one more query for everyone, shaped once.
        var range = await capacity.ForTeamRangeAsync(callerId, new TeamRangeQuery(from, to, query.TeamId, query.DepartmentId, query.SkillIds, query.MatchAllSkills), ct);
        var ids = range.People.Select(p => p.AppUserId).ToList();
        var byPerson = ids.Count == 0
            ? new Dictionary<Guid, List<WorkAllocationDto>>()
            : await AllocationsOnAsync(callerId, ids, range.People.ToDictionary(p => p.AppUserId, p => p.TimeZone), from, to, ct);
        var scheduled = await access.ScheduledByAsync(callerId, ids, ct);
        var mayOwn = await access.MayPlanOwnAsync(callerId, ct);
        var people = range.People.Select(p => new TeamPlanPersonDto(p.AppUserId, p.DisplayName, p.TimeZone, p.IsSchedulable, p.HasSchedule, p.Teams, p.Skills, p.Days,
            byPerson.GetValueOrDefault(p.AppUserId) ?? [], scheduled.Contains(p.AppUserId) || (p.AppUserId == callerId && mayOwn))).ToList();
        // Sums are the capacity actually on offer, as Team capacity counts it.
        var offered = people.Where(p => p.IsSchedulable).SelectMany(p => p.Days).ToList();
        return new TeamPlanDto(from, to, range.Today, range.TimeZone, people,
            offered.Sum(d => d.UsableMinutes), offered.Sum(d => d.ConfirmedMinutes), offered.Sum(d => d.RemainingConfirmedMinutes),
            people.Sum(p => p.Allocations.Count), scheduled.Count > 0, await access.MayOverrideAsync(callerId, ct));
    }

    /// <summary>Planned work starting on these dates in each person's own zone - one query for everyone, shaped in one pass.</summary>
    private async Task<Dictionary<Guid, List<WorkAllocationDto>>> AllocationsOnAsync(
        Guid callerId, List<Guid> ids, IReadOnlyDictionary<Guid, string> zones, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var lo = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var hi = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = await db.WorkAllocations.AsNoTracking().Include(a => a.Ticket)
            .Where(a => ids.Contains(a.AppUserId) && a.Status == WorkAllocationStatus.Planned && a.StartsAt < hi && a.EndsAt > lo)
            .OrderBy(a => a.StartsAt).ToListAsync(ct);
        var resolved = zones.ToDictionary(z => z.Key, z => TimeZones.Resolve(z.Value));
        rows = rows.Where(a => { var d = WorkforceCalendar.LocalDate(a.StartsAt, resolved[a.AppUserId]); return d >= from && d <= to; }).ToList();
        return (await DtosAsync(callerId, rows, ct)).GroupBy(d => d.AppUserId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<IReadOnlyList<TeamUnscheduledWorkDto>> UnscheduledTeamAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default)
    {
        // The people are the scheduler's rows: everyone the caller may see, narrowed as asked.
        var skills = await KnownSkillsAsync(query.SkillIds, ct);
        var staff = access.Narrow((await access.VisibleStaffAsync(callerId, ct)).Where(u => u.IsActive), query.TeamId, query.DepartmentId, skills, query.MatchAllSkills);
        var people = await staff.AsNoTracking().Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct);
        if (people.Count == 0) return [];
        var ids = people.Select(p => p.Id).ToList();
        // Work routed to a team one of them is in sits with the group too (just the asked team, when one was asked for).
        var teamIds = query.TeamId is { } team
            ? new List<Guid> { team }
            : await db.UserTeams.AsNoTracking().Where(m => ids.Contains(m.AppUserId)).Select(m => m.TeamId).Distinct().ToListAsync(ct);
        var now = clock.GetUtcNow();
        var open = (await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct))
            .Where(TicketStatusRules.Open())
            .Where(t => (t.AssignedAppUserId != null && ids.Contains(t.AssignedAppUserId.Value))
                        || (t.AssignedTeamId != null && teamIds.Contains(t.AssignedTeamId.Value)))
            // In nobody's plan: no planned work on it that is still to come, whoever it is planned for.
            .Where(t => !db.WorkAllocations.Any(a => a.TicketId == t.Id && a.Status == WorkAllocationStatus.Planned && a.EndsAt > now));
        var rows = await open
            .OrderBy(t => t.SlaDueAt == null).ThenBy(t => t.SlaDueAt).ThenByDescending(t => t.PsaCreatedAt ?? t.CreatedAt)
            .Take(MaxUnscheduled)
            .Select(t => new
            {
                t.Id, t.Number, t.Provider, t.ExternalTicketId, t.Title, t.PortalPriority, t.PortalStatus, t.Origin, t.SlaDueAt, t.AssignedAppUserId, t.AssignedTeamId,
                ClientName = db.ClientCompanies.Where(c => c.Id == t.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                TeamName = db.Teams.Where(x => x.Id == t.AssignedTeamId).Select(x => x.Name).FirstOrDefault(),
                PlannedSoFar = db.WorkAllocations.Where(a => a.TicketId == t.Id && a.Status == WorkAllocationStatus.Planned).Sum(a => (int?)a.PlannedMinutes) ?? 0,
            })
            .ToListAsync(ct);
        var names = people.ToDictionary(p => p.Id, p => p.DisplayName);
        // A holder outside the group (work routed to one of its teams but held by someone the caller
        // may not see) is only said to exist: no id, no name.
        return rows.Select(t =>
        {
            var inside = t.AssignedAppUserId is { } h && names.ContainsKey(h);
            return new TeamUnscheduledWorkDto(t.Id, Reference(t.Number, t.Provider, t.ExternalTicketId), t.Title, t.ClientName,
                t.PortalPriority, t.PortalStatus, Source(t.Origin, t.Provider), t.SlaDueAt,
                inside ? t.AssignedAppUserId : null, inside ? names[t.AssignedAppUserId!.Value] : null, t.AssignedAppUserId is not null && !inside,
                t.AssignedTeamId, t.TeamName, t.PlannedSoFar);
        }).ToList();
    }

    /// <summary>The requested skills that exist here: the same refusal as the capacity views give, so the queue and the rows agree.</summary>
    private async Task<List<Guid>> KnownSkillsAsync(IReadOnlyList<Guid>? skillIds, CancellationToken ct)
    {
        if (skillIds is not { Count: > 0 }) return [];
        var wanted = skillIds.Distinct().ToList();
        if (wanted.Count > 20) throw new ValidationFailedException("Ask for at most 20 skills at a time.");
        var known = await db.Skills.AsNoTracking().CountAsync(s => wanted.Contains(s.Id), ct);
        if (known != wanted.Count) throw new ValidationFailedException("One of the skills asked for is not in the skill catalogue.");
        return wanted;
    }

    public async Task<IReadOnlyList<PlannablePersonDto>> PlannablePeopleAsync(Guid callerId, CancellationToken ct = default)
    {
        var people = await (await access.SchedulableStaffAsync(callerId, ct)).AsNoTracking()
            .Where(u => u.IsActive).OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.DisplayName, u.IsSchedulable }).ToListAsync(ct);
        if (people.Count == 0) return [];
        var ids = people.Select(p => p.Id).ToList();
        var versions = await WorkforceCalendar.VersionsAsync(db, ids, ct);
        var orgZone = access.OrganizationTimeZone();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return people.Select(p => new PlannablePersonDto(p.Id, p.DisplayName, p.Id == callerId,
            WorkforceCalendar.InForce(versions.GetValueOrDefault(p.Id), today)?.TimeZone ?? orgZone, p.IsSchedulable)).ToList();
    }

    public async Task<IReadOnlyList<WorkAllocationDto>> ForTicketAsync(Guid callerId, Guid ticketId, CancellationToken ct = default)
    {
        _ = await tickets.FindAsync(db.Tickets.AsNoTracking(), ticketId, callerId, Permissions.TicketsViewAll, ct)
            ?? throw new NotFoundException("Ticket");
        var visible = (await access.VisibleStaffAsync(callerId, ct)).Select(u => u.Id);
        var rows = await db.WorkAllocations.AsNoTracking().Include(a => a.Ticket)
            .Where(a => a.TicketId == ticketId && visible.Contains(a.AppUserId))
            .OrderByDescending(a => a.Status == WorkAllocationStatus.Planned).ThenBy(a => a.StartsAt).Take(50).ToListAsync(ct);
        return await DtosAsync(callerId, rows, ct);
    }

    // ---- writing ----------------------------------------------------------------------------

    public async Task<WorkAllocationDto> CreateAsync(Guid callerId, WorkAllocationInput input, CancellationToken ct = default)
    {
        var person = await access.VisiblePersonAsync(callerId, input.AppUserId, ct);
        var self = person.Id == callerId;
        var others = await access.CanScheduleOthersAsync(callerId, person.Id, ct);
        if (!others && !(self && await access.MayPlanOwnAsync(callerId, ct)))
            throw new ForbiddenException(self ? "You can't plan your own work." : "You can't plan work for this person.");
        var (start, end) = CheckPeriod(input.Start, input.End);
        var note = CheckText(input.Note, NoteMax, "note");
        if (!others && input.IsFixed) throw new ValidationFailedException("Only someone who schedules others can fix work in place.");

        // The caller must be able to see the work; so must the person it is planned for.
        var ticket = await tickets.FindAsync(db.Tickets, input.TicketId, callerId, Permissions.TicketsViewAll, ct)
                     ?? throw new NotFoundException("Ticket");
        if (TicketStatusRules.Finished(ticket.PortalStatus))
            throw new ValidationFailedException("This ticket is finished; there is nothing left to plan.");
        var bridge = !self && await BridgeNeededAsync(person, ticket, ct);

        // Everything that changes anything happens under the person's gate, after the conflict check,
        // in one transaction: a refused placement leaves the ticket exactly as it was.
        await using var hold = await gate.HoldAsync(person.Id, ct);
        var verdict = await CheckAsync(callerId, person.Id, start, end, null, input.OverrideReason, ct);
        var row = await PlaceAsync(callerId, person, self, ticket, start, end, !self && input.IsFixed, note, verdict, bridge, ct);
        await hold.CommitAsync(ct);
        return (await DtosAsync(callerId, [row], ct)).Single();
    }

    public async Task<WorkAllocationDto> CreateInternalWorkAsync(Guid callerId, InternalWorkInput input, CancellationToken ct = default)
    {
        if (!await access.MayPlanOwnAsync(callerId, ct)) throw new ForbiddenException("You can't plan your own work.");
        var person = await access.VisiblePersonAsync(callerId, callerId, ct);
        var (start, end) = CheckPeriod(input.Start, input.End);
        var note = CheckText(input.Note, NoteMax, "note");

        // The time is checked before the ticket exists, so a refused placement raises nothing; the
        // ticket and the plan are then written in one transaction - no work without its plan either.
        await using var hold = await gate.HoldAsync(person.Id, ct);
        var verdict = await CheckAsync(callerId, person.Id, start, end, null, input.OverrideReason, ct);
        // The ticket is real work on a real board, assigned to the planner: the board service checks
        // the board, the membership and the client exactly as "New ticket" does.
        var created = await internalTickets.CreateAsync(callerId, new InternalTicketInput(
            input.BoardId, input.Title ?? "", input.Description, input.Priority, null, input.ClientCompanyId, AssignedAppUserId: callerId), ct);
        var ticket = await db.Tickets.FirstAsync(t => t.Id == created.TicketId, ct);
        var row = await PlaceAsync(callerId, person, true, ticket, start, end, false, note, verdict, false, ct);
        await hold.CommitAsync(ct);
        return (await DtosAsync(callerId, [row], ct)).Single();
    }

    /// <summary>
    /// Writes the allocation - and the holder bridge when one is due - in the gate's transaction,
    /// audits it and tells the person. The conflict verdict is already in.
    /// </summary>
    private async Task<WorkAllocation> PlaceAsync(Guid callerId, AppUser person, bool self, Ticket ticket, DateTimeOffset start, DateTimeOffset end,
        bool isFixed, string? note, Verdict verdict, bool bridge, CancellationToken ct)
    {
        if (bridge) Bridge(callerId, person, ticket);
        var row = new WorkAllocation
        {
            MspOrganizationId = person.MspOrganizationId ?? Guid.Empty, TicketId = ticket.Id, Ticket = ticket, AppUserId = person.Id,
            StartsAt = start, EndsAt = end, PlannedMinutes = Minutes(start, end),
            Method = self ? SchedulingMethod.Self : SchedulingMethod.AuthorizedUser, ScheduledByUserId = callerId,
            IsFixed = isFixed, Note = note, UpdatedByUserId = callerId,
        };
        ApplyOverride(row, verdict, callerId);
        db.WorkAllocations.Add(row);
        await SaveAsync(ct);
        if (bridge) await AuditBridgeAsync(person, ticket, ct);
        var zone = await ZoneAsync(person.Id, start, ct);
        await audit.WriteAsync("workforce.allocation.created", "WorkAllocation", row.Id.ToString(), new
        {
            person = person.DisplayName, ticketId = ticket.Id, reference = Reference(ticket), method = row.Method.ToString(),
            when = Describe(row, zone), isFixed = row.IsFixed, overrideReason = row.OverrideReason, overridden = row.OverriddenConflicts,
            warnings = verdict.Warnings, holderSet = bridge,
        }, ct);
        if (!self) await NotifyAsync(person.Id, row, zone, $"Work planned for you: {Reference(ticket)}", $"{ticket.Title} · {Describe(row, zone)}", ct);
        return row;
    }

    public async Task<WorkAllocationDto> UpdateAsync(Guid callerId, Guid allocationId, WorkAllocationUpdate input, CancellationToken ct = default)
    {
        var row = await LoadAsync(allocationId, ct);
        var person = await access.VisiblePersonAsync(callerId, row.AppUserId, ct);
        var self = person.Id == callerId;
        var others = await access.CanScheduleOthersAsync(callerId, person.Id, ct);
        if (row.Status != WorkAllocationStatus.Planned) throw new ValidationFailedException("This work is no longer in the plan.");
        if (!others)
        {
            if (!self || !await access.MayPlanOwnAsync(callerId, ct)) throw new ForbiddenException("You can't change this person's plan.");
            if (row.Method != SchedulingMethod.Self && row.IsFixed)
                throw new ForbiddenException("This work was scheduled for you and is fixed in place. Ask whoever planned it to move it.");
            if (input.IsFixed is { } wantFixed && wantFixed != row.IsFixed)
                throw new ForbiddenException("Only someone who schedules others can fix work in place or free it.");
        }
        if (input.Version != row.Version) throw Stale();
        var (start, end) = CheckPeriod(input.Start, input.End);
        var note = input.Note is null ? row.Note : CheckText(input.Note, NoteMax, "note");

        await using var hold = await gate.HoldAsync(person.Id, ct);
        await FreshAsync(row, input.Version, ct);
        var moved = start != row.StartsAt || end != row.EndsAt;
        var verdict = moved
            ? await CheckAsync(callerId, person.Id, start, end, row.Id, input.OverrideReason, ct)
            : Verdict.Clean;
        var zone = await ZoneAsync(person.Id, start, ct);
        var before = Describe(row, zone);
        var (beforeStart, beforeEnd) = (row.StartsAt, row.EndsAt);
        row.StartsAt = start;
        row.EndsAt = end;
        row.PlannedMinutes = Minutes(start, end);
        if (others && input.IsFixed is { } fix) row.IsFixed = row.Method == SchedulingMethod.Self ? false : fix;
        row.Note = note;
        row.UpdatedByUserId = callerId;
        row.Version++;
        if (moved) ApplyOverride(row, verdict, callerId);
        await SaveAsync(ct);
        var action = !moved ? "workforce.allocation.changed" : start == beforeStart && end != beforeEnd ? "workforce.allocation.resized" : "workforce.allocation.moved";
        await audit.WriteAsync(action, "WorkAllocation", row.Id.ToString(), new
        {
            person = person.DisplayName, reference = Reference(row.Ticket!), before, after = Describe(row, zone), isFixed = row.IsFixed,
            overrideReason = moved ? row.OverrideReason : null, overridden = moved ? row.OverriddenConflicts : null, warnings = verdict.Warnings,
        }, ct);
        if (!self && moved) await NotifyAsync(person.Id, row, zone, $"Planned work moved: {Reference(row.Ticket!)}", $"{row.Ticket!.Title} · now {Describe(row, zone)}", ct);
        await hold.CommitAsync(ct);
        return (await DtosAsync(callerId, [row], ct)).Single();
    }

    public async Task<WorkAllocationDto> ReassignAsync(Guid callerId, Guid allocationId, WorkAllocationReassign input, CancellationToken ct = default)
    {
        var row = await LoadAsync(allocationId, ct);
        var from = await access.VisiblePersonAsync(callerId, row.AppUserId, ct);
        if (row.Status != WorkAllocationStatus.Planned) throw new ValidationFailedException("This work is no longer in the plan.");
        if (!await access.CanScheduleOthersAsync(callerId, from.Id, ct)) throw new ForbiddenException("Only someone who schedules others can give work to someone else.");
        // The new person: in the caller's reach, or "not found" - the same answer as a person who does
        // not exist or belongs to another organization.
        var to = await access.VisiblePersonAsync(callerId, input.AppUserId, ct);
        if (!await access.CanScheduleOthersAsync(callerId, to.Id, ct)) throw new ForbiddenException("You can't plan work for that person.");
        if (to.Id == from.Id) throw new ValidationFailedException("That is who already has it.");
        if (input.Version != row.Version) throw Stale();
        var (start, end) = CheckPeriod(input.Start ?? row.StartsAt, input.End ?? row.EndsAt);
        var ticket = row.Ticket!;
        var bridge = await BridgeNeededAsync(to, ticket, ct, handOverFrom: from.Id);

        await using var hold = await gate.HoldAsync(to.Id, ct);
        await FreshAsync(row, input.Version, ct);
        var verdict = await CheckAsync(callerId, to.Id, start, end, row.Id, input.OverrideReason, ct);
        var before = $"{from.DisplayName}, {Describe(row, await ZoneAsync(from.Id, row.StartsAt, ct))}";
        var zone = await ZoneAsync(to.Id, start, ct);
        if (bridge) Bridge(callerId, to, ticket);
        row.AppUserId = to.Id;
        row.StartsAt = start;
        row.EndsAt = end;
        row.PlannedMinutes = Minutes(start, end);
        row.Method = SchedulingMethod.AuthorizedUser;
        row.ScheduledByUserId = callerId;
        row.UpdatedByUserId = callerId;
        row.Version++;
        ApplyOverride(row, verdict, callerId);
        await SaveAsync(ct);
        if (bridge) await AuditBridgeAsync(to, ticket, ct);
        await audit.WriteAsync("workforce.allocation.reassigned", "WorkAllocation", row.Id.ToString(), new
        {
            reference = Reference(ticket), from = from.DisplayName, to = to.DisplayName, before, after = Describe(row, zone),
            overrideReason = row.OverrideReason, overridden = row.OverriddenConflicts, warnings = verdict.Warnings, holderSet = bridge,
        }, ct);
        if (from.Id != callerId) await NotifyAsync(from.Id, row, zone, $"Work taken out of your plan: {Reference(ticket)}", $"{ticket.Title} is now planned for {to.DisplayName}.", ct);
        if (to.Id != callerId) await NotifyAsync(to.Id, row, zone, $"Work planned for you: {Reference(ticket)}", $"{ticket.Title} · {Describe(row, zone)}", ct);
        await hold.CommitAsync(ct);
        return (await DtosAsync(callerId, [row], ct)).Single();
    }

    public async Task<WorkAllocationDto> CancelAsync(Guid callerId, Guid allocationId, string? reason, CancellationToken ct = default)
    {
        var row = await LoadAsync(allocationId, ct);
        var person = await access.VisiblePersonAsync(callerId, row.AppUserId, ct);
        var self = person.Id == callerId;
        if (row.Status != WorkAllocationStatus.Planned) throw new ValidationFailedException("This work is already out of the plan.");
        if (!await access.CanScheduleOthersAsync(callerId, person.Id, ct))
        {
            if (!self || !await access.MayPlanOwnAsync(callerId, ct)) throw new ForbiddenException("You can't change this person's plan.");
            if (row.Method != SchedulingMethod.Self)
                throw new ForbiddenException("This work was scheduled for you. Ask whoever planned it to take it out, or move it if it is not fixed.");
        }
        var cancelReason = CheckText(reason, 200, "reason");

        await using var hold = await gate.HoldAsync(person.Id, ct);
        await FreshAsync(row, null, ct);
        if (row.Status != WorkAllocationStatus.Planned) throw new ValidationFailedException("This work is already out of the plan.");
        var zone = await ZoneAsync(person.Id, row.StartsAt, ct);
        row.Status = WorkAllocationStatus.Cancelled;
        row.CancelledAt = clock.GetUtcNow();
        row.CancelledByUserId = callerId;
        row.CancelReason = cancelReason;
        row.UpdatedByUserId = callerId;
        row.Version++;
        await SaveAsync(ct);
        // The ticket is untouched: taking work out of a plan is not closing it.
        await audit.WriteAsync("workforce.allocation.cancelled", "WorkAllocation", row.Id.ToString(),
            new { person = person.DisplayName, reference = Reference(row.Ticket!), was = Describe(row, zone), reason = row.CancelReason }, ct);
        if (!self) await NotifyAsync(person.Id, row, zone, $"Planned work taken out: {Reference(row.Ticket!)}", $"{row.Ticket!.Title} · was {Describe(row, zone)}", ct);
        await hold.CommitAsync(ct);
        return (await DtosAsync(callerId, [row], ct)).Single();
    }

    /// <summary>
    /// What the row is NOW, under the gate: a request that read it a moment ago may be acting on a plan
    /// someone else has since changed. A version the caller did not see is a stale screen.
    /// </summary>
    private async Task FreshAsync(WorkAllocation row, int? expectedVersion, CancellationToken ct)
    {
        await db.Entry(row).ReloadAsync(ct);
        if (expectedVersion is { } v && v != row.Version) throw Stale();
    }

    /// <summary>Saves under the optimistic token: a row changed between the read and the write is a stale screen, not a crash.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Stale(); }
    }

    // ---- the checks every write makes ------------------------------------------------------

    private sealed record Verdict(IReadOnlyList<ConflictDto> Overridden, IReadOnlyList<string> Warnings, string? Reason)
    {
        public static readonly Verdict Clean = new([], [], null);
    }

    /// <summary>
    /// Conflicts against what is in the plan NOW (under the person's gate): blocks refuse, overridable
    /// conflicts refuse unless the caller may override and gave a reason, warnings pass and are kept.
    /// </summary>
    private async Task<Verdict> CheckAsync(Guid callerId, Guid appUserId, DateTimeOffset start, DateTimeOffset end, Guid? ignore, string? overrideReason, CancellationToken ct)
    {
        var result = await capacity.EvaluateAsync(callerId, appUserId, new ProposedWork(start, end, IgnoreAllocationId: ignore), ct);
        var warnings = result.Conflicts.Where(c => c.Severity == ConflictSeverity.Warning).Select(c => c.Message).Distinct().ToList();
        if (result.CanSchedule) return new Verdict([], warnings, null);

        var mayOverride = result.CanOverride && await access.MayOverrideAsync(callerId, ct);
        var reason = CheckText(overrideReason, ReasonMax, "override reason");
        if (result.CanOverride && mayOverride && reason is { Length: >= 5 })
            return new Verdict(result.Conflicts.Where(c => c.Severity == ConflictSeverity.Overridable).ToList(), warnings, reason);

        var first = result.Conflicts.OrderByDescending(c => c.Severity).First();
        var message = result.CanOverride
            ? mayOverride
                ? $"This time has a conflict: {first.Message} Give a reason to override it."
                : $"This time is no longer available: {first.Message}"
            : $"This time cannot be used: {first.Message}";
        throw new ConflictException(message, new ConflictProblemDto(result.CanOverride, mayOverride, result.Conflicts));
    }

    /// <summary>What was overridden, why, by whom and when - kept with the work; cleared when it is placed cleanly.</summary>
    private void ApplyOverride(WorkAllocation row, Verdict verdict, Guid callerId)
    {
        if (verdict.Overridden.Count == 0) { row.OverrideReason = null; row.OverriddenConflicts = null; row.OverriddenByUserId = null; row.OverriddenAt = null; return; }
        row.OverrideReason = verdict.Reason;
        row.OverriddenConflicts = string.Join(",", verdict.Overridden.Select(c => c.Type.ToString()).Distinct());
        row.OverriddenByUserId = callerId;
        row.OverriddenAt = clock.GetUtcNow();
    }

    /// <summary>
    /// The one bridge between planning and assignment: a ticket nobody in the portal holds becomes
    /// held by the person it is planned for (portal-side only, as "Take it" is; the PSA is not told).
    /// A ticket someone else holds is left alone, and if the person could not see it the scheduler
    /// is told so, because work planned for someone who cannot open it is work that will not happen.
    /// </summary>
    /// <param name="handOverFrom">When the work is being given away by its planned person: if they
    /// hold the ticket, it goes with the work rather than staying behind with someone who no longer
    /// has it planned.</param>
    /// <remarks>Decides only; nothing is written until the plan itself is, so a refused placement
    /// leaves the ticket's holder exactly as it was.</remarks>
    private async Task<bool> BridgeNeededAsync(AppUser person, Ticket ticket, CancellationToken ct, Guid? handOverFrom = null)
    {
        var sees = await tickets.FindAsync(db.Tickets.AsNoTracking(), ticket.Id, person.Id, Permissions.TicketsViewAll, ct) is not null;
        if (ticket.AssignedAppUserId is { } holder && holder != person.Id && holder != handOverFrom)
        {
            if (sees) return false;
            var holderName = await db.AppUsers.AsNoTracking().Where(u => u.Id == holder).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "someone else";
            throw new ValidationFailedException($"{person.DisplayName} cannot open this ticket: it is held by {holderName}. Hand it over to {person.DisplayName} first, or plan it for {holderName}.");
        }
        if (ticket.AssignedAppUserId == person.Id) return false;
        if (sees && ticket.AssignedAppUserId != handOverFrom && ticket.Origin == TicketOrigin.Psa && ticket.AssignedTechnicianExternalId is { Length: > 0 })
            // A PSA ticket the provider has assigned to someone, and the person can already see it:
            // leave the holder alone rather than second-guess the provider.
            return false;
        return true;
    }

    /// <summary>The bridge itself: tracked changes only, saved with the plan in the same transaction.</summary>
    private void Bridge(Guid callerId, AppUser person, Ticket ticket)
    {
        var previous = ticket.AssignedAppUserId;
        ticket.AssignedAppUserId = person.Id;
        ticket.AssignedByUserId = callerId;
        db.TicketAssignments.Add(new TicketAssignment
        {
            MspOrganizationId = ticket.MspOrganizationId, TicketId = ticket.Id, FromAppUserId = previous, ToAppUserId = person.Id,
            AssignedByUserId = callerId, Note = previous is null ? "Planned into their time" : "Planned work given to them",
        });
    }

    private Task AuditBridgeAsync(AppUser person, Ticket ticket, CancellationToken ct)
        => audit.WriteAsync("ticket.assigned.portal", "Ticket", ticket.Id.ToString(),
            new { ticket.ExternalTicketId, appUserId = person.Id, person.DisplayName, viaPlanning = true }, ct);

    /// <summary>The person's zone on the day the work starts: what their plan, the audit trail and their notification say times in.</summary>
    private async Task<TimeZoneInfo> ZoneAsync(Guid appUserId, DateTimeOffset at, CancellationToken ct)
    {
        var versions = await WorkforceCalendar.VersionsAsync(db, [appUserId], ct);
        var id = WorkforceCalendar.InForce(versions.GetValueOrDefault(appUserId), WorkforceCalendar.LocalDate(at, TimeZoneInfo.Utc))?.TimeZone
                 ?? access.OrganizationTimeZone();
        return TimeZones.Resolve(id);
    }

    /// <summary>
    /// Tells the person someone else changed their plan, through the push notifications they already
    /// have: only when they have a device signed up and want this kind of news (the same choice as
    /// "work assigned to you"). Nobody is told about their own planning. Sent by the worker's push
    /// pass, so the write here is one row.
    /// </summary>
    private async Task NotifyAsync(Guid appUserId, WorkAllocation row, TimeZoneInfo zone, string title, string body, CancellationToken ct)
    {
        if (!await db.PushSubscriptions.AnyAsync(s => s.AppUserId == appUserId, ct)) return;
        var preference = await db.PushPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.AppUserId == appUserId, ct);
        if (preference is not null && !preference.Wants(PushKind.WorkPlanned)) return;
        db.PushNotifications.Add(new PushNotification
        {
            MspOrganizationId = row.MspOrganizationId, AppUserId = appUserId, Kind = PushKind.WorkPlanned, TicketId = row.TicketId,
            Title = title, Body = body, Url = $"/dashboard/workforce/my-plan?date={WorkforceCalendar.LocalDate(row.StartsAt, zone):yyyy-MM-dd}",
        });
        await db.SaveChangesAsync(ct);
    }

    private (DateTimeOffset Start, DateTimeOffset End) CheckPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        var problems = new List<string>();
        if (end <= start) problems.Add("The work must end after it starts.");
        else if (end - start > TimeSpan.FromHours(MaxPlannedHours)) problems.Add($"One piece of planned work can be at most {MaxPlannedHours} hours.");
        if (start.Ticks % TimeSpan.TicksPerMinute != 0 || end.Ticks % TimeSpan.TicksPerMinute != 0) problems.Add("Times must be whole minutes.");
        var now = clock.GetUtcNow();
        if (start < now.AddDays(-1)) problems.Add("Work can be planned from yesterday onwards, not earlier.");
        if (start > now.AddYears(1)) problems.Add("Work can be planned at most a year ahead.");
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));
        return (start, end);
    }

    private static string? CheckText(string? value, int max, string what)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is null) return null;
        if (text.Length > max) throw new ValidationFailedException($"Keep the {what} to {max} characters.");
        if (text.Any(char.IsControl)) throw new ValidationFailedException($"The {what} contains characters that can't be shown.");
        return text;
    }

    private static ConflictException Stale()
        => new("This plan changed since you opened it. Reload and try again.", new ConflictProblemDto(false, false, [], Stale: true));

    private static int Minutes(DateTimeOffset start, DateTimeOffset end) => (int)Math.Round((end - start).TotalMinutes);

    private async Task<WorkAllocation> LoadAsync(Guid id, CancellationToken ct)
        => await db.WorkAllocations.Include(a => a.Ticket).FirstOrDefaultAsync(a => a.Id == id, ct)
           ?? throw new NotFoundException("Planned work");

    // ---- shapes -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<WorkAllocationDto>> DtosAsync(Guid callerId, IReadOnlyList<WorkAllocation> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var visible = (await (await tickets.VisibleAsync(db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)), callerId, Permissions.TicketsViewAll, ct))
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var clientIds = rows.Where(r => r.Ticket?.ClientCompanyId is not null).Select(r => r.Ticket!.ClientCompanyId!.Value).Distinct().ToList();
        var clients = clientIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.ClientCompanies.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var personIds = rows.SelectMany(r => new[] { r.AppUserId, r.ScheduledByUserId, r.OverriddenByUserId ?? Guid.Empty, r.CancelledByUserId ?? Guid.Empty })
            .Where(id => id != Guid.Empty).Distinct().ToList();
        var names = await db.AppUsers.AsNoTracking().Where(u => personIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var versions = await WorkforceCalendar.VersionsAsync(db, rows.Select(r => r.AppUserId).Distinct().ToList(), ct);
        var orgZone = access.OrganizationTimeZone();

        var scheduled = await access.ScheduledByAsync(callerId, rows.Select(r => r.AppUserId).Distinct().ToList(), ct);
        var mayOwn = await access.MayPlanOwnAsync(callerId, ct);
        var result = new List<WorkAllocationDto>(rows.Count);
        foreach (var r in rows)
        {
            var others = scheduled.Contains(r.AppUserId);
            var self = r.AppUserId == callerId;
            var live = r.Status == WorkAllocationStatus.Planned;
            var canEdit = live && (others || (self && mayOwn && (r.Method == SchedulingMethod.Self || !r.IsFixed)));
            var canCancel = live && (others || (self && mayOwn && r.Method == SchedulingMethod.Self));
            var sees = visible.Contains(r.TicketId);
            var t = r.Ticket;
            var zoneId = WorkforceCalendar.InForce(versions.GetValueOrDefault(r.AppUserId), WorkforceCalendar.LocalDate(r.StartsAt, TimeZoneInfo.Utc))?.TimeZone ?? orgZone;
            result.Add(new WorkAllocationDto(r.Id, r.AppUserId, names.GetValueOrDefault(r.AppUserId) ?? "",
                r.TicketId, sees,
                sees && t is not null ? Reference(t) : null, sees ? t?.Title : null,
                sees && t?.ClientCompanyId is { } c ? clients.GetValueOrDefault(c) : null,
                sees ? t?.PortalStatus : null, t is not null && TicketStatusRules.Finished(t.PortalStatus),
                r.StartsAt, r.EndsAt, r.PlannedMinutes, zoneId,
                r.Status, r.Method, r.ScheduledByUserId, names.GetValueOrDefault(r.ScheduledByUserId),
                r.IsFixed, r.Note,
                r.OverrideReason,
                (r.OverriddenConflicts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Enum.Parse<ConflictType>(s)).ToList(),
                r.OverriddenByUserId is { } ob ? names.GetValueOrDefault(ob) : null,
                r.CancelledAt, r.CancelledByUserId is { } cb ? names.GetValueOrDefault(cb) : null, r.CancelReason,
                r.Version, canEdit, canCancel, live && others));
        }
        return result;
    }

    /// <summary>"INT-000123" for the team's own work; "Autotask 12345" for a provider's.</summary>
    public static string Reference(Ticket t) => Reference(t.Number, t.Provider, t.ExternalTicketId);

    private static string Reference(string? number, Desk.Domain.Enums.ProviderType? provider, string? externalId)
        => number ?? (provider is { } p ? $"{ProviderName(p)} {externalId}" : externalId ?? "Ticket");

    private static string ProviderName(Desk.Domain.Enums.ProviderType p) => p switch
    {
        Desk.Domain.Enums.ProviderType.AutotaskPsa => "Autotask",
        Desk.Domain.Enums.ProviderType.ConnectWisePsa => "ConnectWise",
        _ => p.ToString(),
    };

    private static string Source(TicketOrigin origin, Desk.Domain.Enums.ProviderType? provider) => origin switch
    {
        TicketOrigin.Internal => "Team board",
        TicketOrigin.Rmm => "Monitoring",
        _ => provider is { } p ? ProviderName(p) : "PSA",
    };

    /// <summary>"5 Oct 2026 14:00–15:30 (Europe/London)" in the person's own zone - for the audit trail and notifications.</summary>
    private static string Describe(WorkAllocation a, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(a.StartsAt, zone);
        var end = TimeZoneInfo.ConvertTime(a.EndsAt, zone);
        return $"{start:d MMM yyyy HH\\:mm}–{end:HH\\:mm} ({zone.Id})";
    }
}
