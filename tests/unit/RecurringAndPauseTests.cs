using Desk.Api.Controllers;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Step four of the desk: holidays the SLA clock steps over, a clock that stops while the ticket
/// waits on the customer, and tickets that raise themselves on a schedule.
/// </summary>
public class RecurringAndPauseTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static SlaPlan Business(bool pause = true, bool holidays = true) => new()
    {
        MspOrganizationId = Org, Name = "p", ResolveWithinHours = 8, BusinessHoursOnly = true,
        WorkdayStartHour = 9, WorkdayEndHour = 17, WorkingDays = SlaClock.MondayToFriday,
        SkipHolidays = holidays, PauseWhileWaiting = pause,
    };

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // ---- holidays -------------------------------------------------------------------------------

    [Fact]
    public void A_holiday_is_stepped_over_like_a_weekend()
    {
        // Thursday 16:00 with 2 working hours: 1 on Thursday, and Friday is a holiday, so Monday 10:00.
        var holiday = new HashSet<DateOnly> { new(2026, 10, 2) };
        SlaClock.Due(Utc(10, 1, 16), 2, Business(), TimeZoneInfo.Utc, holiday).Should().Be(Utc(10, 5, 10));
        // A plan told not to skip holidays works straight through it.
        SlaClock.Due(Utc(10, 1, 16), 2, Business(holidays: false), TimeZoneInfo.Utc, holiday).Should().Be(Utc(10, 2, 10));
    }

    [Fact]
    public void Working_minutes_between_two_moments_ignore_nights_weekends_and_holidays()
    {
        var holiday = new HashSet<DateOnly> { new(2026, 10, 5) };
        // Friday 16:00 to Tuesday 10:00, Monday a holiday: 60 minutes Friday + 60 Tuesday.
        SlaClock.MinutesBetween(Utc(10, 2, 16), Utc(10, 6, 10), Business(), TimeZoneInfo.Utc, holiday).Should().Be(120);
        SlaClock.MinutesBetween(Utc(10, 6, 10), Utc(10, 2, 16), Business(), TimeZoneInfo.Utc, holiday).Should().Be(-120);
    }

    // ---- pausing --------------------------------------------------------------------------------

    private static Ticket Ticket(DateTimeOffset due, DateTimeOffset? replyDue = null) => new()
    {
        MspOrganizationId = Org, RequesterName = "d", RequesterEmail = "d@t", Title = "t",
        SlaDueAt = due, FirstResponseDueAt = replyDue, PortalStatus = "IN_PROGRESS",
    };

    [Fact]
    public void Waiting_on_the_customer_stops_the_clock_and_moving_on_gives_the_time_back()
    {
        var plan = new SlaPlan { MspOrganizationId = Org, Name = "24x7", ResolveWithinHours = 8, PauseWhileWaiting = true };
        var t = Ticket(due: Utc(10, 1, 14));

        SlaClock.OnStatusChange(t, "WAITING_CUSTOMER", Utc(10, 1, 12), plan, TimeZoneInfo.Utc, null);
        t.SlaPausedAt.Should().Be(Utc(10, 1, 12));
        t.SlaDueAt.Should().Be(Utc(10, 1, 14), "dates move when the clock restarts, never while it is stopped");

        // A day later the customer answers. Two hours were left; two hours are left.
        SlaClock.OnStatusChange(t, "IN_PROGRESS", Utc(10, 2, 12), plan, TimeZoneInfo.Utc, null);
        t.SlaPausedAt.Should().BeNull();
        t.SlaDueAt.Should().Be(Utc(10, 2, 14));
    }

    [Fact]
    public void A_business_hours_pause_over_a_weekend_returns_working_time_not_calendar_time()
    {
        // Friday 15:00, due Friday 17:00: two working hours left when it pauses.
        var t = Ticket(due: Utc(10, 2, 17), replyDue: Utc(10, 2, 16));
        SlaClock.OnStatusChange(t, "ON_HOLD", Utc(10, 2, 15), Business(), TimeZoneInfo.Utc, null);

        // Resumed Monday 09:00: due Monday 11:00, reply Monday 10:00 — not handed the weekend.
        SlaClock.OnStatusChange(t, "IN_PROGRESS", Utc(10, 5, 9), Business(), TimeZoneInfo.Utc, null);
        t.SlaDueAt.Should().Be(Utc(10, 5, 11));
        t.FirstResponseDueAt.Should().Be(Utc(10, 5, 10));
    }

    [Fact]
    public void A_reply_already_given_is_not_moved_and_finishing_just_ends_the_pause()
    {
        var plan = new SlaPlan { MspOrganizationId = Org, Name = "24x7", ResolveWithinHours = 8, PauseWhileWaiting = true };
        var t = Ticket(due: Utc(10, 1, 14), replyDue: Utc(10, 1, 11));
        t.FirstRespondedAt = Utc(10, 1, 10);

        SlaClock.OnStatusChange(t, "WAITING_CUSTOMER", Utc(10, 1, 12), plan, TimeZoneInfo.Utc, null);
        SlaClock.OnStatusChange(t, "IN_PROGRESS", Utc(10, 1, 15), plan, TimeZoneInfo.Utc, null);
        t.FirstResponseDueAt.Should().Be(Utc(10, 1, 11));
        t.SlaDueAt.Should().Be(Utc(10, 1, 17));

        SlaClock.OnStatusChange(t, "WAITING_CUSTOMER", Utc(10, 1, 16), plan, TimeZoneInfo.Utc, null);
        SlaClock.OnStatusChange(t, "RESOLVED", Utc(10, 3, 9), plan, TimeZoneInfo.Utc, null);
        t.SlaPausedAt.Should().BeNull();
        t.SlaDueAt.Should().Be(Utc(10, 1, 17));
    }

    [Fact]
    public void A_plan_that_does_not_pause_leaves_the_clock_running()
    {
        var t = Ticket(due: Utc(10, 1, 14));
        SlaClock.OnStatusChange(t, "WAITING_CUSTOMER", Utc(10, 1, 12), Business(pause: false), TimeZoneInfo.Utc, null);
        t.SlaPausedAt.Should().BeNull();
    }

    // ---- schedules ------------------------------------------------------------------------------

    private static RecurringTicket Schedule(RecurrenceFrequency f, int dayOfWeek = 1, int dayOfMonth = 1, int hour = 9)
        => new() { MspOrganizationId = Org, Title = "t", Frequency = f, DayOfWeek = dayOfWeek, DayOfMonth = dayOfMonth, Hour = hour };

    [Fact]
    public void Each_kind_of_schedule_lands_on_the_right_day()
    {
        var wednesday = Utc(9, 30, 10);
        RecurrenceSchedule.Next(Schedule(RecurrenceFrequency.Weekly, dayOfWeek: 1), wednesday, TimeZoneInfo.Utc).Should().Be(Utc(10, 5, 9));
        RecurrenceSchedule.Next(Schedule(RecurrenceFrequency.Daily), wednesday, TimeZoneInfo.Utc).Should().Be(Utc(10, 1, 9));
        // Friday after nine: the next weekday is Monday.
        RecurrenceSchedule.Next(Schedule(RecurrenceFrequency.Weekdays), Utc(10, 2, 10), TimeZoneInfo.Utc).Should().Be(Utc(10, 5, 9));
        // "Last day of the month" really is: 31 October, then 30 November.
        var last = Schedule(RecurrenceFrequency.Monthly, dayOfMonth: 0);
        // 30 September is itself the last day, but 09:00 has passed by 10:00 — so 31 October.
        RecurrenceSchedule.Next(last, wednesday, TimeZoneInfo.Utc).Should().Be(Utc(10, 31, 9));
        RecurrenceSchedule.Next(last, Utc(10, 31, 10), TimeZoneInfo.Utc).Should().Be(Utc(11, 30, 9));
    }

    [Fact]
    public void A_schedule_is_kept_in_local_time()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "India", "India");
        // 09:00 in India is 03:30 UTC.
        RecurrenceSchedule.Next(Schedule(RecurrenceFrequency.Daily), Utc(9, 30, 4), india)
            .Should().Be(new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero));
    }

    // ---- the service ----------------------------------------------------------------------------

    private sealed record Kit(AdminHarness H, RecurringTicketService Recurring, BoardService Boards, Guid Me, Guid BoardId);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        h.Db.MspOrganizations.Add(new Desk.Domain.Tenancy.MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" });
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "d@techpio.test", IsActive = true };
        h.Db.AppUsers.Add(me);
        await h.Db.SaveChangesAsync();
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        var boards = new BoardService(h.Db, h.Tenant, audit);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var tickets = new InternalTicketService(h.Db, h.Tenant, h.Clock, new RecordingActivity());
        return new Kit(h, new RecurringTicketService(h.Db, h.Tenant, tickets, audit, h.Clock), boards, me.Id, board.Id);
    }

    [Fact]
    public async Task A_due_schedule_raises_its_ticket_with_the_checklist_and_moves_to_its_next_time()
    {
        var k = await BuildAsync();
        var saved = await k.Recurring.SaveAsync(null, new RecurringTicketInput(k.BoardId, "Monthly backup restore test",
            Checklist: "- Pick a random file\n- Restore it to a scratch folder\n\n- Record the result",
            Frequency: RecurrenceFrequency.Daily, Hour: 9), k.Me);
        saved.NextRunAt.Should().BeAfter(k.H.Clock.GetUtcNow());

        // Not yet due: nothing happens.
        (await k.Recurring.RunIfDueAsync(saved.Id)).Should().BeNull();

        k.H.Clock.Advance(TimeSpan.FromDays(1));
        var run = await k.Recurring.RunIfDueAsync(saved.Id);

        run!.Number.Should().Be("INT-000001");
        var tasks = await k.H.Db.TicketTasks.Where(t => t.TicketId == run.TicketId).OrderBy(t => t.SortOrder).Select(t => t.Title).ToListAsync();
        tasks.Should().Equal("Pick a random file", "Restore it to a scratch folder", "Record the result");
        var after = (await k.Recurring.ListAsync()).Single();
        after.LastOutcome.Should().Be("Raised INT-000001.");
        after.NextRunAt.Should().BeAfter(k.H.Clock.GetUtcNow());
        (await k.H.Db.Tickets.SingleAsync()).CreatedByUserId.Should().Be(k.Me);
    }

    [Fact]
    public async Task While_the_last_one_is_open_the_next_is_skipped_and_said_so()
    {
        var k = await BuildAsync();
        var saved = await k.Recurring.SaveAsync(null, new RecurringTicketInput(k.BoardId, "Check the overnight alerts",
            Frequency: RecurrenceFrequency.Daily), k.Me);

        k.H.Clock.Advance(TimeSpan.FromDays(1));
        await k.Recurring.RunIfDueAsync(saved.Id);
        k.H.Clock.Advance(TimeSpan.FromDays(1));
        var second = await k.Recurring.RunIfDueAsync(saved.Id);

        second!.TicketId.Should().BeNull();
        second.Outcome.Should().Be("Skipped — INT-000001 from last time is still open.");
        (await k.H.Db.Tickets.CountAsync()).Should().Be(1);

        // Once the last one is done, the schedule raises again.
        (await k.H.Db.Tickets.SingleAsync()).PortalStatus = "CLOSED";
        await k.H.Db.SaveChangesAsync();
        k.H.Clock.Advance(TimeSpan.FromDays(1));
        (await k.Recurring.RunIfDueAsync(saved.Id))!.Number.Should().Be("INT-000002");
    }

    [Fact]
    public async Task Raise_now_raises_without_moving_the_schedule()
    {
        var k = await BuildAsync();
        var saved = await k.Recurring.SaveAsync(null, new RecurringTicketInput(k.BoardId, "Patch review",
            Frequency: RecurrenceFrequency.Weekly, DayOfWeek: 1), k.Me);

        var run = await k.Recurring.RunNowAsync(saved.Id);

        run.Number.Should().Be("INT-000001");
        (await k.Recurring.ListAsync()).Single().NextRunAt.Should().Be(saved.NextRunAt);
    }

    [Fact]
    public async Task A_schedule_whose_owner_has_left_says_so_instead_of_raising_as_them()
    {
        var k = await BuildAsync();
        var saved = await k.Recurring.SaveAsync(null, new RecurringTicketInput(k.BoardId, "Licence audit",
            Frequency: RecurrenceFrequency.Daily), k.Me);
        (await k.H.Db.AppUsers.SingleAsync(u => u.Id == k.Me)).IsActive = false;
        await k.H.Db.SaveChangesAsync();

        k.H.Clock.Advance(TimeSpan.FromDays(1));
        var run = await k.Recurring.RunIfDueAsync(saved.Id);

        run!.TicketId.Should().BeNull();
        run.Outcome.Should().Contain("no longer active");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(-1)]
    public async Task A_day_of_the_month_that_not_every_month_has_is_refused(int day)
    {
        var k = await BuildAsync();
        await Assert.ThrowsAsync<ValidationFailedException>(() => k.Recurring.SaveAsync(null,
            new RecurringTicketInput(k.BoardId, "Invoices", Frequency: RecurrenceFrequency.Monthly, DayOfMonth: day), k.Me));
    }

    // ---- the status endpoint --------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_board_ticket_to_waiting_pauses_it_and_it_is_not_overdue_while_paused()
    {
        var k = await BuildAsync();
        var plans = new SlaPlanService(k.H.Db, k.H.Tenant, new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock));
        var plan = await plans.SaveAsync(null, new SlaPlanInput("Priority", 1));
        await k.Boards.UpdateAsync(k.BoardId, new BoardInput("Internal IT", "INT", null, DefaultSlaPlanId: plan.Id));
        var raised = await new InternalTicketService(k.H.Db, k.H.Tenant, k.H.Clock, new RecordingActivity())
            .CreateAsync(k.Me, new InternalTicketInput(k.BoardId, "Printer", null));
        var me = new TestCurrentUser(Org, userId: k.Me);
        var status = new TicketStatusController(k.H.Db, new Desk.Infrastructure.Tickets.TicketStatusWriter(k.H.Db, null!, null!), new NoopTicketScopeQuery(), me,
            new Desk.Infrastructure.Sync.OutboundQueue(k.H.Db, k.H.Clock));

        await status.SetStatus(raised.TicketId, new TicketStatusController.SetStatusRequest("WAITING_CUSTOMER"), default);

        var ticket = await k.H.Db.Tickets.SingleAsync();
        ticket.SlaPausedAt.Should().NotBeNull();
        // Past its date by the real clock, but waiting on the customer: not on anybody's overdue list.
        var reads = new Desk.Infrastructure.Tickets.TicketReadService(k.H.Db, new NoopTicketScopeQuery(), me);
        ticket.SlaDueAt = DateTimeOffset.UtcNow.AddHours(-1);
        await k.H.Db.SaveChangesAsync();
        (await reads.SearchAsync(new Desk.Application.Tickets.TicketQuery(OverdueOnly: true))).Total.Should().Be(0);

        await status.SetStatus(raised.TicketId, new TicketStatusController.SetStatusRequest("IN_PROGRESS"), default);
        (await k.H.Db.Tickets.SingleAsync()).SlaPausedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_holiday_can_be_added_once_and_removed()
    {
        var k = await BuildAsync();
        var plans = new SlaPlanService(k.H.Db, k.H.Tenant, new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock));
        var diwali = await plans.AddHolidayAsync(new DateOnly(2026, 11, 8), "Diwali");
        await Assert.ThrowsAsync<ValidationFailedException>(() => plans.AddHolidayAsync(new DateOnly(2026, 11, 8), "Again"));
        (await plans.HolidaysAsync(new DateOnly(2026, 11, 1))).Should().ContainSingle();
        await plans.RemoveHolidayAsync(diwali.Id);
        (await plans.HolidaysAsync()).Should().BeEmpty();
    }
}
