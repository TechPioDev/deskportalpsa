using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Reporting;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The "needs attention" list: each rule fires on the condition that once went unnoticed in
/// production, stays quiet when things are fine, and the digest carries the list to people.
/// </summary>
public class AttentionTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class FakeEmail(bool configured = true) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(configured ? new EmailSenderStatus(true, "alerts@techpio.test", "organization") : EmailSenderStatus.None);
        public Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private sealed class FakeResync(int unsynced = 0) : ITicketResyncService
    {
        public Task<UnsyncedTicketsDto> ListAsync(Guid? connectionId = null, CancellationToken ct = default)
            => Task.FromResult(new UnsyncedTicketsDto(unsynced, []));
        public Task<ResyncResultDto> ResyncAsync(Guid ticketId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<(AttentionService Svc, AdminHarness H, FakeEmail Mail)> BuildAsync(bool mailConfigured = true, int unsynced = 0)
    {
        var h = AdminHarness.Create(Org);
        h.Db.MspOrganizations.Add(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio-" + Guid.NewGuid().ToString("N")[..6] });
        await h.Db.SaveChangesAsync();
        var mail = new FakeEmail(mailConfigured);
        var svc = new AttentionService(h.Db, h.Tenant, new FakeResync(unsynced), mail,
            new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock, NullLogger<AttentionService>.Instance);
        return (svc, h, mail);
    }

    private static PsaConnection Connection(AdminHarness h, ConnectionStatus status = ConnectionStatus.Healthy, TimeSpan? syncedAgo = null, string? error = null)
        => new()
        {
            MspOrganizationId = Org, Name = "TechPio CW", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://cw.test", CredentialSecretRef = "ref", Status = status, TwoWaySync = true,
            LastSuccessfulSyncAt = h.Clock.GetUtcNow() - (syncedAgo ?? TimeSpan.FromMinutes(3)), LastError = error,
        };

    private static Ticket ClosedTicket(Guid connectionId, string psaStatus, DateTimeOffset? closedAt = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = connectionId, ClientCompanyId = Guid.NewGuid(),
        Title = "Printer", RequesterName = "Unknown", RequesterEmail = "unknown@unknown",
        PortalStatus = "CLOSED", PsaStatus = psaStatus, ClosedAt = closedAt,
    };

    [Fact]
    public async Task A_healthy_installation_needs_nothing()
    {
        var (svc, h, _) = await BuildAsync();
        h.Db.PsaConnections.Add(Connection(h));
        await h.Db.SaveChangesAsync();

        (await svc.ListAsync()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_degraded_connection_is_listed_with_its_error()
    {
        var (svc, h, _) = await BuildAsync();
        h.Db.PsaConnections.Add(Connection(h, ConnectionStatus.Degraded, error: "401 Unauthorized"));
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new { Kind = "connection-degraded", Severity = "warning", Link = "/dashboard/connections" });
        item.Detail.Should().Contain("401 Unauthorized");
    }

    // A connection now has states the list did not know: still being set up, paused, put away,
    // locked out by the PSA. Each used to come out as "failed" or "has not synced".

    [Fact]
    public async Task A_connection_that_was_never_switched_on_is_one_reminder_and_not_a_fault()
    {
        var (svc, h, _) = await BuildAsync();
        var c = Connection(h, ConnectionStatus.Failed, error: "Authentication: ConnectWise rejected the credentials.");
        (c.InSetup, c.IsEnabled, c.LastSuccessfulSyncAt) = (true, false, null);
        h.Db.PsaConnections.Add(c);
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle("not 'failed' and 'never synced' as well").Subject;
        (item.Kind, item.Severity).Should().Be(("connection-setup", "warning"));
        item.Detail.Should().Contain("rejected the credentials").And.Contain("Test and switch on");
    }

    [Fact]
    public async Task A_connection_the_PSA_has_locked_out_says_so_once()
    {
        var (svc, h, _) = await BuildAsync();
        var c = Connection(h, ConnectionStatus.Degraded, syncedAgo: TimeSpan.FromHours(5), error: "401 Unauthorized");
        c.LastErrorKind = ConnectionStates.Authentication;
        h.Db.PsaConnections.Add(c);
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle().Subject;
        (item.Kind, item.Severity).Should().Be(("connection-credentials", "critical"));
        item.Detail.Should().Contain("automatic sync has stopped");
    }

    [Fact]
    public async Task A_paused_connection_is_reported_as_paused_and_only_once_it_has_been_a_while()
    {
        var (svc, h, _) = await BuildAsync();
        var c = Connection(h, syncedAgo: TimeSpan.FromMinutes(40));
        c.SyncPausedAt = h.Clock.GetUtcNow() - TimeSpan.FromMinutes(30);
        h.Db.PsaConnections.Add(c);
        await h.Db.SaveChangesAsync();

        (await svc.ListAsync()).Items.Should().BeEmpty("half an hour is a pause, not something forgotten");

        h.Clock.Advance(TimeSpan.FromHours(5));

        var item = (await svc.ListAsync()).Items.Should().ContainSingle("paused, not 'has not synced'").Subject;
        item.Kind.Should().Be("sync-paused");
        item.Title.Should().Contain("paused for 5 hours");
        item.Detail.Should().NotContain("Sync now");
    }

    [Fact]
    public async Task An_archived_connection_is_silent()
    {
        var (svc, h, _) = await BuildAsync();
        var c = Connection(h, ConnectionStatus.Failed, syncedAgo: TimeSpan.FromDays(30), error: "gone");
        (c.ArchivedAt, c.IsEnabled) = (h.Clock.GetUtcNow(), false);
        h.Db.PsaConnections.Add(c);
        await h.Db.SaveChangesAsync();

        (await svc.ListAsync()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_that_stopped_syncing_is_listed_as_stale()
    {
        var (svc, h, _) = await BuildAsync();
        h.Db.PsaConnections.Add(Connection(h, syncedAgo: TimeSpan.FromHours(5)));
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be("sync-stale");
        item.Title.Should().Contain("5 hours");
    }

    [Fact]
    public async Task Closed_tickets_without_a_closed_date_are_counted_by_PSA_status()
    {
        var (svc, h, _) = await BuildAsync();
        var c = Connection(h);
        h.Db.PsaConnections.Add(c);
        h.Db.Tickets.AddRange(
            ClosedTicket(c.Id, "Completed"), ClosedTicket(c.Id, "Completed"),
            ClosedTicket(c.Id, "Closed (resolved)", closedAt: h.Clock.GetUtcNow()));
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new { Kind = "closed-without-date", Count = 2 });
        item.Detail.Should().Contain("\"Completed\" ×2");
    }

    [Fact]
    public async Task Unsynced_tickets_and_dead_jobs_are_critical_and_come_first()
    {
        var (svc, h, _) = await BuildAsync(unsynced: 3);
        h.Db.PsaConnections.Add(Connection(h, ConnectionStatus.Degraded));
        h.Db.BackgroundJobs.Add(new BackgroundJob { MspOrganizationId = Org, JobType = "inbound", PayloadJson = "{}", Status = BackgroundJobStatus.DeadLettered, LastError = "boom" });
        await h.Db.SaveChangesAsync();

        var items = (await svc.ListAsync()).Items;
        items.Select(i => i.Kind).Should().Equal("tickets-unsynced", "jobs-dead-lettered", "connection-degraded");
    }

    [Fact]
    public async Task Scheduled_reports_that_were_not_emailed_are_listed_and_old_ones_age_out()
    {
        var (svc, h, _) = await BuildAsync();
        var schedule = Guid.NewGuid();
        h.Db.StaffReportRuns.AddRange(
            new StaffReportRun { MspOrganizationId = Org, StaffReportScheduleId = schedule, Title = "Daily team report", GeneratedAt = h.Clock.GetUtcNow().AddHours(-2), Delivered = false, DeliveryNote = "No recipients set" },
            new StaffReportRun { MspOrganizationId = Org, StaffReportScheduleId = schedule, Title = "Old", GeneratedAt = h.Clock.GetUtcNow().AddDays(-30), Delivered = false },
            new StaffReportRun { MspOrganizationId = Org, StaffReportScheduleId = null, Title = "Downloaded on demand", GeneratedAt = h.Clock.GetUtcNow(), Delivered = false });
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new { Kind = "staff-reports-undelivered", Count = 1 });
        item.Detail.Should().Contain("Daily team report").And.Contain("No recipients set");
    }

    [Fact]
    public async Task Missing_email_is_only_flagged_once_a_schedule_depends_on_it()
    {
        var (svc, h, _) = await BuildAsync(mailConfigured: false);
        (await svc.ListAsync()).Items.Should().BeEmpty();

        h.Db.StaffReportSchedules.Add(new StaffReportSchedule { MspOrganizationId = Org, Name = "Monthly", IsEnabled = true });
        await h.Db.SaveChangesAsync();

        (await svc.ListAsync()).Items.Should().ContainSingle(i => i.Kind == "email-not-configured");
    }

    [Fact]
    public async Task Digest_recipients_are_saved_cleaned_and_audited()
    {
        var (svc, h, _) = await BuildAsync();

        var (digest, invalid) = await svc.SetDigestRecipientsAsync("ops@techpio.com; nope; ops@techpio.com, lead@techpio.com");

        digest.Recipients.Should().Be("ops@techpio.com, lead@techpio.com");
        invalid.Should().Equal("nope");
        (await h.Db.AuditLog.CountAsync(a => a.Action == "attention.digest.recipients")).Should().Be(1);

        (await svc.SetDigestRecipientsAsync("  ")).Digest.Recipients.Should().BeNull();
    }

    [Fact]
    public async Task Send_now_emails_the_list_to_the_recipients()
    {
        var (svc, h, mail) = await BuildAsync();
        h.Db.PsaConnections.Add(Connection(h, ConnectionStatus.Failed, error: "DNS failure"));
        await h.Db.SaveChangesAsync();
        await svc.SetDigestRecipientsAsync("ops@techpio.com");

        var result = await svc.SendDigestNowAsync();

        result.Sent.Should().BeTrue();
        var sent = mail.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Equal("ops@techpio.com");
        sent.Subject.Should().Contain("1 item needs attention").And.Contain("1 critical");
        sent.TextBody.Should().Contain("DNS failure").And.Contain("/dashboard/connections");
        sent.HtmlBody.Should().Contain("CRITICAL");
    }

    [Fact]
    public async Task Send_now_refuses_without_recipients_or_mail()
    {
        var (svc, _, mail) = await BuildAsync(mailConfigured: false);
        (await svc.SendDigestNowAsync()).Should().BeEquivalentTo(new { Sent = false, Message = "No digest recipients set." });
        await svc.SetDigestRecipientsAsync("ops@techpio.com");
        (await svc.SendDigestNowAsync()).Should().BeEquivalentTo(new { Sent = false, Message = "Email delivery is not configured." });
        mail.Sent.Should().BeEmpty();
    }

    [Fact]
    public void The_digest_escapes_provider_text_in_its_html()
    {
        var message = AttentionService.Compose("TechPio",
            [new AttentionItem("connection-failed", "critical", "CW <b>down</b>", "<script>x</script>", 1, "/dashboard/connections")],
            DateTimeOffset.UnixEpoch, "https://piomanage.com");

        message.HtmlBody.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
        message.HtmlBody.Should().Contain("https://piomanage.com/dashboard/connections");
    }

    [Fact]
    public async Task A_poor_rating_this_week_is_something_to_follow_up()
    {
        var (svc, h, _) = await BuildAsync();
        var ticket = new Desk.Domain.Tickets.Ticket
        {
            MspOrganizationId = Org, Origin = Desk.Domain.Enums.TicketOrigin.Psa, ExternalTicketId = "4410",
            RequesterName = "p", RequesterEmail = "p@t", Title = "VPN", PortalStatus = "RESOLVED",
            SyncStatus = Desk.Domain.Enums.TicketSyncStatus.Synced,
        };
        h.Db.Tickets.Add(ticket);
        h.Db.TicketSatisfactions.AddRange(
            new Desk.Domain.Tickets.TicketSatisfaction { MspOrganizationId = Org, TicketId = ticket.Id, ClientUserId = Guid.NewGuid(),
                Rating = 1, Comment = "Nobody called back", RatedAt = h.Clock.GetUtcNow().AddDays(-1) });
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Single(i => i.Kind == "satisfaction-poor");
        item.Title.Should().Be("1 poor rating from clients this week");
        item.Detail.Should().Contain("1/5 on 4410").And.Contain("Nobody called back");
    }

    private static Desk.Domain.Tickets.Ticket Sla(string reference, string status, DateTimeOffset? due,
        DateTimeOffset? replyDue = null, DateTimeOffset? paused = null) => new()
    {
        MspOrganizationId = Org, Origin = Desk.Domain.Enums.TicketOrigin.Internal, Number = reference,
        RequesterName = "d", RequesterEmail = "d@t", Title = "Ticket " + reference, PortalStatus = status,
        SlaDueAt = due, FirstResponseDueAt = replyDue, SlaPausedAt = paused,
        SyncStatus = Desk.Domain.Enums.TicketSyncStatus.Synced,
    };

    [Fact]
    public async Task Breached_and_about_to_breach_tickets_are_listed_but_waiting_and_finished_ones_are_not()
    {
        var (svc, h, _) = await BuildAsync();
        var now = h.Clock.GetUtcNow();
        h.Db.Tickets.AddRange(
            Sla("INT-1", "IN_PROGRESS", now.AddHours(-30)),          // breached, longest
            Sla("INT-2", "NEW", now.AddHours(-1)),                    // breached
            Sla("INT-3", "NEW", now.AddMinutes(90)),                  // at risk
            Sla("INT-4", "NEW", now.AddHours(5)),                     // due, not yet at risk
            Sla("INT-5", "WAITING_CUSTOMER", now.AddHours(-3)),       // the customer has it
            Sla("INT-6", "ON_HOLD", now.AddHours(-3)),
            Sla("INT-7", "IN_PROGRESS", now.AddHours(-3), paused: now.AddHours(-4)),
            Sla("INT-8", "CLOSED", now.AddHours(-3)));                // finished late is history
        await h.Db.SaveChangesAsync();

        var items = (await svc.ListAsync()).Items;

        var breached = items.Single(i => i.Kind == "sla-breached");
        breached.Should().BeEquivalentTo(new { Severity = "critical", Count = 2, Title = "2 open tickets past their SLA" });
        breached.Detail.Should().StartWith("Longest overdue: INT-1");
        // The linked list shows the waiting ones too; the item says why its count is smaller.
        breached.Detail.Should().Contain("Not counted: 2 more waiting on the customer or on hold.");
        items.Single(i => i.Kind == "sla-at-risk").Should().BeEquivalentTo(new { Count = 1, Title = "1 ticket will breach its SLA within 2 hours" });
    }

    [Fact]
    public async Task A_board_ticket_nobody_has_answered_past_its_reply_time_is_listed_until_someone_does()
    {
        var (svc, h, _) = await BuildAsync();
        var now = h.Clock.GetUtcNow();
        var waiting = Sla("INT-9", "NEW", now.AddHours(20), replyDue: now.AddHours(-1));
        var answered = Sla("INT-10", "NEW", now.AddHours(20), replyDue: now.AddHours(-1));
        answered.FirstRespondedAt = now.AddHours(-2);
        h.Db.Tickets.AddRange(waiting, answered);
        await h.Db.SaveChangesAsync();

        var item = (await svc.ListAsync()).Items.Single(i => i.Kind == "sla-reply-overdue");
        item.Count.Should().Be(1);
        item.Detail.Should().Contain("INT-9");
    }

    [Fact]
    public async Task No_sla_trouble_means_no_sla_items()
    {
        var (svc, _, _) = await BuildAsync();
        (await svc.ListAsync()).Items.Should().NotContain(i => i.Kind.StartsWith("sla-"));
    }
}
