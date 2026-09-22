using System.Net;
using System.Text;
using System.Text.Json;
using Desk.Application.Common;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Email;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Microsoft 365 sending through Graph: what is stored, what is sent to Microsoft, and how its
/// refusals are explained.
/// </summary>
public class GraphMailTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

    /// <summary>Answers the token and sendMail calls, recording what was asked.</summary>
    private sealed class FakeMicrosoft(HttpStatusCode send = HttpStatusCode.Accepted, string? tokenError = null) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return tokenError is null
                    ? Json(HttpStatusCode.OK, """{"access_token":"tok-123","expires_in":3599}""")
                    : Json(HttpStatusCode.Unauthorized, tokenError);
            return send == HttpStatusCode.Accepted
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : Json(send, """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""");
        }
        private static HttpResponseMessage Json(HttpStatusCode code, string json)
            => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (EmailSettingsService Svc, SmtpEmailSender Sender, AdminHarness H) Build(FakeMicrosoft ms)
    {
        var h = AdminHarness.Create(Org);
        var sender = new SmtpEmailSender(h.Db, h.Secrets, new SmtpOptions(), new Factory(ms));
        var svc = new EmailSettingsService(h.Db, h.Tenant, h.User, h.Secrets, sender, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), new SmtpOptions());
        return (svc, sender, h);
    }

    private static EmailSettingsInput Graph(string? secret = "s3cret~value", string tenant = Tenant, string client = Client)
        => new("", 0, "", null, secret, "reports@techpio.com", "TechPio Reports", "Graph", tenant, client);

    [Fact]
    public async Task Saving_keeps_the_ids_visible_and_the_secret_in_the_store()
    {
        var (svc, _, h) = Build(new FakeMicrosoft());

        var saved = await svc.SaveAsync(Graph());

        saved.Should().BeEquivalentTo(new { Method = "Graph", GraphTenantId = Tenant, GraphClientId = Client.ToLowerInvariant(), HasPassword = true, FromAddress = "reports@techpio.com" });
        var row = await h.Db.OrganizationEmailSettings.SingleAsync();
        (await h.Secrets.ReadAsync(row.PasswordSecretRef!))["Password"].Should().Be("s3cret~value");
        (await h.Db.AuditLog.SingleAsync(a => a.Action == "email.settings.created")).DetailJson.Should().NotContain("s3cret");
    }

    [Theory]
    [InlineData("not a tenant!", Client, "Directory (tenant) ID")]
    [InlineData(Tenant, "abc", "Application (client) ID")]
    [InlineData("techpio.onmicrosoft.com", "abc", "Application (client) ID")]
    public async Task Bad_ids_are_refused_with_the_field_to_fix(string tenant, string client, string mentions)
    {
        var (svc, _, _) = Build(new FakeMicrosoft());
        var act = () => svc.SaveAsync(Graph(tenant: tenant, client: client));
        (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain(mentions);
    }

    [Fact]
    public async Task Switching_from_SMTP_needs_a_new_secret_and_editing_Graph_keeps_it()
    {
        var (svc, _, _) = Build(new FakeMicrosoft());
        await svc.SaveAsync(new EmailSettingsInput("smtp.office365.com", 587, "StartTls", "reports@techpio.com", "smtp-pass", "reports@techpio.com", null));

        var noSecret = () => svc.SaveAsync(Graph(secret: null));
        (await noSecret.Should().ThrowAsync<ValidationFailedException>()).Which.Message.Should().Contain("client secret");

        await svc.SaveAsync(Graph());
        (await svc.SaveAsync(Graph(secret: null) with { FromName = "Renamed" })).Should().BeEquivalentTo(new { HasPassword = true, FromName = "Renamed" });
    }

    [Fact]
    public async Task Sending_gets_a_token_then_posts_to_the_mailbox_with_the_attachment()
    {
        var ms = new FakeMicrosoft();
        var (svc, sender, _) = Build(ms);
        await svc.SaveAsync(Graph());

        await sender.SendAsync(Org, new EmailMessage(["ops@techpio.com"], "Daily report", "text", "<p>html</p>",
            [new EmailAttachment("report.pdf", "application/pdf", [1, 2, 3])]));

        ms.Calls.Should().HaveCount(2);
        ms.Calls[0].Url.Should().Be($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token");
        ms.Calls[0].Body.Should().Contain("grant_type=client_credentials").And.Contain("client_secret=s3cret~value")
            .And.Contain("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default");
        ms.Calls[1].Url.Should().Be("https://graph.microsoft.com/v1.0/users/reports%40techpio.com/sendMail");
        ms.Calls[1].Auth.Should().Be("Bearer tok-123");

        using var doc = JsonDocument.Parse(ms.Calls[1].Body);
        var m = doc.RootElement.GetProperty("message");
        m.GetProperty("subject").GetString().Should().Be("Daily report");
        m.GetProperty("body").GetProperty("contentType").GetString().Should().Be("HTML");
        m.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString().Should().Be("ops@techpio.com");
        m.GetProperty("attachments")[0].GetProperty("contentBytes").GetString().Should().Be("AQID");
        m.GetProperty("attachments")[0].GetProperty("@odata.type").GetString().Should().Be("#microsoft.graph.fileAttachment");
        doc.RootElement.GetProperty("saveToSentItems").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Several_recipients_go_in_bcc()
    {
        var body = JsonSerializer.Serialize(GraphMailSender.Body(
            new EmailMessage(["a@x.com", "b@y.com"], "s", "t"), "reports@techpio.com", "Reports"));
        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement.GetProperty("message");
        m.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString().Should().Be("reports@techpio.com");
        m.GetProperty("bccRecipients").GetArrayLength().Should().Be(2);
        m.GetProperty("body").GetProperty("contentType").GetString().Should().Be("Text");
    }

    [Fact]
    public async Task A_wrong_secret_is_explained_in_the_words_of_the_fix()
    {
        var (svc, sender, _) = Build(new FakeMicrosoft(tokenError: """{"error":"invalid_client","error_codes":[7000215]}"""));
        await svc.SaveAsync(Graph());

        var act = () => sender.SendAsync(Org, new EmailMessage(["ops@techpio.com"], "s", "t"));
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Value (not its Secret ID)");
    }

    [Fact]
    public async Task A_missing_permission_names_Mail_Send()
    {
        var (svc, sender, _) = Build(new FakeMicrosoft(send: HttpStatusCode.Forbidden));
        await svc.SaveAsync(Graph());

        var act = () => sender.SendAsync(Org, new EmailMessage(["ops@techpio.com"], "s", "t"));
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Mail.Send").And.Contain("reports@techpio.com");
    }

    [Fact]
    public void Oversized_attachments_are_refused_before_anything_is_sent()
    {
        var big = new byte[GraphMailSender.MaxInlineBytes + 1];
        var act = () => GraphMailSender.Body(new EmailMessage(["a@x.com"], "s", "t", null,
            [new EmailAttachment("big.pdf", "application/pdf", big)]), "r@x.com", "R");
        act.Should().Throw<InvalidOperationException>().WithMessage("*3 MB*");
    }
}
