using Desk.Api.Controllers;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Domain.Workforce;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Phase 6: the clock on a piece of work, and a day read as planned against actual. Same world as
/// the planning tests: Jason and Abbie (technicians, NOC), the lead, Sam in another team, an outsider
/// in another organization; everyone 08:30-17:30 Asia/Kolkata; the clock starts on 1 Jan 2026.
/// </summary>
public partial class WorkPlanTests
{
    private sealed class StubResolver(IServiceManagementConnector c) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid connectionId, CancellationToken ct = default) => Task.FromResult(c);
    }

    private static void ClockTo(World w, DateTimeOffset at) => w.Clock.Advance(at - w.Clock.GetUtcNow());
    private static Task Minutes(World w, int minutes) { w.Clock.Advance(TimeSpan.FromMinutes(minutes)); return Task.CompletedTask; }

    [Fact]
    public async Task A_clock_runs_only_inside_its_segments_and_a_stopped_clock_becomes_one_time_entry()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        await w.As(w.Lead).Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "10:00"));
        var allocation = (await jason.Plans.PlanAsync(w.Jason.Id, w.Jason.Id, Monday, Monday)).Allocations.Single();
        var statusBefore = (await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id)).PortalStatus;

        ClockTo(w, At(Monday, "09:00"));
        var s = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id, allocation.Id));
        (s.Status, s.ActiveSeconds, s.RunningSince, s.AllocationId, s.CanControl, s.OutsideSchedule).Should().Be((WorkSessionStatus.Active, 0, At(Monday, "09:00"), allocation.Id, true, false));

        await Minutes(w, 35);
        var paused = await jason.Time.PauseAsync(w.Jason.Id, s.Id, new WorkSessionStateInput(s.Version, WorkPauseReason.WaitingOnReboot));
        (paused.Status, paused.ActiveSeconds, paused.RunningSince, paused.PauseReason).Should().Be((WorkSessionStatus.Paused, 2100, null, WorkPauseReason.WaitingOnReboot));
        await Minutes(w, 25); // waiting is not working: 09:35-10:00 counts for nothing
        var resumed = await jason.Time.ResumeAsync(w.Jason.Id, s.Id, new WorkSessionStateInput(paused.Version));
        (resumed.Status, resumed.ActiveSeconds, resumed.RunningSince).Should().Be((WorkSessionStatus.Active, 2100, At(Monday, "10:00")));
        await Minutes(w, 25);

        // A reloaded screen asks the server what is running and since when; asking creates nothing.
        var active = await jason.Time.ActiveAsync(w.Jason.Id);
        active.Should().ContainSingle().Which.Should().Match<WorkSessionDto>(a => a.Id == s.Id && a.ActiveSeconds == 2100 && a.RunningSince == At(Monday, "10:00"));
        (await w.Db.WorkSessions.CountAsync()).Should().Be(1);
        var day = await jason.Time.MyDayAsync(w.Jason.Id, null, Monday);
        var item = day.Items.Single();
        (item.State, item.ActualSeconds, item.PlannedMinutes, item.VarianceMinutes, item.VariancePercent, item.Planned, item.OverPlannedEnd)
            .Should().Be((WorkItemState.Active, 3600, 60, 0, 0d, true, true), "09:00-10:00 was planned; at 10:25 the clock has run exactly an hour and the slot has ended");
        day.Current!.Id.Should().Be(s.Id);
        (day.Summary.PlannedMinutes, day.Summary.ActualSeconds, day.Summary.InProgress, day.Summary.RemainingPlannedMinutes).Should().Be((60, 3600, 1, 0));

        var stopped = await jason.Time.StopAsync(w.Jason.Id, s.Id, new StopWorkInput(resumed.Version, "Patched and rebooted"));
        (stopped.Status, stopped.ActiveSeconds, stopped.EndedAt, stopped.RunningSince, stopped.TimeEntrySyncStatus).Should().Be((WorkSessionStatus.Completed, 3600, At(Monday, "10:25"), null, TimeEntrySyncStatus.Synced));
        var entry = await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.Id == stopped.TimeEntryId);
        (entry.Hours, entry.AppUserId, entry.TicketId, entry.WorkSessionId, entry.Notes, entry.EntryDate, entry.Source, entry.Billable)
            .Should().Be((1.0000m, w.Jason.Id, w.JasonsTicket.Id, s.Id, "Patched and rebooted", At(Monday, "09:00"), TimeEntrySource.Portal, true));
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.JasonsTicket.Id);
        (ticket.TimeWorkedHours, ticket.PortalStatus).Should().Be((1m, statusBefore), "stopping the clock logs the time and leaves the ticket's status alone");

        // Once: the entry counts, the session no longer does.
        var after = await jason.Time.MyDayAsync(w.Jason.Id, null, Monday);
        (after.Items.Single().State, after.Items.Single().ActualSeconds, after.Summary.ActualSeconds, after.Current).Should().Be((WorkItemState.InProgress, 3600, 3600, null));
        (await w.Db.AuditLog.Where(a => a.Action.StartsWith("workforce.session.")).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync())
            .Should().Equal("workforce.session.started", "workforce.session.paused", "workforce.session.resumed", "workforce.session.stopped");
        (await w.Db.AuditLog.CountAsync(a => a.Action == "ticket.time.logged")).Should().Be(1);
        var again = () => jason.Time.StopAsync(w.Jason.Id, s.Id, new StopWorkInput(stopped.Version));
        (await again.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("This work is already stopped.");
    }

    [Fact]
    public async Task One_clock_runs_at_a_time_and_the_person_says_what_happens_to_the_other()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        ClockTo(w, At(Monday, "09:00"));
        var a = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        await Minutes(w, 2);

        // Another ticket while A runs: refused, with A named and the ways out; nothing is created.
        var clash = () => jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id));
        var refused = (await clash.Should().ThrowAsync<ConflictException>()).Which;
        refused.Message.Should().Be($"You already have active work on {a.Reference}.");
        var problem = refused.Payload.Should().BeOfType<ActiveWorkProblemDto>().Subject;
        (problem.Current.Id, problem.CanPauseCurrent, problem.CanStopCurrent).Should().Be((a.Id, true, true));
        (await w.Db.WorkSessions.CountAsync()).Should().Be(1);
        // The same ticket again (a double click, a second tab): the same clock, not a second one.
        var same = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        (same.Id, same.Version).Should().Be((a.Id, a.Version));

        // The switch names the clock the person saw: a different one running by then is refused, nothing touched.
        var blind = () => jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id, Switch: ActiveWorkSwitch.PauseCurrent, CurrentId: Guid.NewGuid()));
        (await blind.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("This work changed since the screen loaded");
        // Pause A and start B: A keeps its two minutes, B runs.
        var b = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id, Switch: ActiveWorkSwitch.PauseCurrent, CurrentId: a.Id));
        b.Status.Should().Be(WorkSessionStatus.Active);
        var open = await jason.Time.ActiveAsync(w.Jason.Id);
        open.Select(s => (s.Id, s.Status, s.ActiveSeconds)).Should().Equal((b.Id, WorkSessionStatus.Active, 0), (a.Id, WorkSessionStatus.Paused, 120));
        await Minutes(w, 3);
        // Resume A while B runs: the same choice again; stopping B logs its three minutes.
        var paused = open.Single(s => s.Id == a.Id);
        var resumeClash = () => jason.Time.ResumeAsync(w.Jason.Id, a.Id, new WorkSessionStateInput(paused.Version));
        await resumeClash.Should().ThrowAsync<ConflictException>();
        var resumed = await jason.Time.ResumeAsync(w.Jason.Id, a.Id, new WorkSessionStateInput(paused.Version, Switch: ActiveWorkSwitch.StopCurrent));
        resumed.Status.Should().Be(WorkSessionStatus.Active);
        var bRow = await w.Db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == b.Id);
        (bRow.Status, bRow.ActiveSeconds).Should().Be((WorkSessionStatus.Completed, 180));
        (await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.WorkSessionId == b.Id)).Hours.Should().Be(0.05m);
        (await jason.Time.ActiveAsync(w.Jason.Id)).Should().ContainSingle().Which.Id.Should().Be(a.Id);
        // Starting paused work is resuming it: no second session on the ticket.
        await jason.Time.PauseAsync(w.Jason.Id, a.Id, new WorkSessionStateInput(resumed.Version));
        var back = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        (back.Id, back.Status).Should().Be((a.Id, WorkSessionStatus.Active));
        (await w.Db.WorkSessions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Two_tabs_or_two_devices_starting_work_at_once_end_with_one_clock()
    {
        var w = await WorldAsync();
        ClockTo(w, At(Monday, "09:00"));
        var tabA = World.For(AdminHarness.Create(OrgA, w.DbName).Db, OrgA, w.Jason, w.Clock).Time;
        var tabB = World.For(AdminHarness.Create(OrgA, w.DbName).Db, OrgA, w.Jason, w.Clock).Time;
        async Task<(WorkSessionDto? Dto, Exception? Error)> Try(Func<Task<WorkSessionDto>> call)
        {
            try { return (await call(), null); } catch (Exception ex) { return (null, ex); }
        }
        // Different tickets at the same moment: exactly one clock runs and the other tab is told.
        var results = await Task.WhenAll(
            Try(() => tabA.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id))),
            Try(() => tabB.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id))));
        results.Count(r => r.Dto is not null).Should().Be(1);
        results.Single(r => r.Error is not null).Error.Should().BeOfType<ConflictException>();
        (await w.Db.WorkSessions.CountAsync(s => s.Status == WorkSessionStatus.Active)).Should().Be(1);
        // The same ticket from both: both tabs hold the same clock.
        var running = results.Single(r => r.Dto is not null).Dto!;
        var both = await Task.WhenAll(
            Try(() => tabA.StartAsync(w.Jason.Id, new StartWorkInput(running.TicketId))),
            Try(() => tabB.StartAsync(w.Jason.Id, new StartWorkInput(running.TicketId))));
        both.Select(r => r.Dto?.Id).Should().AllBeEquivalentTo(running.Id);
        // Two stops at once: one wins, the other is told the work changed.
        await Minutes(w, 5);
        var stops = await Task.WhenAll(
            Try(() => tabA.StopAsync(w.Jason.Id, running.Id, new StopWorkInput(running.Version))),
            Try(() => tabB.StopAsync(w.Jason.Id, running.Id, new StopWorkInput(running.Version))));
        stops.Count(r => r.Dto is not null).Should().Be(1);
        (await w.Db.TicketTimeEntries.CountAsync(e => e.WorkSessionId == running.Id)).Should().Be(1, "a double stop logs the time once");
        // A screen holding an old version is told to reload rather than acting on what it thinks it sees.
        var fresh = await w.As(w.Jason).Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id));
        var stale = () => tabA.PauseAsync(w.Jason.Id, fresh.Id, new WorkSessionStateInput(fresh.Version + 7));
        (await stale.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("This work changed since the screen loaded");
    }

    [Fact]
    public async Task Unplanned_after_hours_overnight_and_clock_change_work_is_recorded_as_it_was()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        // A critical ticket at 20:00, nothing planned: started at once, said to be outside the schedule, never refused.
        ClockTo(w, At(Monday, "20:00"));
        var s = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id));
        (s.AllocationId, s.OutsideSchedule).Should().Be((null, true));
        // Into the night: Tuesday's day shows nothing of it yet (the clock belongs to Monday), though the clock itself is still there to recover.
        ClockTo(w, At(Tuesday, "01:00"));
        var tuesdayWhileRunning = await jason.Time.MyDayAsync(w.Jason.Id, null, Tuesday);
        (tuesdayWhileRunning.Items.Count, tuesdayWhileRunning.Summary.ActualSeconds, tuesdayWhileRunning.Current?.Id).Should().Be((0, 0, s.Id));
        var stopped = await jason.Time.StopAsync(w.Jason.Id, s.Id, new StopWorkInput(s.Version));
        stopped.ActiveSeconds.Should().Be(5 * 3600);
        var monday = await jason.Time.MyDayAsync(w.Jason.Id, null, Monday);
        var item = monday.Items.Single();
        (item.Planned, item.State, item.ActualSeconds, item.VarianceMinutes, item.VariancePercent, item.PlannedMinutes).Should().Be((false, WorkItemState.InProgress, 18000, null, null, 0), "not planned is not a variance");
        (monday.Summary.UnplannedActualSeconds, monday.Summary.ActualSeconds).Should().Be((18000, 18000));
        (await jason.Time.MyDayAsync(w.Jason.Id, null, Tuesday)).Items.Should().BeEmpty("the work belongs to the day it started");
        // Across a clock change the segments are instants: two real hours are two hours.
        ClockTo(w, new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero));
        var spring = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        w.Clock.Advance(TimeSpan.FromHours(2));
        (await jason.Time.StopAsync(w.Jason.Id, spring.Id, new StopWorkInput(spring.Version))).ActiveSeconds.Should().Be(7200);
    }

    [Fact]
    public async Task A_clock_under_a_minute_or_thrown_away_logs_nothing_and_a_manual_entry_counts_like_a_clock()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        ClockTo(w, At(Monday, "09:00"));
        var blip = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        w.Clock.Advance(TimeSpan.FromSeconds(40));
        var cancelled = await jason.Time.StopAsync(w.Jason.Id, blip.Id, new StopWorkInput(blip.Version));
        (cancelled.Status, cancelled.TimeEntryId).Should().Be((WorkSessionStatus.Cancelled, null));
        var mistake = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        await Minutes(w, 10);
        (await jason.Time.StopAsync(w.Jason.Id, mistake.Id, new StopWorkInput(mistake.Version, Discard: true))).Status.Should().Be(WorkSessionStatus.Cancelled);
        (await w.Db.TicketTimeEntries.CountAsync()).Should().Be(0);
        (await w.Db.AuditLog.CountAsync(a => a.Action == "workforce.session.cancelled")).Should().Be(2);
        // Thirty minutes typed in: the same kind of hour, in the same totals.
        (await jason.Writer.LogAsync(await w.Db.Tickets.SingleAsync(t => t.Id == w.JasonsTicket.Id), null, w.Jason.Id, 0.5m, true, "Reviewed backup failure", null, null, null, At(Monday, "11:00"), null, default)).Totals!.TimeWorkedHours.Should().Be(0.5m);
        var day = await jason.Time.MyDayAsync(w.Jason.Id, null, Monday);
        (day.Items.Single().ActualSeconds, day.Items.Single().State, day.Summary.ActualSeconds).Should().Be((1800, WorkItemState.InProgress, 1800));
    }

    [Fact]
    public async Task Time_on_a_PSA_ticket_is_pushed_once_and_a_rejected_push_keeps_the_time_for_a_retry()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        var ticket = await w.Db.Tickets.AsNoTracking().SingleAsync(t => t.Id == w.Autotask.Id);
        ClockTo(w, At(Monday, "09:00"));
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "TE-1", null);
        var s = await lead.Time.StartAsync(w.Lead.Id, new StartWorkInput(w.Autotask.Id));
        await Minutes(w, 30);
        var stopped = await lead.Time.StopAsync(w.Lead.Id, s.Id, new StopWorkInput(s.Version, "Checked the mailbox"));
        stopped.TimeEntrySyncStatus.Should().Be(TimeEntrySyncStatus.Synced);
        var entry = await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.WorkSessionId == s.Id);
        (entry.ExternalEntryId, entry.Hours, entry.SyncStatus).Should().Be(("TE-1", 0.5m, TimeEntrySyncStatus.Synced));
        w.Connector.PushedTime.Should().ContainSingle().Which.Entry.Should().Match<UnifiedTimeEntryCreateRequest>(e => e.Hours == 0.5m && e.Notes == "Checked the mailbox");

        // The PSA now holds that entry and hands it back on every read: the panel shows ONE row, ours.
        w.Connector.TimeEntries[ticket.ExternalTicketId!] = [new UnifiedTimeEntry("TE-1", "29682887", 0.5m, true, At(Monday, "09:00"), "Checked the mailbox")];
        var controller = new TicketTimeController(w.Db, new StubResolver(w.Connector), null!, lead.Scope,
            new TestCurrentUser(OrgA, userId: w.Lead.Id, name: w.Lead.DisplayName, permissions: new HashSet<string> { Permissions.TicketsLogTime, Permissions.BoardsManage }), lead.Audit);
        var rows = ((Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.List(ticket.Id, default)).Value as IEnumerable<TicketTimeController.TimeRow>;
        rows!.Should().ContainSingle().Which.Should().Match<TicketTimeController.TimeRow>(r => r.ExternalId == "TE-1" && r.Source == "Portal" && r.Hours == 0.5m);
        (await w.Db.TicketTimeEntries.CountAsync(e => e.TicketId == ticket.Id)).Should().Be(1);

        // The PSA is down for the next one: the time is kept here, marked, counted, and retried without a duplicate.
        w.Connector.TimeEntryFailure = new ConnectorException(ConnectorFailureKind.Timeout, "Autotask unavailable");
        var s2 = await lead.Time.StartAsync(w.Lead.Id, new StartWorkInput(w.Autotask.Id));
        await Minutes(w, 15);
        var failed = await lead.Time.StopAsync(w.Lead.Id, s2.Id, new StopWorkInput(s2.Version));
        (failed.Status, failed.TimeEntrySyncStatus, failed.TimeEntrySyncError).Should().Be((WorkSessionStatus.Completed, TimeEntrySyncStatus.Failed, "Autotask unavailable"));
        var day = await lead.Time.MyDayAsync(w.Lead.Id, null, Monday);
        (day.Items.Single().ActualSeconds, day.Items.Single().EntriesNotSynced).Should().Be((45 * 60, 1));
        w.Connector.TimeEntryFailure = null;
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "TE-2", null);
        await controller.Retry(ticket.Id, failed.TimeEntryId!.Value, default);
        var retried = await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.Id == failed.TimeEntryId);
        (retried.SyncStatus, retried.ExternalEntryId).Should().Be((TimeEntrySyncStatus.Synced, "TE-2"));
        (await w.Db.TicketTimeEntries.CountAsync(e => e.TicketId == ticket.Id)).Should().Be(2, "two sessions, two entries, however many pushes");
        w.Connector.PushedTime.Should().HaveCount(2, "the first push and the retry; the rejected attempt was never accepted by the PSA");

        // A clock stopped on the way to starting another is pushed too, once the gate is released.
        w.Connector.NextTimeEntryResult = new CreateTimeEntryResult(true, "TE-3", null);
        var s3 = await lead.Time.StartAsync(w.Lead.Id, new StartWorkInput(w.Autotask.Id));
        await Minutes(w, 5);
        var next = await lead.Time.StartAsync(w.Lead.Id, new StartWorkInput(w.OpenTicket.Id, Switch: ActiveWorkSwitch.StopCurrent));
        next.Status.Should().Be(WorkSessionStatus.Active);
        var yielded = await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.WorkSessionId == s3.Id);
        (yielded.SyncStatus, yielded.ExternalEntryId, yielded.Hours).Should().Be((TimeEntrySyncStatus.Synced, "TE-3", 0.0833m));
        w.Connector.PushedTime.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_finished_ticket_takes_no_clock_and_nobody_outside_reaches_a_session()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        ClockTo(w, At(Monday, "09:00"));
        var closed = await w.Db.Tickets.SingleAsync(t => t.Id == w.JasonsTicket.Id);
        closed.PortalStatus = "CLOSED";
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var finished = () => jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        (await finished.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("This ticket is finished; there is nothing left to work on.");
        // A PSA ticket the PSA has not accepted yet takes no clock: its hour could never reach the PSA.
        var unsynced = await w.Db.Tickets.SingleAsync(t => t.Id == w.Autotask.Id);
        unsynced.ExternalTicketId = null;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var notYet = () => w.As(w.Lead).Time.StartAsync(w.Lead.Id, new StartWorkInput(w.Autotask.Id));
        (await notYet.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("This ticket is not yet synced to the PSA, so time cannot be logged.");

        var s = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.OpenTicket.Id));
        // Another organization: no session, no ticket, no person, no team.
        var outsider = World.For(AdminHarness.Create(OrgB, w.DbName).Db, OrgB, w.Outsider, w.Clock).Time;
        (await outsider.ActiveAsync(w.Outsider.Id)).Should().BeEmpty();
        var stop = () => outsider.StopAsync(w.Outsider.Id, s.Id, new StopWorkInput(s.Version));
        (await stop.Should().ThrowAsync<NotFoundException>()).WithMessage("Work session was not found.");
        var start = () => outsider.StartAsync(w.Outsider.Id, new StartWorkInput(w.OpenTicket.Id));
        (await start.Should().ThrowAsync<NotFoundException>()).WithMessage("Ticket was not found.");
        var day = () => outsider.MyDayAsync(w.Outsider.Id, w.Jason.Id, Monday);
        (await day.Should().ThrowAsync<NotFoundException>()).WithMessage("Person was not found.");
        (await outsider.TeamTodayAsync(w.Outsider.Id, new TeamPlanQuery(Monday))).People.Should().NotContain(p => p.AppUserId == w.Jason.Id || p.AppUserId == w.Abbie.Id, "another organization's people are not theirs to see");
        // A colleague is not a lead: Jason's clock is not theirs to touch. The lead may stop a runaway clock.
        var abbie = () => w.As(w.Abbie).Time.PauseAsync(w.Abbie.Id, s.Id, new WorkSessionStateInput(s.Version));
        await abbie.Should().ThrowAsync<NotFoundException>();
        var byLead = await w.As(w.Lead).Time.PauseAsync(w.Lead.Id, s.Id, new WorkSessionStateInput(s.Version));
        byLead.Status.Should().Be(WorkSessionStatus.Paused);
        (await w.Db.WorkSessions.AsNoTracking().SingleAsync(x => x.Id == s.Id)).UpdatedByUserId.Should().Be(w.Lead.Id);
    }

    [Fact]
    public async Task Team_today_shows_who_is_working_on_what_within_the_asker_scope()
    {
        var w = await WorldAsync();
        var lead = w.As(w.Lead);
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.JasonsTicket, w.Jason, Monday, "09:00", "11:00"));
        await lead.Plans.CreateAsync(w.Lead.Id, Place(w.OpenTicket, w.Abbie, Monday, "14:00", "15:00"));
        ClockTo(w, At(Monday, "09:30"));
        var s = await w.As(w.Jason).Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        await Minutes(w, 20);

        var team = await lead.Time.TeamTodayAsync(w.Lead.Id, new TeamPlanQuery(Monday));
        team.People.Select(p => p.DisplayName).Should().Contain(["Jason Carter", "Abbie Noor", "Lena Lead"]).And.NotContain("Sam Shah");
        var jason = team.People.Single(p => p.AppUserId == w.Jason.Id);
        (jason.Current!.Id, jason.PlannedMinutes, jason.ActualSeconds, jason.InProgress, jason.NotStarted, jason.UsableMinutes).Should().Be((s.Id, 120, 1200, 1, 0, 480));
        var abbie = team.People.Single(p => p.AppUserId == w.Abbie.Id);
        (abbie.Current, abbie.PlannedMinutes, abbie.ActualSeconds, abbie.NotStarted).Should().Be((null, 60, 0, 1));
        (team.Working, team.PlannedMinutes, team.ActualSeconds).Should().Be((1, 180, 1200));
        // A manager opens Jason's day and may stop his clock, but it is not their own day to work.
        var his = await lead.Time.MyDayAsync(w.Lead.Id, w.Jason.Id, Monday);
        (his.AppUserId, his.CanWork, his.Current!.CanControl, his.Unscheduled.Count).Should().Be((w.Jason.Id, false, true, 0));
        // A technician's team is themselves.
        (await w.As(w.Jason).Time.TeamTodayAsync(w.Jason.Id, new TeamPlanQuery(Monday))).People.Select(p => p.AppUserId).Should().Equal(w.Jason.Id);
    }

    [Fact]
    public async Task Someone_else_changing_a_persons_time_must_say_why_and_the_change_is_kept_with_both_values()
    {
        var w = await WorldAsync();
        var jason = w.As(w.Jason);
        ClockTo(w, At(Monday, "09:00"));
        var s = await jason.Time.StartAsync(w.Jason.Id, new StartWorkInput(w.JasonsTicket.Id));
        w.Clock.Advance(TimeSpan.FromMinutes(150)); // the clock ran on through lunch by mistake
        var stopped = await jason.Time.StopAsync(w.Jason.Id, s.Id, new StopWorkInput(s.Version));
        var entryId = stopped.TimeEntryId!.Value;

        TicketTimeController As(AppUser who, params string[] keys) => new(w.Db, new StubResolver(w.Connector), null!, w.As(who).Scope,
            new TestCurrentUser(OrgA, userId: who.Id, name: who.DisplayName, permissions: keys.ToHashSet()), w.As(who).Audit);
        // The person corrects their own: no reason needed.
        await As(w.Jason, Permissions.TicketsLogTime).Update(w.JasonsTicket.Id, entryId.ToString(), new TicketTimeController.UpdateTimeRequest(2.0m, null, null), default);
        (await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.Id == entryId)).Hours.Should().Be(2.0m);
        // A lead changing someone else's must say why; the audit keeps who, from, to and why.
        var lead = As(w.Lead, Permissions.TicketsLogTime, Permissions.BoardsManage);
        var silent = () => lead.Update(w.JasonsTicket.Id, entryId.ToString(), new TicketTimeController.UpdateTimeRequest(1.75m, null, null), default);
        (await silent.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Be("Say why the time is being changed (at least 5 characters).");
        await lead.Update(w.JasonsTicket.Id, entryId.ToString(), new TicketTimeController.UpdateTimeRequest(1.75m, null, null, "Timer ran through lunch"), default);
        (await w.Db.TicketTimeEntries.AsNoTracking().SingleAsync(e => e.Id == entryId)).Hours.Should().Be(1.75m);
        var edit = await w.Db.AuditLog.Where(a => a.Action == "ticket.time.edited").OrderBy(a => a.CreatedAt).LastAsync();
        edit.DetailJson.Should().Contain("Timer ran through lunch").And.Contain("1.75").And.Contain("2.0");
        edit.DetailJson.Should().Contain(w.Lead.Id.ToString(), "who changed it is kept with the change");
        // Someone who is neither: not theirs to change.
        var abbie = () => As(w.Abbie, Permissions.TicketsLogTime).Update(w.JasonsTicket.Id, entryId.ToString(), new TicketTimeController.UpdateTimeRequest(0.5m, null, null, "because"), default);
        await abbie.Should().ThrowAsync<ForbiddenException>();
    }
}
