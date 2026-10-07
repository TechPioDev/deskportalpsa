using Desk.Application.Admin;
using Desk.Application.Authorization;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Tickets;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// Work execution: the clock on a piece of work, and a day read as planned against actual.
///
/// Rules that hold everywhere here:
/// - A session is attached to a ticket the person may log time on. It is never attendance.
/// - One running clock per person. Starting or resuming while another runs is refused with the
///   running work named, unless the caller says what to do with it (pause it, or stop it).
/// - Time runs only inside segments; a pause closes the open segment, a resume opens the next.
///   The server's timestamps are the truth; the screen only displays them.
/// - Stopping turns the active seconds into one ticket time entry through the same writer the time
///   panel uses; the session then counts for nothing itself, so a minute is summed once: a day's
///   actual time is the person's time entries dated that day plus the live clocks that have not
///   become entries yet.
/// - Every state change runs under the person's gate with the version the screen saw, so a
///   double-click, two tabs or two devices end with one state.
/// </summary>
public sealed class WorkTimeService(
    DeskDbContext db, WorkforceAccess access, ICapacityService capacity, ITicketScopeQuery tickets, IWorkPlanService plans,
    IConnectorResolver connectors, TicketTimeWriter writer, IEffectivePermissionService permissions,
    PlanningGate gate, IAuditWriter audit, TimeProvider clock) : IWorkTimeService
{
    /// <summary>A clock stopped under this is thrown away rather than logged: nothing was done.</summary>
    public const int MinimumLoggedSeconds = 60;

    private ReferenceLabels? _labels;

    /// <summary>
    /// One ticket's reference. The connections are read once for the request, and only where the
    /// reference depends on one: the team's own tickets carry a number of their own.
    /// </summary>
    private async Task<string> ReferenceAsync(Ticket t, CancellationToken ct)
        => ReferenceLabels.Needed(t.Number, t.Provider)
            ? WorkPlanService.Reference(t, _labels ??= await ReferenceLabels.LoadAsync(db, ct))
            : WorkPlanService.Reference(t);
    public const int NoteMax = 2000;

    // ---- the clock ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WorkSessionDto>> ActiveAsync(Guid callerId, CancellationToken ct = default)
    {
        var rows = await OpenSessionsAsync(callerId, ct);
        return await DtosAsync(callerId, rows, ct);
    }

    public async Task<WorkSessionDto> StartAsync(Guid callerId, StartWorkInput input, CancellationToken ct = default)
    {
        var ticket = await tickets.FindAsync(db.Tickets, input.TicketId, callerId, Permissions.TicketsLogTime, ct)
                     ?? throw new NotFoundException("Ticket");
        if (TicketStatusRules.Finished(ticket.PortalStatus)) throw new ValidationFailedException("This ticket is finished; there is nothing left to work on.");
        if (ticket.Origin == TicketOrigin.Psa && string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA, so time cannot be logged.");
        if (input.AllocationId is { } allocationId
            && !await db.WorkAllocations.AsNoTracking().AnyAsync(a => a.Id == allocationId && a.AppUserId == callerId && a.TicketId == ticket.Id, ct))
            throw new ValidationFailedException("That planned work is not yours, or not on this ticket.");

        await using var hold = await gate.HoldAsync(callerId, ct);
        var open = await OpenSessionsAsync(callerId, ct);
        var now = clock.GetUtcNow();

        // Already on it: the same clock, not a second one.
        var same = open.FirstOrDefault(s => s.TicketId == ticket.Id);
        if (same is { Status: WorkSessionStatus.Active })
        {
            await hold.CommitAsync(ct);
            return await DtoAsync(callerId, same, ct);
        }
        var running = open.FirstOrDefault(s => s.Status == WorkSessionStatus.Active);
        var yielded = running is null ? null : await YieldAsync(callerId, running, input.Switch, input.CurrentId, ct);

        if (same is { Status: WorkSessionStatus.Paused })
        {
            // Starting paused work is resuming it.
            Open(same, now, callerId);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("workforce.session.resumed", "WorkSession", same.Id.ToString(), new { reference = WorkPlanService.Reference(ticket), at = now }, ct);
            await hold.CommitAsync(ct);
            await PushYieldedAsync(yielded, ct);
            return await DtoAsync(callerId, same, ct);
        }

        var session = new WorkSession
        {
            MspOrganizationId = ticket.MspOrganizationId, AppUserId = callerId, TicketId = ticket.Id, Ticket = ticket, AllocationId = input.AllocationId,
            Status = WorkSessionStatus.Active, StartedAt = now, UpdatedByUserId = callerId,
        };
        session.Segments.Add(new WorkSessionSegment { MspOrganizationId = ticket.MspOrganizationId, StartedAt = now });
        db.WorkSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.session.started", "WorkSession", session.Id.ToString(),
            new { reference = WorkPlanService.Reference(ticket), ticketId = ticket.Id, allocationId = input.AllocationId, at = now, planned = input.AllocationId is not null }, ct);
        await hold.CommitAsync(ct);
        await PushYieldedAsync(yielded, ct);
        return await DtoAsync(callerId, session, ct);
    }

    public async Task<WorkSessionDto> PauseAsync(Guid callerId, Guid sessionId, WorkSessionStateInput input, CancellationToken ct = default)
    {
        var session = await ControlledAsync(callerId, sessionId, ct);
        if (session.Status != WorkSessionStatus.Active) throw new ValidationFailedException("This work is not running.");
        if (input.Version != session.Version) throw Stale();

        await using var hold = await gate.HoldAsync(session.AppUserId, ct);
        await FreshAsync(session, input.Version, ct);
        if (session.Status != WorkSessionStatus.Active) throw new ValidationFailedException("This work is not running.");
        var now = clock.GetUtcNow();
        Close(session, now);
        session.Status = WorkSessionStatus.Paused;
        session.PauseReason = input.PauseReason;
        Touch(session, callerId);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.session.paused", "WorkSession", session.Id.ToString(),
            new { reference = WorkPlanService.Reference(session.Ticket!), at = now, reason = input.PauseReason.ToString(), activeSeconds = session.ActiveSeconds }, ct);
        await hold.CommitAsync(ct);
        return await DtoAsync(callerId, session, ct);
    }

    public async Task<WorkSessionDto> ResumeAsync(Guid callerId, Guid sessionId, WorkSessionStateInput input, CancellationToken ct = default)
    {
        var session = await ControlledAsync(callerId, sessionId, ct);
        if (session.Status != WorkSessionStatus.Paused) throw new ValidationFailedException("This work is not paused.");
        if (input.Version != session.Version) throw Stale();

        await using var hold = await gate.HoldAsync(session.AppUserId, ct);
        await FreshAsync(session, input.Version, ct);
        if (session.Status != WorkSessionStatus.Paused) throw new ValidationFailedException("This work is not paused.");
        var running = (await OpenSessionsAsync(session.AppUserId, ct)).FirstOrDefault(s => s.Status == WorkSessionStatus.Active && s.Id != session.Id);
        var yielded = running is null ? null : await YieldAsync(callerId, running, input.Switch, input.CurrentId, ct);
        var now = clock.GetUtcNow();
        Open(session, now, callerId);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.session.resumed", "WorkSession", session.Id.ToString(), new { reference = WorkPlanService.Reference(session.Ticket!), at = now }, ct);
        await hold.CommitAsync(ct);
        await PushYieldedAsync(yielded, ct);
        return await DtoAsync(callerId, session, ct);
    }

    public async Task<WorkSessionDto> StopAsync(Guid callerId, Guid sessionId, StopWorkInput input, CancellationToken ct = default)
    {
        var session = await ControlledAsync(callerId, sessionId, ct);
        if (session.Status is not (WorkSessionStatus.Active or WorkSessionStatus.Paused)) throw new ValidationFailedException("This work is already stopped.");
        if (input.Version != session.Version) throw Stale();
        var note = CheckNote(input.Note);

        await using var hold = await gate.HoldAsync(session.AppUserId, ct);
        await FreshAsync(session, input.Version, ct);
        if (session.Status is not (WorkSessionStatus.Active or WorkSessionStatus.Paused)) throw new ValidationFailedException("This work is already stopped.");
        var now = clock.GetUtcNow();
        await FinishAsync(callerId, session, now, note, input.Billable, input.WorkType, input.WorkRole, input.Discard, ct);
        await hold.CommitAsync(ct);

        // The push to the PSA happens after the gate is released: a slow provider must not hold the person's lock.
        if (session.TimeEntryId is { } entryId) await PushAsync(session, entryId, ct);
        return await DtoAsync(callerId, session, ct);
    }

    /// <summary>Closes the clock and writes its time (a Pending entry; the push follows), or throws it away.</summary>
    private async Task FinishAsync(Guid callerId, WorkSession session, DateTimeOffset now, string? note, bool billable, string? workType, string? workRole, bool discard, CancellationToken ct)
    {
        Close(session, now);
        session.EndedAt = now;
        session.Note = note;
        Touch(session, callerId);
        var ticket = session.Ticket!;
        if (discard || session.ActiveSeconds < MinimumLoggedSeconds)
        {
            session.Status = WorkSessionStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("workforce.session.cancelled", "WorkSession", session.Id.ToString(),
                new { reference = WorkPlanService.Reference(ticket), at = now, activeSeconds = session.ActiveSeconds, reason = discard ? "discarded" : "under a minute" }, ct);
            return;
        }
        session.Status = WorkSessionStatus.Completed;
        // Exact to the second (0.0001 h is a third of a second); the display rounds, the record does not.
        var hours = Math.Round(session.ActiveSeconds / 3600m, 4, MidpointRounding.AwayFromZero);
        // The push to the PSA is deferred until the gate is released (pushNow: false): the entry waits as Pending.
        var entry = (await writer.LogAsync(ticket, null, session.AppUserId, hours, billable, note, Blank(workType), Blank(workRole), null, session.StartedAt,
            new { sessionId = session.Id, activeSeconds = session.ActiveSeconds, allocationId = session.AllocationId }, ct, session.Id, pushNow: false)).Entry;
        session.TimeEntryId = entry.Id;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("workforce.session.stopped", "WorkSession", session.Id.ToString(),
            new { reference = WorkPlanService.Reference(ticket), at = now, activeSeconds = session.ActiveSeconds, hours, entryId = entry.Id, planned = session.AllocationId is not null }, ct);
    }

    /// <summary>Sends the entry a stopped session wrote to the ticket's PSA, when it has one. A rejection keeps the entry Failed, for a retry.</summary>
    private async Task PushAsync(WorkSession session, Guid entryId, CancellationToken ct)
    {
        var ticket = session.Ticket!;
        if (ticket.Origin != TicketOrigin.Psa || ticket.PsaConnectionId is not { } connectionId || string.IsNullOrEmpty(ticket.ExternalTicketId)) return;
        var entry = await db.TicketTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null || entry.SyncStatus == TimeEntrySyncStatus.Synced) return;
        IServiceManagementConnector connector;
        try { connector = await connectors.ResolveAsync(connectionId, ct); }
        catch (Exception ex)
        {
            entry.SyncStatus = TimeEntrySyncStatus.Failed;
            entry.SyncError = ex.Message;
            await db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            if (await writer.PushAsync(entry, ticket, connector, ct))
            {
                try { await writer.RecomputeAsync(ticket, connector, ct); } catch (Exception) { /* totals refresh on the next read */ }
            }
        }
        catch (Exception ex)
        {
            // The clock is stopped and its time recorded; only the push failed. Kept as Failed with the reason, for a retry.
            entry.SyncStatus = TimeEntrySyncStatus.Failed;
            entry.SyncError = ex.Message;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// What to do with the clock that is already running when another is to start. Returns the clock
    /// it stopped, if it stopped one, so the caller can push that clock's entry once the gate is released.
    /// </summary>
    private async Task<WorkSession?> YieldAsync(Guid callerId, WorkSession running, ActiveWorkSwitch choice, Guid? agreedId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The person agreed to pause or stop the clock their screen showed; a different one running by now is not theirs to decide about blindly.
        if (choice != ActiveWorkSwitch.None && agreedId is { } agreed && agreed != running.Id) throw Stale();
        switch (choice)
        {
            case ActiveWorkSwitch.PauseCurrent:
                Close(running, now);
                running.Status = WorkSessionStatus.Paused;
                running.PauseReason = WorkPauseReason.None;
                Touch(running, callerId);
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync("workforce.session.paused", "WorkSession", running.Id.ToString(),
                    new { reference = WorkPlanService.Reference(running.Ticket!), at = now, reason = "switched to other work", activeSeconds = running.ActiveSeconds }, ct);
                return null;
            case ActiveWorkSwitch.StopCurrent:
                await FinishAsync(callerId, running, now, null, true, null, null, false, ct);
                return running;
            default:
                var dto = await DtoAsync(callerId, running, ct);
                throw new ConflictException($"You already have active work on {dto.Reference ?? "another ticket"}.", new ActiveWorkProblemDto(dto, true, true));
        }
    }

    /// <summary>The entry of a clock stopped on the way to starting another goes to the PSA now, outside the gate, like any stopped clock's.</summary>
    private async Task PushYieldedAsync(WorkSession? yielded, CancellationToken ct)
    {
        if (yielded?.TimeEntryId is { } entryId) await PushAsync(yielded, entryId, ct);
    }

    // ---- the day ----------------------------------------------------------------------------------

    public async Task<MyDayDto> MyDayAsync(Guid callerId, Guid? appUserId, DateOnly? date, CancellationToken ct = default)
    {
        var person = appUserId is { } other && other != callerId
            ? await access.VisiblePersonAsync(callerId, other, ct)
            : await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerId, ct) ?? throw new NotFoundException("Person");
        var self = person.Id == callerId;
        var cap = await capacity.ForPersonAsync(callerId, person.Id, date, date, ct);
        var day = cap.Days.Count > 0 ? cap.Days[0] : null;
        var theDay = day?.Date ?? date ?? cap.Today;
        var zone = TimeZones.Resolve(cap.TimeZone);
        var now = clock.GetUtcNow();
        var (lo, hi) = Bounds(theDay);

        // Planned: the person's live allocations that start on the day, in their zone.
        var allocations = (await db.WorkAllocations.AsNoTracking().Include(a => a.Ticket)
                .Where(a => a.AppUserId == person.Id && a.StartsAt < hi && a.EndsAt > lo && (a.Status == WorkAllocationStatus.Planned || a.Status == WorkAllocationStatus.Tentative))
                .OrderBy(a => a.StartsAt).ToListAsync(ct))
            .Where(a => WorkforceCalendar.LocalDate(a.StartsAt, zone) == theDay).ToList();
        // Actual: the person's time entries dated the day, and the live clocks not yet written as entries.
        var entries = (await db.TicketTimeEntries.AsNoTracking()
                .Where(e => e.AppUserId == person.Id && e.EntryDate >= lo && e.EntryDate < hi).ToListAsync(ct))
            // And what they entered in the PSA itself, under a login that is linked to them.
            .Concat((await PsaLoggedTime.ForAsync(db, [person.Id], lo, hi, ct)).Select(x => x.Entry))
            .Where(e => WorkforceCalendar.LocalDate(e.EntryDate, zone) == theDay).ToList();
        var sessions = await db.WorkSessions.AsNoTracking().Include(s => s.Ticket).Include(s => s.Segments)
            .Where(s => s.AppUserId == person.Id && ((s.StartedAt >= lo && s.StartedAt < hi) || s.Status == WorkSessionStatus.Active || s.Status == WorkSessionStatus.Paused))
            .OrderBy(s => s.StartedAt).ToListAsync(ct);
        // Every live clock, for the header and the Now card (an overnight clock is recovered whatever day is viewed)...
        var open = sessions.Where(s => s.TimeEntryId == null && s.Status is WorkSessionStatus.Active or WorkSessionStatus.Paused).ToList();
        // ...but a day's figures hold only the clocks that started on it: a clock belongs to the day it started.
        var live = open.Where(s => WorkforceCalendar.LocalDate(s.StartedAt, zone) == theDay).ToList();

        var ticketIds = allocations.Select(a => a.TicketId).Concat(entries.Select(e => e.TicketId)).Concat(live.Select(s => s.TicketId)).Distinct().ToList();
        var visible = await VisibleAsync(callerId, ticketIds, ct);
        var ticketsById = allocations.Select(a => a.Ticket!).Concat(live.Select(s => s.Ticket!)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var missing = ticketIds.Where(id => !ticketsById.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            foreach (var t in await db.Tickets.AsNoTracking().Where(t => missing.Contains(t.Id)).ToListAsync(ct)) ticketsById[t.Id] = t;
        var clientNames = await ClientNamesAsync(ticketsById.Values, ct);
        var canControl = self || await LeadsAsync(callerId, ct);

        var items = new List<MyDayItemDto>();
        foreach (var ticketId in ticketIds)
        {
            var ticket = ticketsById[ticketId];
            var sees = visible.Contains(ticketId);
            var slots = allocations.Where(a => a.TicketId == ticketId)
                .Select(a => new MyDaySlotDto(a.Id, a.StartsAt, a.EndsAt, a.PlannedMinutes, a.Status == WorkAllocationStatus.Tentative, a.IsFixed)).ToList();
            var planned = allocations.Where(a => a.TicketId == ticketId && a.Status == WorkAllocationStatus.Planned).Sum(a => a.PlannedMinutes);
            var tentative = allocations.Where(a => a.TicketId == ticketId && a.Status == WorkAllocationStatus.Tentative).Sum(a => a.PlannedMinutes);
            var ticketEntries = entries.Where(e => e.TicketId == ticketId).ToList();
            var recorded = (int)Math.Round(ticketEntries.Sum(e => e.Hours) * 3600m);
            var session = live.FirstOrDefault(s => s.TicketId == ticketId && s.Status == WorkSessionStatus.Active)
                          ?? live.FirstOrDefault(s => s.TicketId == ticketId);
            var liveSeconds = live.Where(s => s.TicketId == ticketId).Sum(s => Elapsed(s, now));
            var actual = recorded + liveSeconds;
            var finished = TicketStatusRules.Finished(ticket.PortalStatus);
            var state = finished ? WorkItemState.Completed
                : session?.Status == WorkSessionStatus.Active ? WorkItemState.Active
                : session?.Status == WorkSessionStatus.Paused ? WorkItemState.Paused
                : actual > 0 ? WorkItemState.InProgress
                : WorkItemState.Planned;
            var lastEnd = slots.Count == 0 ? (DateTimeOffset?)null : slots.Max(s => s.EndsAt);
            var firstActual = ticketEntries.Select(e => e.EntryDate).Concat(live.Where(s => s.TicketId == ticketId).Select(s => s.StartedAt)).DefaultIfEmpty(now).Min();
            items.Add(new MyDayItemDto(ticketId, sees,
                sees ? await ReferenceAsync(ticket, ct) : null, sees ? ticket.Title : null, sees ? clientNames.GetValueOrDefault(ticketId) : null,
                WorkPlanService.Source(ticket.Origin, ticket.Provider), sees ? ticket.PortalStatus : null, finished, sees ? ticket.PortalPriority : null, sees ? ticket.SlaDueAt : null,
                slots, planned, tentative, actual,
                planned > 0 ? (int)Math.Round(actual / 60.0) - planned : null,
                planned > 0 ? Math.Round(((actual / 60.0) - planned) / planned * 100, 1) : null,
                planned > 0 || tentative > 0, state,
                session is null ? null : await DtoAsync(callerId, session, ct, visible, day, canControl),
                session?.Status == WorkSessionStatus.Active && lastEnd is { } end && now > end,
                ticketEntries.Count(e => e.SyncStatus != TimeEntrySyncStatus.Synced),
                slots.Count > 0 ? slots.Min(s => s.StartsAt) : firstActual));
        }
        items = items.OrderBy(i => i.OrderAt).ToList();

        var currentRow = open.FirstOrDefault(s => s.Status == WorkSessionStatus.Active);
        var currentVisible = currentRow is null ? visible : await VisibleAsync(callerId, [.. ticketIds, currentRow.TicketId], ct);
        var current = currentRow is null ? null : await DtoAsync(callerId, currentRow, ct, currentVisible, live.Contains(currentRow) ? day : await DayOfAsync(callerId, currentRow, ct), canControl);
        var paused = new List<WorkSessionDto>();
        foreach (var s in open.Where(s => s.Status == WorkSessionStatus.Paused))
            paused.Add(await DtoAsync(callerId, s, ct, await VisibleAsync(callerId, [s.TicketId], ct), live.Contains(s) ? day : await DayOfAsync(callerId, s, ct), canControl));
        var summary = new MyDaySummaryDto(
            items.Sum(i => i.PlannedMinutes), items.Sum(i => i.TentativeMinutes), items.Sum(i => i.ActualSeconds),
            items.Where(i => !i.Planned).Sum(i => i.ActualSeconds),
            items.Count(i => i.State == WorkItemState.Completed),
            items.Count(i => i.State is WorkItemState.Active or WorkItemState.Paused or WorkItemState.InProgress),
            items.Count(i => i.State == WorkItemState.Planned),
            items.Where(i => i.State != WorkItemState.Completed).Sum(i => Math.Max(0, i.PlannedMinutes - (int)Math.Round(i.ActualSeconds / 60.0))));
        var unscheduled = self ? await plans.UnscheduledAsync(callerId, ct) : [];
        var canWork = self && (await permissions.ResolveAsync(callerId, Permissions.TicketsLogTime, ct)).Scope is not PermissionScope.None;
        return new MyDayDto(person.Id, person.DisplayName, cap.TimeZone, theDay, cap.Today, day, items, unscheduled, current, paused, summary, canWork);
    }

    public async Task<TeamTodayDto> TeamTodayAsync(Guid callerId, TeamPlanQuery query, CancellationToken ct = default)
    {
        // Today in the organization's zone unless a date is asked for; the range reads one day.
        var theDay = query.From ?? WorkforceCalendar.LocalDate(clock.GetUtcNow(), TimeZones.Resolve(access.OrganizationTimeZone()));
        var range = await capacity.ForTeamRangeAsync(callerId, new TeamRangeQuery(theDay, theDay, query.TeamId, query.DepartmentId, query.SkillIds, query.MatchAllSkills), ct);
        var ids = range.People.Select(p => p.AppUserId).ToList();
        var now = clock.GetUtcNow();
        if (ids.Count == 0) return new TeamTodayDto(theDay, range.TimeZone, [], 0, 0, 0, 0);
        var (lo, hi) = Bounds(theDay);

        var allocations = await db.WorkAllocations.AsNoTracking()
            .Where(a => ids.Contains(a.AppUserId) && a.StartsAt < hi && a.EndsAt > lo && a.Status == WorkAllocationStatus.Planned)
            .Select(a => new { a.AppUserId, a.TicketId, a.StartsAt, a.PlannedMinutes }).ToListAsync(ct);
        var entries = await db.TicketTimeEntries.AsNoTracking()
            .Where(e => e.AppUserId != null && ids.Contains(e.AppUserId.Value) && e.EntryDate >= lo && e.EntryDate < hi)
            .Select(e => new { AppUserId = e.AppUserId!.Value, e.TicketId, e.EntryDate, e.Hours }).ToListAsync(ct);
        // And what each entered in the PSA itself, under a login that is linked to them.
        entries.AddRange((await PsaLoggedTime.ForAsync(db, ids, lo, hi, ct)).Select(x => new { x.AppUserId, x.Entry.TicketId, x.Entry.EntryDate, x.Entry.Hours }));
        var sessions = await db.WorkSessions.AsNoTracking().Include(s => s.Ticket).Include(s => s.Segments)
            .Where(s => ids.Contains(s.AppUserId) && (s.Status == WorkSessionStatus.Active || s.Status == WorkSessionStatus.Paused))
            .ToListAsync(ct);
        var finishedTickets = await db.Tickets.AsNoTracking()
            .Where(t => allocations.Select(a => a.TicketId).Distinct().Contains(t.Id)).Select(t => new { t.Id, t.PortalStatus }).ToListAsync(ct);
        var finished = finishedTickets.Where(t => TicketStatusRules.Finished(t.PortalStatus)).Select(t => t.Id).ToHashSet();
        var visible = await VisibleAsync(callerId, sessions.Select(s => s.TicketId).Distinct().ToList(), ct);
        var leads = await LeadsAsync(callerId, ct);

        var people = new List<TeamTodayPersonDto>();
        foreach (var p in range.People)
        {
            var zone = TimeZones.Resolve(p.TimeZone);
            var day = p.Days.Count > 0 ? p.Days[0] : null;
            var mine = allocations.Where(a => a.AppUserId == p.AppUserId && WorkforceCalendar.LocalDate(a.StartsAt, zone) == theDay).ToList();
            var recorded = (int)Math.Round(entries.Where(e => e.AppUserId == p.AppUserId && WorkforceCalendar.LocalDate(e.EntryDate, zone) == theDay).Sum(e => e.Hours) * 3600m);
            var open = sessions.Where(s => s.AppUserId == p.AppUserId && s.TimeEntryId == null).ToList();
            // The day's seconds are the clocks that started on it; the running clock is shown whatever day it started.
            var ofDay = open.Where(s => WorkforceCalendar.LocalDate(s.StartedAt, zone) == theDay).ToList();
            var liveSeconds = ofDay.Sum(s => Elapsed(s, now));
            var running = open.FirstOrDefault(s => s.Status == WorkSessionStatus.Active);
            var touched = entries.Where(e => e.AppUserId == p.AppUserId && WorkforceCalendar.LocalDate(e.EntryDate, zone) == theDay).Select(e => e.TicketId)
                .Concat(ofDay.Select(s => s.TicketId)).ToHashSet();
            var plannedTickets = mine.Select(a => a.TicketId).Distinct().ToList();
            people.Add(new TeamTodayPersonDto(p.AppUserId, p.DisplayName, p.TimeZone, p.IsSchedulable, p.HasSchedule,
                running is null ? null : await DtoAsync(callerId, running, ct, visible, day, p.AppUserId == callerId || leads),
                open.Count(s => s.Status == WorkSessionStatus.Paused),
                day?.UsableMinutes ?? 0, mine.Sum(a => a.PlannedMinutes), recorded + liveSeconds,
                plannedTickets.Count(t => finished.Contains(t)),
                plannedTickets.Count(t => !finished.Contains(t) && touched.Contains(t)),
                plannedTickets.Count(t => !finished.Contains(t) && !touched.Contains(t))));
        }
        return new TeamTodayDto(theDay, range.TimeZone, people, people.Sum(p => p.PlannedMinutes), people.Sum(p => p.ActualSeconds),
            people.Count(p => p.Current is not null), people.Sum(p => p.PausedCount));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private Task<List<WorkSession>> OpenSessionsAsync(Guid appUserId, CancellationToken ct)
        => db.WorkSessions.Include(s => s.Ticket).Include(s => s.Segments)
            .Where(s => s.AppUserId == appUserId && (s.Status == WorkSessionStatus.Active || s.Status == WorkSessionStatus.Paused))
            .OrderByDescending(s => s.Status == WorkSessionStatus.Active).ThenByDescending(s => s.StartedAt).ToListAsync(ct);

    /// <summary>The session, if the caller may control it: their own, or anyone's for a board lead.</summary>
    private async Task<WorkSession> ControlledAsync(Guid callerId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.WorkSessions.Include(s => s.Ticket).Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                      ?? throw new NotFoundException("Work session");
        if (session.AppUserId != callerId && !await LeadsAsync(callerId, ct)) throw new NotFoundException("Work session");
        return session;
    }

    private async Task<bool> LeadsAsync(Guid callerId, CancellationToken ct)
        => (await permissions.ResolveAsync(callerId, Permissions.BoardsManage, ct)).Scope is PermissionScope.All;

    private async Task FreshAsync(WorkSession session, int version, CancellationToken ct)
    {
        await db.Entry(session).ReloadAsync(ct);
        await db.Entry(session).Collection(s => s.Segments).LoadAsync(ct);
        if (session.Version != version) throw Stale();
    }

    private static ConflictException Stale() => new("This work changed since the screen loaded. Reload and try again.", new { stale = true });

    private void Open(WorkSession session, DateTimeOffset now, Guid callerId)
    {
        // Added through the set, not the tracked parent's collection: a new child carrying its own key
        // is otherwise taken for an existing row to update, and the insert never happens.
        var segment = new WorkSessionSegment { MspOrganizationId = session.MspOrganizationId, SessionId = session.Id, StartedAt = now };
        db.WorkSessionSegments.Add(segment);
        session.Segments.Add(segment);
        session.Status = WorkSessionStatus.Active;
        session.PauseReason = WorkPauseReason.None;
        Touch(session, callerId);
    }

    /// <summary>Closes the open segment, if any, and adds its whole seconds to the session.</summary>
    private static void Close(WorkSession session, DateTimeOffset now)
    {
        var running = session.Segments.FirstOrDefault(s => s.EndedAt == null);
        if (running is null) return;
        running.EndedAt = now;
        running.Seconds = Math.Max(0, (int)Math.Floor((now - running.StartedAt).TotalSeconds));
        session.ActiveSeconds += running.Seconds;
    }

    private static void Touch(WorkSession session, Guid callerId)
    {
        session.UpdatedByUserId = callerId;
        session.Version++;
    }

    /// <summary>Active seconds right now: the closed segments plus the open one, by the server's clock.</summary>
    private static int Elapsed(WorkSession s, DateTimeOffset now)
    {
        var open = s.Segments.FirstOrDefault(x => x.EndedAt == null);
        return s.ActiveSeconds + (open is null ? 0 : Math.Max(0, (int)Math.Floor((now - open.StartedAt).TotalSeconds)));
    }

    private static string? CheckNote(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > NoteMax) throw new ValidationFailedException($"The note is at most {NoteMax} characters.");
        return trimmed;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Instants wide enough to hold a local day in any zone; rows are then matched to the date by the zone itself.</summary>
    private static (DateTimeOffset Lo, DateTimeOffset Hi) Bounds(DateOnly day)
        => (new DateTimeOffset(day.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(day.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private async Task<HashSet<Guid>> VisibleAsync(Guid callerId, IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return [];
        var q = await tickets.VisibleAsync(db.Tickets.AsNoTracking(), callerId, Permissions.TicketsViewAll, ct);
        return (await q.Where(t => ticketIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
    }

    private async Task<Dictionary<Guid, string>> ClientNamesAsync(IEnumerable<Ticket> rows, CancellationToken ct)
    {
        var companyIds = rows.Where(t => t.ClientCompanyId != null).Select(t => t.ClientCompanyId!.Value).Distinct().ToList();
        if (companyIds.Count == 0) return [];
        var names = await db.ClientCompanies.AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Where(t => t.ClientCompanyId is { } c && names.ContainsKey(c)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => names[g.First().ClientCompanyId!.Value]);
    }

    private async Task<WorkSessionDto> DtoAsync(Guid callerId, WorkSession s, CancellationToken ct)
    {
        var visible = await VisibleAsync(callerId, [s.TicketId], ct);
        var canControl = s.AppUserId == callerId || await LeadsAsync(callerId, ct);
        return await DtoAsync(callerId, s, ct, visible, await DayOfAsync(callerId, s, ct), canControl);
    }

    /// <summary>The person's day the session started on, for the outside-the-schedule fact; null when the caller may not read their capacity.</summary>
    private async Task<DayCapacityDto?> DayOfAsync(Guid callerId, WorkSession s, CancellationToken ct)
    {
        try
        {
            var versions = await WorkforceCalendar.VersionsAsync(db, [s.AppUserId], ct);
            var zoneId = WorkforceCalendar.InForce(versions.GetValueOrDefault(s.AppUserId), WorkforceCalendar.LocalDate(s.StartedAt, TimeZoneInfo.Utc))?.TimeZone ?? access.OrganizationTimeZone();
            var date = WorkforceCalendar.LocalDate(s.StartedAt, TimeZones.Resolve(zoneId));
            var cap = await capacity.ForPersonAsync(callerId, s.AppUserId, date, date, ct);
            return cap.Days.Count > 0 ? cap.Days[0] : null;
        }
        catch (DeskException) { return null; }
    }

    private async Task<IReadOnlyList<WorkSessionDto>> DtosAsync(Guid callerId, IReadOnlyList<WorkSession> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var visible = await VisibleAsync(callerId, rows.Select(r => r.TicketId).Distinct().ToList(), ct);
        var canControl = rows.All(r => r.AppUserId == callerId) || await LeadsAsync(callerId, ct);
        // The day a clock started on is read once per person and date, however many clocks started that day.
        var days = new Dictionary<(Guid, DateOnly), DayCapacityDto?>();
        var result = new List<WorkSessionDto>();
        foreach (var r in rows)
        {
            var key = (r.AppUserId, WorkforceCalendar.LocalDate(r.StartedAt, TimeZoneInfo.Utc));
            if (!days.TryGetValue(key, out var day)) days[key] = day = await DayOfAsync(callerId, r, ct);
            result.Add(await DtoAsync(callerId, r, ct, visible, day, canControl));
        }
        return result;
    }

    private async Task<WorkSessionDto> DtoAsync(Guid callerId, WorkSession s, CancellationToken ct, HashSet<Guid> visible, DayCapacityDto? day, bool canControl)
    {
        var ticket = s.Ticket ?? await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == s.TicketId, ct);
        var sees = visible.Contains(s.TicketId);
        var clientName = sees && ticket.ClientCompanyId is { } c ? await db.ClientCompanies.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var open = s.Segments.FirstOrDefault(x => x.EndedAt == null);
        TimeEntrySyncStatus? sync = null; string? syncError = null;
        if (s.TimeEntryId is { } entryId)
        {
            var e = await db.TicketTimeEntries.AsNoTracking().Where(x => x.Id == entryId).Select(x => new { x.SyncStatus, x.SyncError }).FirstOrDefaultAsync(ct);
            sync = e?.SyncStatus; syncError = e?.SyncError;
        }
        // Outside the working window of the day it started on: a fact for the day, never "overtime".
        var outside = day is not null
            ? !day.IsWorkingDay || day.WindowStart is null || day.WindowEnd is null || s.StartedAt < day.WindowStart || s.StartedAt >= day.WindowEnd
            : false;
        return new WorkSessionDto(s.Id, s.AppUserId, s.TicketId, sees, sees ? await ReferenceAsync(ticket, ct) : null, sees ? ticket.Title : null, clientName,
            TicketStatusRules.Finished(ticket.PortalStatus), s.AllocationId, s.Status, s.PauseReason, s.StartedAt, s.EndedAt,
            s.ActiveSeconds, open?.StartedAt, s.TimeEntryId, sync, syncError, s.Note, outside, s.Version, canControl);
    }
}
