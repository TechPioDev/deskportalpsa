using System.Text.Json;
using Desk.Api.Controllers;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Alerts arriving from a monitoring tool: what opens a ticket, what must not open a second one,
/// what closes it again, and what a wrong key is told.
/// </summary>
public class AlertIntakeTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static (AlertSourceService Sources, AlertIntakeService Intake, BoardService Boards, AdminHarness H) Build()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return (new AlertSourceService(h.Db, h.Tenant, h.User, audit),
                new AlertIntakeService(h.Db, h.Tenant, h.Clock, new RecordingActivity(), NullLogger<AlertIntakeService>.Instance),
                new BoardService(h.Db, h.Tenant, audit),
                h);
    }

    private static async Task<(string Key, Guid BoardId)> SourceOnMonitoringBoardAsync(
        BoardService boards, AlertSourceService sources, bool closeOnClear = true)
    {
        var board = await boards.CreateAsync(new BoardInput("Monitoring", "RMM", null, BoardKind.Rmm));
        var created = await sources.CreateAsync(new AlertSourceInput("NinjaOne", board.Id, AlertVendor.NinjaOne, closeOnClear));
        return (created.Key, board.Id);
    }

    [Fact]
    public async Task An_alert_opens_a_numbered_ticket_on_the_monitoring_board()
    {
        var (sources, intake, boards, h) = Build();
        var (key, boardId) = await SourceOnMonitoringBoardAsync(boards, sources);

        var result = await intake.ReceiveAsync(key, new AlertMessage(
            "alert-9001", "Disk C: is 95% full", "Free space 4 GB of 100 GB",
            Severity: "critical", Device: "ACME-SRV01"));

        result.Should().BeEquivalentTo(new { Outcome = "opened", Number = "RMM-000001" });
        var ticket = await h.Db.Tickets.SingleAsync();
        ticket.Should().BeEquivalentTo(new
        {
            Origin = TicketOrigin.Rmm,
            BoardId = (Guid?)boardId,
            SourceAlertId = "alert-9001",
            PortalPriority = "HIGH",
            PortalStatus = "NEW",
            PsaConnectionId = (Guid?)null,
            RequesterName = "NinjaOne",
        });
        ticket.Description.Should().Contain("ACME-SRV01");
    }

    [Fact]
    public async Task The_same_alert_arriving_again_updates_one_ticket_rather_than_opening_another()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources);
        var alert = new AlertMessage("alert-9001", "Disk C: is 95% full");

        await intake.ReceiveAsync(key, alert);
        var second = await intake.ReceiveAsync(key, alert);

        second.Outcome.Should().Be("updated");
        (await h.Db.Tickets.CountAsync()).Should().Be(1);
        // The first repeat says so once; a tool repeating every five minutes must not bury its ticket.
        (await h.Db.TicketNotes.CountAsync()).Should().Be(1);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await intake.ReceiveAsync(key, alert);
        (await h.Db.TicketNotes.CountAsync()).Should().Be(1, "a reminder five minutes later is the same fault");
    }

    [Fact]
    public async Task A_cleared_alert_closes_its_ticket_and_says_so()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources);
        await intake.ReceiveAsync(key, new AlertMessage("alert-9001", "Disk C: is 95% full"));

        h.Clock.Advance(TimeSpan.FromHours(2));
        var cleared = await intake.ReceiveAsync(key, new AlertMessage("alert-9001", "Disk C: is 95% full", Cleared: true));

        cleared.Outcome.Should().Be("closed");
        var ticket = await h.Db.Tickets.SingleAsync();
        ticket.PortalStatus.Should().Be("CLOSED");
        // Closed WITH a date: every resolution-time figure reads this, and the attention list reports
        // a closed ticket that has none.
        ticket.ClosedAt.Should().NotBeNull();
        ticket.ResolvedAt.Should().NotBeNull();
        (await h.Db.TicketNotes.CountAsync(n => n.Body.Contains("cleared"))).Should().Be(1);
    }

    [Fact]
    public async Task A_source_told_not_to_close_leaves_the_ticket_open()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources, closeOnClear: false);
        await intake.ReceiveAsync(key, new AlertMessage("a1", "Service stopped"));

        var cleared = await intake.ReceiveAsync(key, new AlertMessage("a1", "Service stopped", Cleared: true));

        cleared.Outcome.Should().Be("updated");
        (await h.Db.Tickets.SingleAsync()).ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_same_condition_returning_after_it_closed_reopens_the_ticket()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources);
        await intake.ReceiveAsync(key, new AlertMessage("a1", "Service stopped"));
        await intake.ReceiveAsync(key, new AlertMessage("a1", "Service stopped", Cleared: true));

        h.Clock.Advance(TimeSpan.FromHours(3));
        var again = await intake.ReceiveAsync(key, new AlertMessage("a1", "Service stopped"));

        again.Outcome.Should().Be("opened");
        var ticket = await h.Db.Tickets.SingleAsync();
        ticket.ClosedAt.Should().BeNull();
        ticket.PortalStatus.Should().Be("NEW");
        (await h.Db.Tickets.CountAsync()).Should().Be(1, "it is the same fault, not a new one");
    }

    [Fact]
    public async Task An_alert_names_its_client_when_the_portal_knows_that_name()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources);
        h.Db.ClientCompanies.Add(new ClientCompany
        {
            MspOrganizationId = Org, Name = "Acme Dental", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "1001",
        });
        await h.Db.SaveChangesAsync();

        await intake.ReceiveAsync(key, new AlertMessage("a1", "Disk full", Client: "acme  dental"));
        var matched = await h.Db.Tickets.SingleAsync();
        matched.ClientCompanyId.Should().NotBeNull("a name that differs only in spacing is the same customer");

        var unknown = await intake.ReceiveAsync(key, new AlertMessage("a2", "Disk full", Client: "Someone Else Ltd"));
        unknown.Detail.Should().Contain("Someone Else Ltd");
        (await h.Db.Tickets.SingleAsync(t => t.SourceAlertId == "a2")).ClientCompanyId.Should().BeNull();
    }

    [Fact]
    public async Task A_wrong_key_and_a_switched_off_source_are_told_the_same_thing()
    {
        var (sources, intake, boards, h) = Build();
        var (key, _) = await SourceOnMonitoringBoardAsync(boards, sources);

        var wrong = () => intake.ReceiveAsync("dsk_not-a-real-key", new AlertMessage("a1", "Anything"));
        await wrong.Should().ThrowAsync<NotFoundException>();

        var source = await h.Db.AlertSources.SingleAsync();
        await sources.SetActiveAsync(source.Id, false);
        var off = () => intake.ReceiveAsync(key, new AlertMessage("a1", "Anything"));
        await off.Should().ThrowAsync<NotFoundException>();
        (await h.Db.Tickets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_key_is_shown_once_and_never_stored_in_a_readable_form()
    {
        var (sources, _, boards, h) = Build();
        var board = await boards.CreateAsync(new BoardInput("Monitoring", "RMM", null, BoardKind.Rmm));

        var created = await sources.CreateAsync(new AlertSourceInput("Datto", board.Id, AlertVendor.DattoRmm));

        created.Key.Should().StartWith("dsk_");
        var row = await h.Db.AlertSources.SingleAsync();
        row.KeyHash.Should().NotBe(created.Key);
        row.KeyHint.Should().Be(created.Key[..12]);
        (await sources.ListAsync()).Single().Should().BeEquivalentTo(new { KeyHint = created.Key[..12] });

        // Replacing the key stops the old one working.
        var replaced = await sources.RegenerateKeyAsync(row.Id);
        replaced.Key.Should().NotBe(created.Key);
    }

    [Fact]
    public async Task Alerts_belong_on_a_monitoring_board_not_on_the_team_s_own()
    {
        var (sources, _, boards, _) = Build();
        var ours = await boards.CreateAsync(new BoardInput("Internal IT", "INT", null));

        var act = () => sources.CreateAsync(new AlertSourceInput("NinjaOne", ours.Id, AlertVendor.NinjaOne));

        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("monitoring board");
    }

    [Theory]
    [InlineData("critical", "HIGH")]
    [InlineData("Warning", "NORMAL")]
    [InlineData("info", "LOW")]
    [InlineData("emergency", "URGENT")]
    [InlineData("whatever the tool calls it", "NORMAL")]
    public void Severity_becomes_a_priority_the_desk_already_uses(string severity, string expected)
        => AlertIntakeService.Priority(severity).Should().Be(expected);

    [Fact]
    public void A_payload_written_from_a_vendor_template_is_understood()
    {
        // Field names as the vendors' own examples spell them, not as this portal would.
        var body = JsonDocument.Parse("""
        {
          "alertUid": "9f1c-22",
          "subject": "CPU above 90% for 15 minutes",
          "details": "Sustained load on the SQL host",
          "priority": "critical",
          "deviceHostname": "ACME-SQL01",
          "siteName": "Acme Dental",
          "status": "cleared",
          "timestamp": "2026-09-28T09:15:00Z"
        }
        """).RootElement;

        var message = AlertIntakeController.Read(body);

        message.Should().BeEquivalentTo(new AlertMessage(
            "9f1c-22", "CPU above 90% for 15 minutes", "Sustained load on the SQL host",
            "critical", "ACME-SQL01", "Acme Dental", Cleared: true,
            OccurredAt: DateTimeOffset.Parse("2026-09-28T09:15:00Z")));
    }
}
