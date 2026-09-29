using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Approvals: a technician asks the client's named approver; the ticket waits (SLA paused) until the
/// approver answers in the portal or the technician records an answer given by phone; either way the
/// ticket comes back to the team, and every step leaves a note in the thread.
/// </summary>
public class ApprovalTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();

    /// <summary>Records the trail notes instead of posting them anywhere.</summary>
    private sealed class TrailNotes : ITicketCommandService
    {
        public List<(Guid TicketId, string Author, bool ByClient, string Body)> Posted { get; } = [];

        public Task PostTrailNoteAsync(Guid ticketId, string authorName, bool authoredByClient, string body, CancellationToken ct = default)
        {
            Posted.Add((ticketId, authorName, authoredByClient, body));
            return Task.CompletedTask;
        }

        public Task<CreateTicketResultDto> CreateAsync(ClientAccess access, CreateTicketInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddCommentAsync(ClientAccess access, Guid ticketId, string body, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic, bool emailContact, IReadOnlyList<string> emailCc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReplyRecipientsDto> ListReplyRecipientsAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RefreshContactAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record Kit(AdminHarness H, ApprovalService Svc, TrailNotes Notes, Guid Tech, Guid BoardId, Guid Rahul, Guid Priya, Guid NoLogin);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        var tech = new AppUser { MspOrganizationId = Org, DisplayName = "Anika Sharma", Email = "anika@techpio.test", IsActive = true };
        var board = new Board { MspOrganizationId = Org, Name = "Acme monitoring", Key = "ACP", Kind = BoardKind.Rmm, ClientVisible = true };
        h.Db.AppUsers.Add(tech);
        h.Db.Boards.Add(board);
        h.Db.ClientCompanies.AddRange(
            new ClientCompany { Id = Acme, MspOrganizationId = Org, Name = "Acme", ExternalCompanyId = "1", PsaConnectionId = Guid.NewGuid() },
            new ClientCompany { Id = Globex, MspOrganizationId = Org, Name = "Globex", ExternalCompanyId = "2", PsaConnectionId = Guid.NewGuid() });

        // Rahul approves purchases and has a portal login; Priya raised the ticket; Meera is on the
        // approver list but has never signed in.
        var rahul = new ClientUser { MspOrganizationId = Org, ClientCompanyId = Acme, Email = "Rahul@Acme.test", DisplayName = "Rahul Verma" };
        var priya = new ClientUser { MspOrganizationId = Org, ClientCompanyId = Acme, Email = "priya@acme.test", DisplayName = "Priya" };
        h.Db.ClientUsers.AddRange(rahul, priya);
        var rahulApprover = new Approver { MspOrganizationId = Org, ClientCompanyId = Acme, Name = "Rahul Verma", Email = "rahul@acme.test", Scope = "Purchases" };
        var meera = new Approver { MspOrganizationId = Org, ClientCompanyId = Acme, Name = "Meera", Email = "meera@acme.test", SortOrder = 1 };
        h.Db.Approvers.AddRange(rahulApprover, meera);
        await h.Db.SaveChangesAsync();

        var notes = new TrailNotes();
        var svc = new ApprovalService(h.Db, new NoopTicketScopeQuery(), new TicketStatusWriter(h.Db, null!, null!), notes,
            new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock, NullLogger<ApprovalService>.Instance);
        return new Kit(h, svc, notes, tech.Id, board.Id, rahul.Id, priya.Id, meera.Id);
    }

    private static ClientAccess As(Guid clientUser, Guid? company = null, bool admin = false)
        => new(Org, company ?? Acme, clientUser, admin);

    private static async Task<Ticket> TicketAsync(Kit k, string status = "NEW", Guid? company = null, Guid? board = null, Guid? requester = null)
    {
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, BoardId = board ?? k.BoardId, Number = "ACP-000001",
            ClientCompanyId = company ?? Acme, RequesterName = "Priya", RequesterEmail = "priya@acme.test",
            RequesterUserId = requester ?? k.Priya, Title = "Acrobat for Priya", PortalStatus = status,
        };
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();
        return t;
    }

    private static async Task<Guid> RahulApproverAsync(Kit k)
        => await k.H.Db.Approvers.Where(a => a.Name == "Rahul Verma").Select(a => a.Id).SingleAsync();

    [Fact]
    public async Task Asking_puts_the_ticket_on_hold_for_the_client_and_leaves_a_note()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k, "IN_PROGRESS");

        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id,
            new ApprovalRequestInput(await RahulApproverAsync(k), "  Adobe Acrobat licence, ₹18,000  "));

        asked.Should().BeEquivalentTo(new { ApproverName = "Rahul Verma", Request = "Adobe Acrobat licence, ₹18,000", State = "Pending" });
        var ticket = await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id);
        ticket.PortalStatus.Should().Be("WAITING_CUSTOMER");
        k.Notes.Posted.Should().ContainSingle().Which.Body.Should().Be("Approval requested from Rahul Verma: Adobe Acrobat licence, ₹18,000");
    }

    [Fact]
    public async Task The_approver_answers_in_the_portal_and_the_ticket_comes_back_to_the_team()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);
        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(await RahulApproverAsync(k), "Acrobat licence"));

        // Rahul did not raise the ticket and is no company administrator - he is asked by email.
        var mine = await k.Svc.MineAsync(As(k.Rahul));
        mine.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = asked.Id, Reference = "ACP-000001", Request = "Acrobat licence" });

        var answer = await k.Svc.DecideAsync(As(k.Rahul), asked.Id, approved: true, comment: "  Go ahead  ");

        answer.Should().BeEquivalentTo(new { State = "Approved", Channel = "Portal", DecisionComment = "Go ahead" });
        (await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id)).PortalStatus.Should().Be("IN_PROGRESS");
        k.Notes.Posted.Last().Should().Be((t.Id, "Rahul Verma", true, "Approved: Acrobat licence “Go ahead”"));
        (await k.Svc.MineAsync(As(k.Rahul))).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_person_asked_can_answer_and_nobody_from_another_company_can_see_it()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);
        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(await RahulApproverAsync(k), "Acrobat"));

        // Priya raised the ticket and can see the question, but it is Rahul's to answer.
        var seen = await k.Svc.ClientViewAsync(As(k.Priya), t.Id);
        seen.Should().ContainSingle().Which.CanAnswer.Should().BeFalse();
        (await k.Svc.MineAsync(As(k.Priya))).Should().BeEmpty();
        var refused = await Assert.ThrowsAsync<ForbiddenException>(() => k.Svc.DecideAsync(As(k.Priya), asked.Id, true, null));
        refused.Message.Should().Contain("waiting for Rahul Verma");

        // Another company is not told the request exists at all.
        await Assert.ThrowsAsync<NotFoundException>(() => k.Svc.DecideAsync(As(k.Rahul, company: Globex), asked.Id, true, null));
        (await k.Svc.MineAsync(As(k.Rahul, company: Globex))).Should().BeEmpty();

        (await k.H.Db.TicketApprovals.SingleAsync()).State.Should().Be(ApprovalState.Pending);
    }

    [Fact]
    public async Task A_technician_records_an_answer_given_by_phone_and_the_record_says_so()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);
        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(await RahulApproverAsync(k), "Weekend reboot"));

        // "Portal" is the approver's own click; a technician cannot claim it.
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            k.Svc.RecordAsync(k.Tech, "Anika Sharma", asked.Id, new ApprovalRecordInput(true, "Portal", null)));

        var recorded = await k.Svc.RecordAsync(k.Tech, "Anika Sharma", asked.Id, new ApprovalRecordInput(false, "phone", "Not this weekend"));

        recorded.Should().BeEquivalentTo(new { State = "Rejected", Channel = "Phone", RecordedByName = "Anika Sharma" });
        k.Notes.Posted.Last().Body.Should().Be("Rejected by Rahul Verma by phone, recorded by Anika Sharma. “Not this weekend”");
        (await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id)).PortalStatus.Should().Be("IN_PROGRESS");

        // Settled is settled: neither the approver nor the technician can answer it again.
        await Assert.ThrowsAsync<ValidationFailedException>(() => k.Svc.DecideAsync(As(k.Rahul), asked.Id, true, null));
    }

    [Fact]
    public async Task An_answer_does_not_undo_a_later_decision_about_the_ticket()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);
        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(await RahulApproverAsync(k), "Acrobat"));

        // Meanwhile the technician put it on hold for a supplier.
        var ticket = await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id);
        ticket.PortalStatus = "ON_HOLD";
        await k.H.Db.SaveChangesAsync();

        await k.Svc.DecideAsync(As(k.Rahul), asked.Id, true, null);

        (await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id)).PortalStatus.Should().Be("ON_HOLD");
    }

    [Fact]
    public async Task Withdrawing_a_request_hands_the_ticket_back_and_frees_it_for_a_new_one()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);
        var approver = await RahulApproverAsync(k);
        var asked = await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(approver, "Acrobat"));

        // One open question at a time.
        var twice = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(approver, "Acrobat again")));
        twice.Message.Should().Contain("already waiting");

        var withdrawn = await k.Svc.CancelAsync(k.Tech, "Anika Sharma", asked.Id);

        withdrawn.State.Should().Be("Cancelled");
        (await k.H.Db.Tickets.SingleAsync(x => x.Id == t.Id)).PortalStatus.Should().Be("IN_PROGRESS");
        (await k.Svc.StaffViewAsync(k.Tech, t.Id)).CanAsk.Should().BeTrue();
    }

    [Fact]
    public async Task Nobody_is_asked_on_a_ticket_the_client_cannot_see_or_about_another_company()
    {
        var k = await BuildAsync();
        var hidden = new Board { MspOrganizationId = Org, Name = "Internal IT", Key = "INT" };
        k.H.Db.Boards.Add(hidden);
        await k.H.Db.SaveChangesAsync();
        var internalOne = await TicketAsync(k, board: hidden.Id);
        var finished = await TicketAsync(k, status: "RESOLVED");
        var globexTicket = await TicketAsync(k, company: Globex);
        var approver = await RahulApproverAsync(k);

        (await k.Svc.StaffViewAsync(k.Tech, internalOne.Id)).Reason.Should().Contain("cannot see tickets on this board");
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            k.Svc.RequestAsync(k.Tech, "Anika Sharma", internalOne.Id, new ApprovalRequestInput(approver, "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            k.Svc.RequestAsync(k.Tech, "Anika Sharma", finished.Id, new ApprovalRequestInput(approver, "x")));

        // Acme's approver on Globex's ticket: not found, as if it did not exist.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            k.Svc.RequestAsync(k.Tech, "Anika Sharma", globexTicket.Id, new ApprovalRequestInput(approver, "x")));
        (await k.H.Db.TicketApprovals.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_technician_sees_who_can_answer_in_the_portal()
    {
        var k = await BuildAsync();
        var t = await TicketAsync(k);

        var view = await k.Svc.StaffViewAsync(k.Tech, t.Id);

        view.CanAsk.Should().BeTrue();
        // Matched case-insensitively: Rahul signs in as Rahul@Acme.test, the list says rahul@acme.test.
        view.Approvers.Should().BeEquivalentTo(new[]
        {
            new { Name = "Rahul Verma", CanAnswerInPortal = true },
            new { Name = "Meera", CanAnswerInPortal = false },
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task An_approval_left_unanswered_for_two_days_needs_attention()
    {
        var k = await BuildAsync();
        k.H.Db.MspOrganizations.Add(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio-" + Guid.NewGuid().ToString("N")[..6] });
        await k.H.Db.SaveChangesAsync();
        var t = await TicketAsync(k);
        await k.Svc.RequestAsync(k.Tech, "Anika Sharma", t.Id, new ApprovalRequestInput(await RahulApproverAsync(k), "Acrobat"));
        var attention = new AttentionService(k.H.Db, k.H.Tenant, new NoResync(), new NoMail(),
            new AuditWriter(k.H.Db, k.H.User, k.H.Tenant, k.H.Clock), k.H.Clock, NullLogger<AttentionService>.Instance);

        k.H.Clock.Advance(TimeSpan.FromDays(1));
        (await attention.ListAsync()).Items.Should().NotContain(i => i.Kind == "approval-waiting");

        k.H.Clock.Advance(TimeSpan.FromDays(2));
        var item = (await attention.ListAsync()).Items.Single(i => i.Kind == "approval-waiting");
        item.Title.Should().Be("1 approval waiting more than 2 days");
        item.Detail.Should().Contain("ACP-000001 has waited 3 days for Rahul Verma at Acme");
    }

    private sealed class NoResync : Desk.Application.Admin.ITicketResyncService
    {
        public Task<Desk.Application.Admin.UnsyncedTicketsDto> ListAsync(Guid? connectionId = null, CancellationToken ct = default)
            => Task.FromResult(new Desk.Application.Admin.UnsyncedTicketsDto(0, []));
        public Task<Desk.Application.Admin.ResyncResultDto> ResyncAsync(Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoMail : Desk.Application.Common.IEmailSender
    {
        public Task<Desk.Application.Common.EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(Desk.Application.Common.EmailSenderStatus.None);
        public Task SendAsync(Guid organizationId, Desk.Application.Common.EmailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
