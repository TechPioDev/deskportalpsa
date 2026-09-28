using Desk.Application.Analytics;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The team's own boards: raising work that belongs to no PSA, handing it to a colleague, and the
/// one rule that must never bend — a client cannot see it, even when it names them.
/// </summary>
public class InternalBoardTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static (BoardService Boards, InternalTicketService Tickets, AdminHarness H) Build()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return (new BoardService(h.Db, h.Tenant, audit),
                new InternalTicketService(h.Db, h.Tenant, h.Clock, new RecordingActivity()),
                h);
    }

    private static async Task<Guid> StaffAsync(AdminHarness h, string name = "Dalbir")
    {
        var user = new Desk.Domain.Identity.AppUser
        {
            MspOrganizationId = Org, DisplayName = name, Email = $"{name.ToLowerInvariant()}@techpio.test", IsActive = true,
        };
        h.Db.AppUsers.Add(user);
        await h.Db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task A_board_takes_a_prefix_that_every_ticket_number_carries()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "int", "Our own work"));

        board.Should().BeEquivalentTo(new { Name = "Internal IT", Key = "INT", Kind = BoardKind.Internal, ClientVisible = false });

        var first = await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Rebuild the spare laptop", null));
        var second = await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Document the switch config", null));

        first.Number.Should().Be("INT-000001");
        second.Number.Should().Be("INT-000002");
    }

    [Theory]
    [InlineData("i", "prefix")]
    [InlineData("TOOLONGPREFIX", "prefix")]
    [InlineData("1ST", "prefix")]
    [InlineData("", "prefix")]
    public async Task A_bad_prefix_is_refused_with_the_rule(string key, string mentions)
    {
        var (boards, _, _) = Build();
        var act = () => boards.CreateAsync(new BoardInput("Internal", key, null));
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain(mentions);
    }

    [Fact]
    public async Task An_internal_ticket_belongs_to_no_PSA_and_is_never_pending_a_push()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        var created = await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Patch the test bench", "Before Friday"));

        var row = await h.Db.Tickets.SingleAsync(t => t.Id == created.TicketId);
        row.Should().BeEquivalentTo(new
        {
            Origin = TicketOrigin.Internal,
            PsaConnectionId = (Guid?)null,
            Provider = (ProviderType?)null,
            ExternalTicketId = (string?)null,
            ClientCompanyId = (Guid?)null,
            // Anything else is read as a failed push by every "not synced" reader in the product.
            SyncStatus = TicketSyncStatus.Synced,
            CreatedByUserId = me,
            RequesterName = "Dalbir",
        });
    }

    [Fact]
    public async Task Work_recorded_against_a_client_stays_out_of_that_client_s_portal()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var company = new ClientCompany { MspOrganizationId = Org, Name = "Acme Dental", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "1001" };
        h.Db.ClientCompanies.Add(company);
        await h.Db.SaveChangesAsync();
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Acme migration prep", null, ClientCompanyId: company.Id));

        var reads = new TicketReadService(h.Db, new NoopTicketScopeQuery(), h.User);
        var seen = await reads.ListAsync(new ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true));

        seen.Should().BeEmpty("work the team records against a customer is not the customer's to read");
    }

    [Fact]
    public async Task An_RMM_board_may_be_published_to_the_client_it_names_but_an_internal_board_may_not()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var company = new ClientCompany { MspOrganizationId = Org, Name = "Acme Dental", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "1001" };
        h.Db.ClientCompanies.Add(company);
        await h.Db.SaveChangesAsync();

        // Asking for client visibility on an internal board is ignored, not obeyed.
        var internalBoard = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null, BoardKind.Internal, ClientVisible: true));
        internalBoard.ClientVisible.Should().BeFalse();

        var alerts = await boards.CreateAsync(new BoardInput("Monitoring", "RMM", null, BoardKind.Rmm, ClientVisible: true));
        alerts.ClientVisible.Should().BeTrue();

        await tickets.CreateAsync(me, new InternalTicketInput(internalBoard.Id, "Internal only", null, ClientCompanyId: company.Id));
        var alert = await tickets.CreateAsync(me, new InternalTicketInput(alerts.Id, "Disk almost full", null, ClientCompanyId: company.Id));

        var reads = new TicketReadService(h.Db, new NoopTicketScopeQuery(), h.User);
        var seen = await reads.ListAsync(new ClientAccess(Org, company.Id, Guid.NewGuid(), IsCompanyAdministrator: true));

        seen.Select(t => t.Title).Should().Equal("Disk almost full");
        (await h.Db.Tickets.SingleAsync(t => t.Id == alert.TicketId)).Origin.Should().Be(TicketOrigin.Rmm);
    }

    [Fact]
    public async Task Handing_a_ticket_over_records_who_passed_it_and_who_decided()
    {
        var (boards, tickets, h) = Build();
        var day = await StaffAsync(h, "Day");
        var night = await StaffAsync(h, "Night");
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        var created = await tickets.CreateAsync(day, new InternalTicketInput(
            board.Id, "Finish the firewall rules", null, AssignedAppUserId: night));

        var ticket = await h.Db.Tickets.SingleAsync(t => t.Id == created.TicketId);
        ticket.Should().BeEquivalentTo(new { AssignedAppUserId = (Guid?)night, AssignedByUserId = (Guid?)day });

        var handover = await h.Db.TicketAssignments.SingleAsync(a => a.TicketId == created.TicketId);
        handover.Should().BeEquivalentTo(new { ToAppUserId = (Guid?)night, AssignedByUserId = (Guid?)day });
    }

    [Fact]
    public async Task A_board_with_members_is_theirs_and_an_empty_one_belongs_to_everybody()
    {
        var (boards, _, h) = Build();
        var me = await StaffAsync(h, "Lead");
        var board = await boards.CreateAsync(new BoardInput("Night shift", "NGT", null));

        (await boards.ListAsync()).Single().MemberCount.Should().Be(0);

        var members = await boards.SetMembersAsync(board.Id, [me]);
        members.Select(m => m.DisplayName).Should().Equal("Lead");

        (await boards.SetMembersAsync(board.Id, [])).Should().BeEmpty();
    }

    [Fact]
    public async Task A_prefix_cannot_change_once_people_are_quoting_its_numbers()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Anything", null));

        var act = () => boards.UpdateAsync(board.Id, new BoardInput("Internal IT", "OPS", null));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("already has tickets");
    }

    [Fact]
    public async Task A_closed_board_takes_no_new_work()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Old project", "OLD", null));
        await boards.SetActiveAsync(board.Id, false);

        var act = () => tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Too late", null));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("closed");
    }

    [Fact]
    public async Task A_technician_s_figures_show_client_work_and_internal_work_side_by_side()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        // Two of the team's own tickets, one of them finished.
        var first = await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Rebuild the bench", null, AssignedAppUserId: me));
        await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Write the runbook", null, AssignedAppUserId: me));
        var done = await h.Db.Tickets.SingleAsync(t => t.Id == first.TicketId);
        done.ResolvedAt = h.Clock.GetUtcNow();
        done.TimeWorkedHours = 2.5m;

        // One client ticket from a PSA, also finished, with its own hours.
        h.Db.Tickets.Add(new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = Guid.NewGuid(),
            Provider = ProviderType.AutotaskPsa, ExternalTicketId = "7815", ClientCompanyId = Guid.NewGuid(),
            Title = "Printer offline", RequesterName = "Asha", RequesterEmail = "asha@acme.test",
            AssignedAppUserId = me, ResolvedAt = h.Clock.GetUtcNow(), TimeWorkedHours = 4m,
        });
        await h.Db.SaveChangesAsync();

        var metrics = new TechnicianMetricsService(h.Db, new ProductivityScorer(), h.Clock);
        var mine = await metrics.ForTechnicianAsync(new MetricsFilter { AppUserId = me }, new ProductivityWeights());

        mine.Should().BeEquivalentTo(new
        {
            Assigned = 3, Resolved = 2,
            AssignedClient = 1, AssignedInternal = 2,
            ResolvedClient = 1, ResolvedInternal = 1,
            ClientHours = 4m, InternalHours = 2.5m,
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task A_topic_fills_in_what_usually_follows_from_it()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h, "Raiser");
        var onCall = await StaffAsync(h, "OnCall");
        var dept = new Desk.Domain.Organization.Department { MspOrganizationId = Org, Name = "NOC", IsActive = true };
        h.Db.Departments.Add(dept);
        await h.Db.SaveChangesAsync();
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        var topic = await boards.AddTopicAsync(board.Id, new BoardTopicInput(
            "Patching", DefaultDepartmentId: dept.Id, DefaultPriority: "high",
            DefaultAssigneeUserId: onCall, DueInHours: 8));
        topic.Should().BeEquivalentTo(new { Name = "Patching", DefaultDepartmentName = "NOC", DefaultPriority = "HIGH" });

        var created = await tickets.CreateAsync(me, new InternalTicketInput(
            board.Id, "September patch run", null, BoardTopicId: topic.Id, Source: "Meeting"));

        var ticket = await h.Db.Tickets.SingleAsync(t => t.Id == created.TicketId);
        ticket.Should().BeEquivalentTo(new
        {
            DepartmentId = (Guid?)dept.Id,
            BoardTopicId = (Guid?)topic.Id,
            PortalPriority = "HIGH",
            AssignedAppUserId = (Guid?)onCall,
            Source = "Meeting",
        });
        ticket.SlaDueAt.Should().Be(h.Clock.GetUtcNow().AddHours(8));
    }

    [Fact]
    public async Task What_the_person_typed_beats_what_the_topic_would_have_chosen()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var other = await StaffAsync(h, "Someone else");
        var noc = new Desk.Domain.Organization.Department { MspOrganizationId = Org, Name = "NOC", IsActive = true };
        var helpdesk = new Desk.Domain.Organization.Department { MspOrganizationId = Org, Name = "HelpDesk", IsActive = true };
        h.Db.Departments.AddRange(noc, helpdesk);
        await h.Db.SaveChangesAsync();
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var topic = await boards.AddTopicAsync(board.Id, new BoardTopicInput(
            "Patching", DefaultDepartmentId: noc.Id, DefaultPriority: "HIGH", DueInHours: 8));

        var due = h.Clock.GetUtcNow().AddDays(3);
        var created = await tickets.CreateAsync(me, new InternalTicketInput(
            board.Id, "Special case", null, Priority: "LOW", BoardTopicId: topic.Id,
            DepartmentId: helpdesk.Id, AssignedAppUserId: other, DueAt: due));

        var ticket = await h.Db.Tickets.SingleAsync(t => t.Id == created.TicketId);
        ticket.Should().BeEquivalentTo(new
        {
            DepartmentId = (Guid?)helpdesk.Id,
            PortalPriority = "LOW",
            AssignedAppUserId = (Guid?)other,
        });
        ticket.SlaDueAt.Should().Be(due);
    }

    [Fact]
    public async Task A_source_outside_the_list_is_refused_rather_than_stored_as_typed()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        var act = () => tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Anything", null, Source: "carrier pigeon"));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("Phone");
        // Counting where work comes from is the only reason to record it, and free text cannot be counted.
        (await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "By phone", null, Source: "phone"))).Should().NotBeNull();
        (await h.Db.Tickets.SingleAsync(t => t.Title == "By phone")).Source.Should().Be("Phone");
    }

    [Fact]
    public async Task A_retired_topic_keeps_its_tickets_and_takes_no_new_ones()
    {
        var (boards, tickets, h) = Build();
        var me = await StaffAsync(h);
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        var topic = await boards.AddTopicAsync(board.Id, new BoardTopicInput("Old process"));
        await tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Raised while it existed", null, BoardTopicId: topic.Id));

        await boards.SetTopicActiveAsync(topic.Id, false);

        (await boards.TopicsAsync(board.Id)).Should().BeEmpty();
        (await boards.TopicsAsync(board.Id, includeInactive: true)).Should().ContainSingle();
        (await h.Db.Tickets.SingleAsync()).BoardTopicId.Should().Be(topic.Id, "history keeps the topic it was raised under");

        var act = () => tickets.CreateAsync(me, new InternalTicketInput(board.Id, "Too late", null, BoardTopicId: topic.Id));
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("retired");
    }

    [Fact]
    public async Task Two_topics_on_one_board_cannot_share_a_name()
    {
        var (boards, _, _) = Build();
        var board = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));
        await boards.AddTopicAsync(board.Id, new BoardTopicInput("Patching"));

        var act = () => boards.AddTopicAsync(board.Id, new BoardTopicInput(" patching "));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("already has a topic");
    }
}
