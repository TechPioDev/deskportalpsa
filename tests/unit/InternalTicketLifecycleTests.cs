using Desk.Api.Controllers;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
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
/// A ticket on the team's own board, from raised to done and back: its details changed after the
/// fact, resolved with what fixed it, reopened when it was not, its time kept honest, and every step
/// on its History with who did it.
/// </summary>
public class InternalTicketLifecycleTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record Kit(AdminHarness H, AuditWriter Audit, InternalTicketService Tickets, TicketStatusWriter Status,
        Board Board, AppUser Anika, AppUser Arjun);

    private static async Task<Kit> KitAsync(bool requireResolution = false)
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        var anika = new AppUser { MspOrganizationId = Org, DisplayName = "Anika", Email = "anika@techpio.test", IsActive = true };
        var arjun = new AppUser { MspOrganizationId = Org, DisplayName = "Arjun", Email = "arjun@techpio.test", IsActive = true };
        var board = new Board { MspOrganizationId = Org, Name = "Office IT", Key = "OIT", Kind = BoardKind.Internal, RequireResolution = requireResolution };
        h.Db.AddRange(anika, arjun, board);
        await h.Db.SaveChangesAsync();
        return new Kit(h, audit, new InternalTicketService(h.Db, h.Tenant, h.Clock, new RecordingActivity(), audit),
            new TicketStatusWriter(h.Db, null!, null!, audit), board, anika, arjun);
    }

    private static async Task<Ticket> RaiseAsync(Kit k, string title = "Meeting room screen flickers")
    {
        var created = await k.Tickets.CreateAsync(k.Anika.Id, new InternalTicketInput(k.Board.Id, title, "Since Monday", Priority: "NORMAL"));
        return await k.H.Db.Tickets.SingleAsync(t => t.Id == created.TicketId);
    }

    private static Task<List<string>> AuditsAsync(Kit k, Ticket t)
        => k.H.Db.AuditLog.Where(a => a.EntityType == "Ticket" && a.EntityId == t.Id.ToString()).Select(a => a.Action).ToListAsync();

    // ── Editing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_board_ticket_s_details_can_be_changed_and_only_the_changes_are_recorded()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        var due = k.H.Clock.GetUtcNow().AddDays(2);

        await k.Tickets.EditAsync(t, new InternalTicketEdit("Meeting room 2 screen flickers", "Since Monday", "HIGH", due, Category: "AV"));

        t.Should().BeEquivalentTo(new { Title = "Meeting room 2 screen flickers", PortalPriority = "HIGH", SlaDueAt = (DateTimeOffset?)due, PortalCategory = "AV" });
        var edit = await k.H.Db.AuditLog.SingleAsync(a => a.Action == "ticket.edited");
        edit.DetailJson.Should().Contain("priority").And.Contain("HIGH").And.NotContain("description", "the description did not change");

        // Saving the form untouched is not an edit.
        await k.Tickets.EditAsync(t, new InternalTicketEdit(t.Title, t.Description, t.PortalPriority, t.SlaDueAt, Category: "AV"));
        (await k.H.Db.AuditLog.CountAsync(a => a.Action == "ticket.edited")).Should().Be(1);
    }

    [Fact]
    public async Task A_psa_ticket_s_details_are_the_provider_s_to_change()
    {
        var k = await KitAsync();
        var psa = new Ticket
        {
            MspOrganizationId = Org, Title = "From Autotask", RequesterName = "r", RequesterEmail = "r@x.test",
            PortalStatus = "NEW", PortalPriority = "NORMAL", Provider = ProviderType.AutotaskPsa, PsaConnectionId = Guid.NewGuid(),
        };
        k.H.Db.Tickets.Add(psa);
        await k.H.Db.SaveChangesAsync();

        await k.Tickets.Invoking(s => s.EditAsync(psa, new InternalTicketEdit("Renamed", null, "HIGH")))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*PSA*");
    }

    [Fact]
    public async Task A_topic_from_another_board_is_refused()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        var elsewhere = new BoardTopic { MspOrganizationId = Org, BoardId = Guid.NewGuid(), Name = "Payroll" };
        k.H.Db.BoardTopics.Add(elsewhere);
        await k.H.Db.SaveChangesAsync();

        await k.Tickets.Invoking(s => s.EditAsync(t, new InternalTicketEdit(t.Title, null, "NORMAL", BoardTopicId: elsewhere.Id)))
            .Should().ThrowAsync<ValidationFailedException>();
    }

    // ── Resolving and reopening ───────────────────────────────────────────────

    [Fact]
    public async Task A_board_that_asks_for_a_resolution_gets_one_and_keeps_it()
    {
        var k = await KitAsync(requireResolution: true);
        var t = await RaiseAsync(k);

        await k.Status.Invoking(s => s.SetAsync(t, "RESOLVED", null, default)).Should().ThrowAsync<ValidationFailedException>()
            .WithMessage("*resolution*");

        await k.Status.SetAsync(t, "RESOLVED", "Replaced the HDMI cable behind the panel.", default);
        t.Resolution.Should().Be("Replaced the HDMI cable behind the panel.");
        t.ResolvedAt.Should().NotBeNull();

        // Closing after resolving does not ask twice.
        await k.Status.SetAsync(t, "CLOSED", null, default);
        t.ClosedAt.Should().NotBeNull();
        t.ReopenCount.Should().Be(0, "resolved to closed is still finished");
    }

    [Fact]
    public async Task Bringing_a_finished_ticket_back_is_a_counted_reopen_and_it_counts_as_open_again()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        await k.Status.SetAsync(t, "RESOLVED", "Rebooted the display", default);

        await k.Status.SetAsync(t, "IN_PROGRESS", null, default);

        // The old dates kept a reopened ticket out of every open-work list and made its eventual
        // resolution time look instant.
        t.Should().BeEquivalentTo(new { ResolvedAt = (DateTimeOffset?)null, ClosedAt = (DateTimeOffset?)null, ReopenCount = 1 });
        t.LastReopenedAt.Should().NotBeNull();
        t.Resolution.Should().Be("Rebooted the display", "the next person should see what was tried last time");
        (await AuditsAsync(k, t)).Should().Contain(["ticket.status.changed", "ticket.reopened"]);
    }

    // ── Time ──────────────────────────────────────────────────────────────────

    private static TicketTimeController TimeAs(Kit k, AppUser who, bool lead = false) => new(
        k.H.Db, null!, null!, new NoopTicketScopeQuery(),
        new TestCurrentUser(Org, userId: who.Id, name: who.DisplayName,
            permissions: lead
                ? new HashSet<string> { Permissions.TicketsLogTime, Permissions.BoardsManage }
                : new HashSet<string> { Permissions.TicketsLogTime }),
        k.Audit);

    private static TicketTimeController.LogTimeRequest Hours(decimal h, DateTimeOffset? at = null)
        => new(h, "Billable", "Worked on it", null, null, null, at);

    [Fact]
    public async Task Someone_else_s_time_is_theirs_to_change_unless_you_lead_the_boards()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        await TimeAs(k, k.Anika).LogTime(t.Id, Hours(1.5m), default);
        var entry = await k.H.Db.TicketTimeEntries.SingleAsync();

        // Any technician who could see the ticket could rewrite anyone's hours.
        await TimeAs(k, k.Arjun).Invoking(c => c.Update(t.Id, entry.Id.ToString(), new(8m, null, null), default))
            .Should().ThrowAsync<ForbiddenException>();
        await TimeAs(k, k.Arjun).Invoking(c => c.Delete(t.Id, entry.Id.ToString(), default))
            .Should().ThrowAsync<ForbiddenException>();

        await TimeAs(k, k.Anika).Update(t.Id, entry.Id.ToString(), new(2m, null, null), default);
        await TimeAs(k, k.Arjun, lead: true).Update(t.Id, entry.Id.ToString(), new(2.5m, null, null), default);
        (await k.H.Db.TicketTimeEntries.SingleAsync()).Hours.Should().Be(2.5m);
        (await AuditsAsync(k, t)).Should().Contain(["ticket.time.logged", "ticket.time.edited"]);
    }

    [Fact]
    public async Task Board_time_can_be_dated_back_within_a_month_and_never_forward()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        var now = DateTimeOffset.UtcNow;

        await TimeAs(k, k.Anika).LogTime(t.Id, Hours(1m, now.AddDays(-5)), default);
        (await k.H.Db.TicketTimeEntries.SingleAsync()).EntryDate.Should().BeCloseTo(now.AddDays(-5), TimeSpan.FromMinutes(1));

        await TimeAs(k, k.Anika).Invoking(c => c.LogTime(t.Id, Hours(1m, now.AddDays(-31)), default))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*30 days*");
        await TimeAs(k, k.Anika).Invoking(c => c.LogTime(t.Id, Hours(1m, now.AddDays(1)), default))
            .Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task The_time_list_says_which_entries_this_person_may_change()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        await TimeAs(k, k.Anika).LogTime(t.Id, Hours(1m), default);
        await TimeAs(k, k.Arjun).LogTime(t.Id, Hours(2m), default);

        var rows = ((Microsoft.AspNetCore.Mvc.OkObjectResult)await TimeAs(k, k.Anika).List(t.Id, default)).Value
            as IEnumerable<TicketTimeController.TimeRow>;

        rows!.Select(r => (r.Hours, r.MayChange)).Should().BeEquivalentTo([(1m, true), (2m, false)]);
    }

    // ── History ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_history_tells_the_ticket_s_story_newest_first()
    {
        var k = await KitAsync();
        var t = await RaiseAsync(k);
        k.H.Db.TicketAssignments.Add(new TicketAssignment
        {
            MspOrganizationId = Org, TicketId = t.Id, FromAppUserId = k.Anika.Id, ToAppUserId = k.Arjun.Id,
            AssignedByUserId = k.Anika.Id, Note = "Needs a ladder, I'm off at 6",
        });
        await k.H.Db.SaveChangesAsync();
        k.H.Clock.Advance(TimeSpan.FromMinutes(5));
        await k.Tickets.EditAsync(t, new InternalTicketEdit(t.Title, t.Description, "HIGH"));
        k.H.Clock.Advance(TimeSpan.FromMinutes(5));
        await TimeAs(k, k.Arjun).LogTime(t.Id, Hours(0.75m), default);
        k.H.Clock.Advance(TimeSpan.FromMinutes(5));
        await k.Status.SetAsync(t, "RESOLVED", "New cable", default);

        var history = await new TicketHistoryService(k.H.Db).ForAsync(t);

        history.Select(e => e.Summary).Should().ContainInOrder(
            "Status New → Resolved, with a resolution",
            "Logged 0.75h",
            "Changed priority (Normal → High)");
        history.Should().ContainSingle(e => e.Kind == "assigned")
            .Which.Should().BeEquivalentTo(new { Summary = "Passed from Anika to Arjun", Who = "Anika", Note = "Needs a ladder, I'm off at 6" });
        history.Should().ContainSingle(e => e.Kind == "created").Which.Summary.Should().Be("Raised on Office IT");
    }

    [Fact]
    public async Task A_ticket_raised_before_history_was_recorded_still_shows_where_it_began()
    {
        var k = await KitAsync();
        var old = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = k.Board.Id, Title = "Old job",
            RequesterName = "Anika", RequesterEmail = "a@x.test", PortalStatus = "NEW", PortalPriority = "NORMAL",
            CreatedByUserId = k.Anika.Id,
        };
        k.H.Db.Tickets.Add(old);
        await k.H.Db.SaveChangesAsync();

        var history = await new TicketHistoryService(k.H.Db).ForAsync(old);

        history.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = "created", Summary = "Raised", Who = "Anika" });
    }
}
