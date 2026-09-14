using System.Text;
using Desk.Application.Common;
using Desk.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Scheduled reports used to go to a delivery that only wrote a log line, so every run said "not
/// delivered" and nobody ever received one. These pin what each outcome now tells the scheduler.
/// </summary>
public class EmailDeliveryTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class FakeSender(bool configured = true, Exception? fail = null) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
            => Task.FromResult(configured ? new EmailSenderStatus(true, "reports@techpio.test", "organization") : EmailSenderStatus.None);

        public Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default)
        {
            if (fail is not null) throw fail;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static EmailReportDelivery Delivery(FakeSender sender) => new(sender, NullLogger<EmailReportDelivery>.Instance);

    [Fact]
    public void Recipients_split_on_commas_semicolons_and_lines_and_report_the_typos()
    {
        var (valid, invalid) = EmailAddresses.Parse("a@acme.com; Bob <b@acme.com>,\nnot-an-email, c@localhost, A@acme.com");

        valid.Should().Equal("a@acme.com", "b@acme.com");
        invalid.Should().Equal("not-an-email", "c@localhost");
    }

    [Fact]
    public async Task A_report_is_emailed_with_the_csv_attached()
    {
        var sender = new FakeSender();

        var result = await Delivery(sender).DeliverAsync(Org, "ops@acme.com, it@acme.com", "Acme — scheduled report", "Acme-report.csv", "Metric,Value\nOpen,3");

        result.Delivered.Should().BeTrue();
        result.Note.Should().Be("Emailed to 2 recipients.");
        var message = sender.Sent.Single();
        message.To.Should().Equal("ops@acme.com", "it@acme.com");
        var attachment = message.Attachments!.Single();
        attachment.FileName.Should().Be("Acme-report.csv");
        // BOM first, so Excel opens accented client names correctly.
        attachment.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(attachment.Content.Skip(3).ToArray()).Should().Be("Metric,Value\nOpen,3");
    }

    [Fact]
    public async Task Without_a_mail_server_the_run_says_so_instead_of_claiming_a_send()
    {
        var sender = new FakeSender(configured: false);

        var result = await Delivery(sender).DeliverAsync(Org, "ops@acme.com", "s", "f.csv", "x");

        result.Delivered.Should().BeFalse();
        result.Note.Should().Contain("not configured");
        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_addresses_are_skipped_and_named_while_the_valid_ones_still_receive_it()
    {
        var sender = new FakeSender();

        var result = await Delivery(sender).DeliverAsync(Org, "ops@acme.com; opsacme.com", "s", "f.csv", "x");

        result.Delivered.Should().BeTrue();
        result.Note.Should().Be("Emailed to 1 recipient. Skipped invalid: opsacme.com.");
    }

    [Fact]
    public async Task A_refused_send_is_recorded_on_the_run_rather_than_failing_the_schedule()
    {
        var sender = new FakeSender(fail: new InvalidOperationException("535 authentication failed"));

        var result = await Delivery(sender).DeliverAsync(Org, "ops@acme.com", "s", "f.csv", "x");

        result.Delivered.Should().BeFalse();
        result.Note.Should().Contain("535 authentication failed").And.Contain("available in the portal");
    }

    [Fact]
    public async Task No_recipients_means_portal_only()
    {
        var result = await Delivery(new FakeSender()).DeliverAsync(Org, "  ", "s", "f.csv", "x");

        result.Should().BeEquivalentTo(new { Delivered = false, Note = "No recipients set — report available in the portal." });
    }

    [Fact]
    public void Several_recipients_are_blind_copied_so_no_one_receives_the_others_addresses()
    {
        var mime = SmtpEmailSender.Build(new EmailMessage(["a@acme.com", "b@acme.com"], "s", "body"), "reports@techpio.test", "Desk Portal");

        mime.To.Mailboxes.Select(m => m.Address).Should().Equal("reports@techpio.test");
        mime.Bcc.Mailboxes.Select(m => m.Address).Should().Equal("a@acme.com", "b@acme.com");
    }

    [Fact]
    public void A_single_recipient_is_addressed_directly()
    {
        var mime = SmtpEmailSender.Build(new EmailMessage(["a@acme.com"], "s", "body"), "reports@techpio.test", "Desk Portal");

        mime.To.Mailboxes.Select(m => m.Address).Should().Equal("a@acme.com");
        mime.Bcc.Count.Should().Be(0);
    }
}
