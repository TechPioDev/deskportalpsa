using Desk.Api.Controllers;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Step three of the desk: SLA plans that set when board work is owed, canned responses for the reply
/// box, and the task list inside a ticket that has to be finished before the ticket can close.
/// </summary>
public class DeskToolsTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // ---- the SLA clock --------------------------------------------------------------------------

    private static SlaPlan Plan(bool business, int start = 9, int end = 18, int days = SlaClock.MondayToFriday)
        => new() { MspOrganizationId = Org, Name = "p", ResolveWithinHours = 1, BusinessHoursOnly = business,
                   WorkdayStartHour = start, WorkdayEndHour = end, WorkingDays = days };

    [Fact]
    public void Round_the_clock_is_plain_addition()
    {
        var friday = new DateTimeOffset(2026, 10, 2, 17, 30, 0, TimeSpan.Zero);
        SlaClock.Due(friday, 4, Plan(business: false), TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Business_hours_spend_only_the_hours_the_desk_is_open()
    {
        // 17:30 Friday with four working hours to go: half an hour on Friday, three and a half on
        // Monday. Not 21:30 Friday, which is what "four hours" means to a clock that never closes.
        var friday = new DateTimeOffset(2026, 10, 2, 17, 30, 0, TimeSpan.Zero);
        SlaClock.Due(friday, 4, Plan(business: true), TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Business_hours_are_counted_in_the_organizations_own_time_zone()
    {
        // Built rather than looked up: this test is about the clock's arithmetic, and whether a host
        // knows "Asia/Kolkata" by that name depends on the host (Linux does; a Windows box without
        // ICU does not), which is the zone resolver's concern, not this one's.
        var india = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "India", "India");
        // 12:00 UTC on a Monday is 17:30 in India: half an hour left that day, then Tuesday from 09:00.
        var raised = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        var due = SlaClock.Due(raised, 2, Plan(business: true), india);

        // Tuesday 10:30 in India, which is 05:00 UTC.
        due.Should().Be(new DateTimeOffset(2026, 9, 29, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_raise_before_opening_starts_the_clock_when_the_desk_opens()
    {
        var early = new DateTimeOffset(2026, 9, 29, 6, 0, 0, TimeSpan.Zero);
        SlaClock.Due(early, 1, Plan(business: true), TimeZoneInfo.Utc)
            .Should().Be(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void An_unknown_time_zone_falls_back_to_UTC_rather_than_failing_the_ticket()
        => SlaClock.Zone("Not/AZone").Should().Be(TimeZoneInfo.Utc);

    // ---- plans become due dates -----------------------------------------------------------------

    private sealed record Kit(AdminHarness H, BoardService Boards, InternalTicketService Tickets, SlaPlanService Plans, Guid Me);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        h.Db.MspOrganizations.Add(new Desk.Domain.Tenancy.MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" });
        var me = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "d@techpio.test", IsActive = true };
        h.Db.AppUsers.Add(me);
        await h.Db.SaveChangesAsync();
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return new Kit(h, new BoardService(h.Db, h.Tenant, audit),
            new InternalTicketService(h.Db, h.Tenant, h.Clock, new RecordingActivity()),
            new SlaPlanService(h.Db, h.Tenant, audit), me.Id);
    }

    [Fact]
    public async Task A_topics_plan_sets_both_the_resolve_and_the_reply_dates()
    {
        var k = await BuildAsync();
        var plan = await k.Plans.SaveAsync(null, new SlaPlanInput("Priority", 8, FirstResponseWithinHours: 1));
        var board = await k.Boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var topic = await k.Boards.AddTopicAsync(board.Id, new BoardTopicInput("Outage", SlaPlanId: plan.Id));

        var raised = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(board.Id, "Switch down", null, BoardTopicId: topic.Id));

        var ticket = await k.H.Db.Tickets.SingleAsync(t => t.Id == raised.TicketId);
        var now = k.H.Clock.GetUtcNow();
        ticket.SlaPlanId.Should().Be(plan.Id);
        ticket.SlaDueAt.Should().Be(now.AddHours(8));
        ticket.FirstResponseDueAt.Should().Be(now.AddHours(1));
    }

    [Fact]
    public async Task A_board_default_plan_applies_when_the_topic_names_none_and_a_typed_date_beats_both()
    {
        var k = await BuildAsync();
        var plan = await k.Plans.SaveAsync(null, new SlaPlanInput("Standard", 24, FirstResponseWithinHours: 4));
        var board = await k.Boards.CreateAsync(new BoardInput("Internal IT", "INT", null, DefaultSlaPlanId: plan.Id));
        var now = k.H.Clock.GetUtcNow();

        var byDefault = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(board.Id, "Renew certificate", null));
        (await k.H.Db.Tickets.SingleAsync(t => t.Id == byDefault.TicketId)).SlaDueAt.Should().Be(now.AddHours(24));

        var typed = now.AddDays(3);
        var chosen = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(board.Id, "Audit licences", null, DueAt: typed));
        var t = await k.H.Db.Tickets.SingleAsync(x => x.Id == chosen.TicketId);
        // Somebody typed a date: that is the promise. The reply time still comes from the plan.
        t.SlaDueAt.Should().Be(typed);
        t.FirstResponseDueAt.Should().Be(now.AddHours(4));
    }

    [Fact]
    public async Task A_retired_plan_is_no_longer_applied_and_cannot_be_chosen_again()
    {
        var k = await BuildAsync();
        var plan = await k.Plans.SaveAsync(null, new SlaPlanInput("Old", 24));
        var board = await k.Boards.CreateAsync(new BoardInput("Internal IT", "INT", null, DefaultSlaPlanId: plan.Id));
        await k.Plans.SetActiveAsync(plan.Id, false);

        var raised = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(board.Id, "After retirement", null));
        (await k.H.Db.Tickets.SingleAsync(t => t.Id == raised.TicketId)).SlaDueAt.Should().BeNull();

        await Assert.ThrowsAsync<ValidationFailedException>(
            () => k.Boards.AddTopicAsync(board.Id, new BoardTopicInput("Anything", SlaPlanId: plan.Id)));
    }

    [Theory]
    [InlineData(9, 9)]    // opens and closes at once: nothing, or everything — round the clock says the latter plainly
    [InlineData(0, 24)]
    public async Task Working_hours_that_open_and_close_at_the_same_time_are_refused(int start, int end)
    {
        var k = await BuildAsync();
        await Assert.ThrowsAsync<ValidationFailedException>(() => k.Plans.SaveAsync(null,
            new SlaPlanInput("Nights", 8, BusinessHoursOnly: true, WorkdayStartHour: start, WorkdayEndHour: end)));
    }

    [Fact]
    public async Task A_night_shift_is_a_plan_that_can_be_saved()
    {
        var k = await BuildAsync();
        var plan = await k.Plans.SaveAsync(null,
            new SlaPlanInput("Night shift", 8, BusinessHoursOnly: true, WorkdayStartHour: 22, WorkdayEndHour: 6));
        plan.Should().BeEquivalentTo(new { WorkdayStartHour = 22, WorkdayEndHour = 6 });
    }

    [Fact]
    public async Task A_reply_promise_later_than_the_resolve_promise_is_refused()
    {
        var k = await BuildAsync();
        await Assert.ThrowsAsync<ValidationFailedException>(
            () => k.Plans.SaveAsync(null, new SlaPlanInput("Backwards", 4, FirstResponseWithinHours: 8)));
    }

    // ---- tasks ----------------------------------------------------------------------------------

    private static async Task<(Kit K, Guid TicketId, TicketTaskService Tasks)> TicketWithTasksAsync()
    {
        var k = await BuildAsync();
        var board = await k.Boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var raised = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(board.Id, "New starter laptop", null));
        var me = new TestCurrentUser(Org, userId: k.Me);
        return (k, raised.TicketId, new TicketTaskService(k.H.Db, k.H.Tenant, me, new NoopTicketScopeQuery(), k.H.Clock));
    }

    [Fact]
    public async Task Tasks_keep_their_order_and_record_who_ticked_them()
    {
        var (_, ticketId, tasks) = await TicketWithTasksAsync();
        await tasks.AddAsync(ticketId, "Image the laptop", null);
        await tasks.AddAsync(ticketId, "Enrol in Intune", null);
        var list = await tasks.AddAsync(ticketId, "Hand it over", null);

        list = await tasks.MoveAsync(list[2].Id, -1);
        list.Select(t => t.Title).Should().Equal("Image the laptop", "Hand it over", "Enrol in Intune");

        list = await tasks.SetDoneAsync(list[0].Id, true);
        list[0].Should().BeEquivalentTo(new { IsDone = true, DoneByName = "Dalbir" });
        list[0].DoneAt.Should().NotBeNull();

        list = await tasks.SetDoneAsync(list[0].Id, false);
        list[0].Should().BeEquivalentTo(new { IsDone = false, DoneAt = (DateTimeOffset?)null, DoneByName = (string?)null });
    }

    [Fact]
    public async Task A_ticket_with_open_tasks_cannot_be_closed_from_the_portal()
    {
        var (k, ticketId, tasks) = await TicketWithTasksAsync();
        var list = await tasks.AddAsync(ticketId, "Update the asset register", null);
        var me = new TestCurrentUser(Org, userId: k.Me);
        // A board ticket never reaches the provider branch, so no connector is needed.
        var status = new TicketStatusController(k.H.Db, null!, null!, new NoopTicketScopeQuery(), me);

        var refused = await Assert.ThrowsAsync<ValidationFailedException>(
            () => status.SetStatus(ticketId, new TicketStatusController.SetStatusRequest("CLOSED"), default));
        refused.Message.Should().Contain("still open");
        (await k.H.Db.Tickets.SingleAsync(t => t.Id == ticketId)).PortalStatus.Should().Be("NEW");

        // Moving it along is not closing it, so that is still allowed.
        await status.SetStatus(ticketId, new TicketStatusController.SetStatusRequest("IN_PROGRESS"), default);

        await tasks.SetDoneAsync(list[0].Id, true);
        await status.SetStatus(ticketId, new TicketStatusController.SetStatusRequest("CLOSED"), default);
        (await k.H.Db.Tickets.SingleAsync(t => t.Id == ticketId)).PortalStatus.Should().Be("CLOSED");
    }

    [Fact]
    public async Task A_blank_task_or_one_for_somebody_who_has_left_is_refused()
    {
        var (_, ticketId, tasks) = await TicketWithTasksAsync();
        await Assert.ThrowsAsync<ValidationFailedException>(() => tasks.AddAsync(ticketId, "   ", null));
        await Assert.ThrowsAsync<ValidationFailedException>(() => tasks.AddAsync(ticketId, "Call them", Guid.NewGuid()));
    }

    // ---- first response -------------------------------------------------------------------------

    [Fact]
    public async Task The_first_staff_note_is_the_first_response_and_a_later_one_does_not_move_it()
    {
        var (k, ticketId, _) = await TicketWithTasksAsync();
        // A board ticket takes the local-note path, so neither connector nor mapping is reached.
        var commands = new TicketCommandService(k.H.Db, null!, new Desk.Application.Mapping.MappingEngine(),
            new Desk.Infrastructure.Sync.SyncEventStore(k.H.Db, k.H.Clock), new NoopTicketScopeQuery(), k.H.Clock,
            new RecordingActivity());

        await commands.AddStaffCommentAsync(k.Me, "Dalbir", ticketId, "Picked this up", isPublic: false);
        var first = (await k.H.Db.Tickets.SingleAsync(t => t.Id == ticketId)).FirstRespondedAt;
        first.Should().NotBeNull();

        k.H.Clock.Advance(TimeSpan.FromHours(2));
        await commands.AddStaffCommentAsync(k.Me, "Dalbir", ticketId, "Still on it", isPublic: false);
        (await k.H.Db.Tickets.SingleAsync(t => t.Id == ticketId)).FirstRespondedAt.Should().Be(first);
    }

    // ---- canned responses -----------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_is_offered_the_responses_for_everywhere_and_for_its_own_board_only()
    {
        var k = await BuildAsync();
        var ours = await k.Boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var theirs = await k.Boards.CreateAsync(new BoardInput("Projects", "PRJ", null));
        var raised = await k.Tickets.CreateAsync(k.Me, new InternalTicketInput(ours.Id, "Printer", null));
        var me = new TestCurrentUser(Org, userId: k.Me);
        var canned = new CannedResponseService(k.H.Db, k.H.Tenant, me, new NoopTicketScopeQuery(),
            new AuditWriter(k.H.Db, me, k.H.Tenant, k.H.Clock));

        await canned.SaveAsync(null, new CannedResponseInput("Received", "Thanks, we have {ticket.number}."));
        await canned.SaveAsync(null, new CannedResponseInput("IT restart", "Please restart.", ours.Id));
        await canned.SaveAsync(null, new CannedResponseInput("Project kickoff", "Kickoff booked.", theirs.Id));

        (await canned.ListAsync(raised.TicketId)).Select(r => r.Name).Should().BeEquivalentTo("Received", "IT restart");
        (await canned.ListAsync()).Should().HaveCount(3);

        await Assert.ThrowsAsync<ValidationFailedException>(
            () => canned.SaveAsync(null, new CannedResponseInput("received", "A second one")));
    }
}
