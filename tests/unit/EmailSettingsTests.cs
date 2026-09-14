using Desk.Application.Common;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Email;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The mail account an administrator enters in the portal: where its password goes, what editing
/// keeps, and which account an organization's mail actually leaves through.
/// </summary>
public class EmailSettingsTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static (EmailSettingsService Svc, SmtpEmailSender Sender, AdminHarness H) Build(SmtpOptions? server = null, Guid? org = null, string? db = null)
    {
        var h = AdminHarness.Create(org ?? Org, db);
        server ??= new SmtpOptions();
        var sender = new SmtpEmailSender(h.Db, h.Secrets, server);
        var svc = new EmailSettingsService(h.Db, h.Tenant, h.User, h.Secrets, sender, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), server);
        return (svc, sender, h);
    }

    private static EmailSettingsInput Input(string? password = "s3cret-Pa55", string host = "smtp.office365.com", string? username = "reports@techpio.com")
        => new(host, 587, "StartTls", username, password, "reports@techpio.com", "TechPio Reports");

    [Fact]
    public async Task The_password_goes_to_the_secret_store_and_never_comes_back_out()
    {
        var (svc, _, h) = Build();

        var saved = await svc.SaveAsync(Input());

        var row = await h.Db.OrganizationEmailSettings.SingleAsync();
        row.PasswordSecretRef.Should().NotBeNull();
        (await h.Secrets.ReadAsync(row.PasswordSecretRef!))["Password"].Should().Be("s3cret-Pa55");
        saved.Should().BeEquivalentTo(new { HasOwnAccount = true, HasPassword = true, Host = "smtp.office365.com", FromAddress = "reports@techpio.com" });
        saved.Status.Should().Be(new EmailSenderStatus(true, "reports@techpio.com", "organization"));
        // Nothing the API returns or the audit log holds carries the password.
        System.Text.Json.JsonSerializer.Serialize(saved).Should().NotContain("s3cret");
        (await h.Db.AuditLog.SingleAsync(a => a.Action == "email.settings.created")).DetailJson.Should().NotContain("s3cret");
    }

    [Fact]
    public async Task Leaving_the_password_blank_keeps_it_and_an_empty_one_removes_it()
    {
        var (svc, _, h) = Build();
        await svc.SaveAsync(Input());

        await svc.SaveAsync(Input(password: null, host: "smtp.sendgrid.net"));
        var row = await h.Db.OrganizationEmailSettings.SingleAsync();
        row.Host.Should().Be("smtp.sendgrid.net");
        (await h.Secrets.ReadAsync(row.PasswordSecretRef!))["Password"].Should().Be("s3cret-Pa55");

        await svc.SaveAsync(Input(password: "", username: null));
        (await h.Db.OrganizationEmailSettings.SingleAsync()).PasswordSecretRef.Should().BeNull();
    }

    [Fact]
    public async Task A_username_without_a_password_is_refused_and_nothing_is_written()
    {
        var (svc, _, h) = Build();

        var act = () => svc.SaveAsync(Input(password: null));

        await act.Should().ThrowAsync<ValidationFailedException>().WithMessage("*password*");
        (await h.Db.OrganizationEmailSettings.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    public async Task A_private_mail_server_is_refused_when_egress_is_guarded(string host)
    {
        // Otherwise the form is a way to open connections to services inside the host's network.
        var (svc, _, h) = Build(new SmtpOptions { BlockPrivateHosts = true });

        var act = () => svc.SaveAsync(Input(host: host));

        await act.Should().ThrowAsync<ValidationFailedException>().WithMessage("*private network*");
        (await h.Db.OrganizationEmailSettings.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("https://smtp.office365.com")]
    [InlineData("smtp.office365.com/path")]
    public async Task Only_a_server_name_is_accepted(string host)
    {
        var (svc, _, _) = Build();
        await ((Func<Task>)(() => svc.SaveAsync(Input(host: host)))).Should().ThrowAsync<ValidationFailedException>();
    }

    [Fact]
    public async Task The_organizations_own_account_wins_over_the_server_fallback_and_removing_it_falls_back()
    {
        var server = new SmtpOptions { Host = "smtp.server.test", From = "noreply@server.test" };
        var (svc, sender, _) = Build(server);

        (await sender.StatusAsync(Org)).Should().Be(new EmailSenderStatus(true, "noreply@server.test", "server"));

        await svc.SaveAsync(Input());
        (await sender.StatusAsync(Org)).Source.Should().Be("organization");

        await svc.RemoveAsync();
        (await sender.StatusAsync(Org)).Source.Should().Be("server");
    }

    [Fact]
    public async Task One_organizations_account_is_never_used_for_another()
    {
        var db = Guid.NewGuid().ToString();
        var (svc, _, _) = Build(db: db);
        await svc.SaveAsync(Input());

        // The worker asks from platform scope with an explicit organization; a different one has none.
        var (_, otherSender, _) = Build(org: Guid.NewGuid(), db: db);
        (await otherSender.StatusAsync(Guid.NewGuid())).Should().Be(EmailSenderStatus.None);
        (await otherSender.StatusAsync(Org)).Source.Should().Be("organization");
    }
}
