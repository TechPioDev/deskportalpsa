using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The opt-in review of resolved work - asked for by a board or a topic, done by someone other than
/// whoever did the work - and links between tickets, which never reveal a ticket the reader cannot see.
/// </summary>
public class TicketReviewAndLinkTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed record Kit(AdminHarness H, TicketStatusWriter Status, TicketReviewService Review, Board Board, AppUser Tech, AppUser Lead);

    private static async Task<Kit> KitAsync(bool boardReviews = true)
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        var tech = new AppUser { MspOrganizationId = Org, DisplayName = "Arjun", Email = "arjun@techpio.test", IsActive = true };
        var lead = new AppUser { MspOrganizationId = Org, DisplayName = "Dalbir", Email = "dalbir@techpio.test", IsActive = true };
        var board = new Board { MspOrganizationId = Org, Name = "Security", Key = "SEC", Kind = BoardKind.Internal, RequireReview = boardReviews };
        h.Db.AddRange(tech, lead, board);
        await h.Db.SaveChangesAsync();
        var status = new TicketStatusWriter(h.Db, null!, null!, audit);
        return new Kit(h, status, new TicketReviewService(h.Db, status, audit, h.Clock), board, tech, lead);
    }

    private static async Task<Ticket> TicketAsync(Kit k, string title = "Firewall rule review", Guid? topicId = null)
    {
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = k.Board.Id, BoardTopicId = topicId, Number = $"SEC-{Guid.NewGuid():N}"[..10],
            Title = title, RequesterName = "Arjun", RequesterEmail = "a@x.test", PortalStatus = "IN_PROGRESS", PortalPriority = "NORMAL",
            AssignedAppUserId = k.Tech.Id,
        };
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Resolved_work_on_a_reviewing_board_waits_for_a_lead_before_it_closes()
    {
        var k = await KitAsync();
        var t = await TicketAsync(k);

        await k.Status.SetAsync(t, "RESOLVED", null, default);
        t.ReviewState.Should().Be(TicketReviewState.Pending);

        await k.Status.Invoking(s => s.SetAsync(t, "CLOSED", null, default)).Should().ThrowAsync<ValidationFailedException>()
            .WithMessage("*reviewed before it closes*");

        await k.Review.ReviewAsync(t, k.Lead.Id, "Dalbir", approve: true, note: null);
        t.Should().BeEquivalentTo(new { PortalStatus = "CLOSED", ReviewState = TicketReviewState.Approved, ReviewedByUserId = (Guid?)k.Lead.Id, ReviewSendBacks = 0 });
        (await k.H.Db.AuditLog.AnyAsync(a => a.Action == "ticket.review.approved" && a.EntityId == t.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task Nobody_reviews_their_own_work()
    {
        var k = await KitAsync();
        var t = await TicketAsync(k);
        await k.Status.SetAsync(t, "RESOLVED", null, default);

        await k.Review.Invoking(r => r.ReviewAsync(t, k.Tech.Id, "Arjun", approve: true, note: null))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Sent_back_work_returns_to_work_with_the_note_and_is_not_counted_as_a_reopen()
    {
        var k = await KitAsync();
        var t = await TicketAsync(k);
        await k.Status.SetAsync(t, "RESOLVED", null, default);

        await k.Review.Invoking(r => r.ReviewAsync(t, k.Lead.Id, "Dalbir", approve: false, note: null))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*what needs doing*");
        await k.Review.ReviewAsync(t, k.Lead.Id, "Dalbir", approve: false, note: "Rule 14 still allows any source.");

        // A review finding is not a ticket that came back after it was finished.
        t.Should().BeEquivalentTo(new { PortalStatus = "IN_PROGRESS", ReviewState = TicketReviewState.None, ReviewSendBacks = 1, ReopenCount = 0 });
        var note = await k.H.Db.TicketNotes.SingleAsync(n => n.TicketId == t.Id);
        note.Should().BeEquivalentTo(new { IsPublic = false, Body = "Sent back in review: Rule 14 still allows any source." });

        // Resolved again, it waits for review again.
        await k.Status.SetAsync(t, "RESOLVED", null, default);
        t.ReviewState.Should().Be(TicketReviewState.Pending);
    }

    [Fact]
    public async Task A_topic_can_ask_for_review_on_a_board_that_does_not()
    {
        var k = await KitAsync(boardReviews: false);
        var topic = new BoardTopic { MspOrganizationId = Org, BoardId = k.Board.Id, Name = "Firewall change", RequireReview = true };
        k.H.Db.BoardTopics.Add(topic);
        await k.H.Db.SaveChangesAsync();
        var reviewed = await TicketAsync(k, "Open port 443", topic.Id);
        var ordinary = await TicketAsync(k, "Rename a share");

        await k.Status.SetAsync(reviewed, "RESOLVED", null, default);
        await k.Status.SetAsync(ordinary, "CLOSED", null, default);

        reviewed.ReviewState.Should().Be(TicketReviewState.Pending);
        ordinary.PortalStatus.Should().Be("CLOSED", "a board and topic that do not review close as they always did");
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    /// <summary>A reader who cannot see tickets titled HIDDEN.</summary>
    private sealed class HidesSome : ITicketScopeQuery
    {
        public Task<IQueryable<Ticket>> VisibleAsync(IQueryable<Ticket> source, Guid appUserId, string permissionKey, CancellationToken ct = default)
            => Task.FromResult(source.Where(t => !t.Title.StartsWith("HIDDEN")));

        public async Task<Ticket?> FindAsync(IQueryable<Ticket> source, Guid ticketId, Guid appUserId, string permissionKey, CancellationToken ct = default)
            => await (await VisibleAsync(source, appUserId, permissionKey, ct)).FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    [Fact]
    public async Task A_link_reads_from_both_sides_and_is_recorded_on_both_tickets()
    {
        var k = await KitAsync(boardReviews: false);
        var dupe = await TicketAsync(k, "Printer offline again");
        var original = await TicketAsync(k, "Printer offline");
        var links = new TicketLinkService(k.H.Db, new HidesSome(), new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock));

        await links.AddAsync(dupe, k.Tech.Id, original.Id, TicketLinkKind.Duplicate);

        (await links.ListAsync(dupe, k.Tech.Id)).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Relation = "Duplicate of", OtherTitle = "Printer offline" });
        (await links.ListAsync(original, k.Tech.Id)).Should().ContainSingle().Which.Relation.Should().Be("Duplicated by");
        (await k.H.Db.AuditLog.CountAsync(a => a.Action == "ticket.linked")).Should().Be(2);

        // One link per pair, in either direction; never to itself.
        await links.Invoking(l => l.AddAsync(original, k.Tech.Id, dupe.Id, TicketLinkKind.Related)).Should().ThrowAsync<ValidationFailedException>();
        await links.Invoking(l => l.AddAsync(dupe, k.Tech.Id, dupe.Id, TicketLinkKind.Related)).Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task A_link_never_reveals_a_ticket_the_reader_cannot_see()
    {
        var k = await KitAsync(boardReviews: false);
        var mine = await TicketAsync(k, "Mailbox full");
        var hidden = await TicketAsync(k, "HIDDEN salary review");
        var links = new TicketLinkService(k.H.Db, new HidesSome(), new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock));

        // Linking to it is "not found", exactly as opening it would be.
        await links.Invoking(l => l.AddAsync(mine, k.Tech.Id, hidden.Id, TicketLinkKind.Related)).Should().ThrowAsync<NotFoundException>();

        // A link someone else made to it is left out of the list, not named.
        k.H.Db.TicketLinks.Add(new TicketLink { MspOrganizationId = Org, FromTicketId = mine.Id, ToTicketId = hidden.Id, Kind = TicketLinkKind.Related });
        await k.H.Db.SaveChangesAsync();
        (await links.ListAsync(mine, k.Tech.Id)).Should().BeEmpty();
    }
}
