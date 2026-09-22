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
}
