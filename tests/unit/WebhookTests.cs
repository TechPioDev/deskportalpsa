using System.Text;
using Desk.Api.Controllers;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The inbound webhook route: anonymous, so the signature is the whole of its security. It had no
/// test. With no secret stored the signing key was the empty string, and the timestamp that was
/// meant to stop replays was checked but not signed.
/// </summary>
public class WebhookTests
{
    private const string Secret = "s3cret-for-this-connection";
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class Resolver(IServiceManagementConnector? connector, Exception? failure = null) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default)
            => failure is not null ? throw failure : Task.FromResult(connector!);
    }

    /// <summary><paramref name="Db"/> reads what the deliveries wrote, across tenants, for the assertions.</summary>
    private sealed record World(
        DeskDbContext Db, DbContextOptions<DeskDbContext> Options, IConnectorResolver Resolver, TestClock Clock, Guid Connection);

    private static async Task<World> WorldAsync(string? secret = Secret, bool enabled = true, Exception? resolveFailure = null)
    {
        var clock = new TestClock();
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var connection = new PsaConnection
        {
            MspOrganizationId = OrgA, Name = "CW", Provider = ProviderType.ConnectWisePsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", IsEnabled = enabled,
        };
        var db = new DeskDbContext(options, Platform(), clock);
        db.PsaConnections.Add(connection);
        await db.SaveChangesAsync();

        var connector = new MockConnector(new MockConnectorOptions { WebhookSecret = secret ?? "" }, clock);
        return new World(db, options, new Resolver(connector, resolveFailure), clock, connection.Id);
    }

    private static TenantContext Platform()
    {
        var t = new TenantContext();
        t.SetPlatformScope();
        return t;
    }

    private static Task<IActionResult> DeliverAsync(World w, string body, string? signedWith = Secret,
        DateTimeOffset? signedAt = null, DateTimeOffset? headerTime = null, Guid? to = null, string? signature = null)
    {
        var timestamp = (signedAt ?? w.Clock.GetUtcNow()).ToString("o");
        var http = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        http.Request.Headers[WebhookSignature.TimestampHeader] = (headerTime ?? signedAt ?? w.Clock.GetUtcNow()).ToString("o");
        if ((signature ?? (signedWith is null ? null : WebhookSignature.Compute(timestamp, body, signedWith))) is { } s)
            http.Request.Headers["X-Signature"] = s;

        // A request of its own, as the API gives each delivery: no tenant yet, its own unit of work.
        var tenant = new TenantContext();
        var db = new DeskDbContext(w.Options, tenant, w.Clock);
        var controller = new WebhooksController(w.Resolver, new SyncEventStore(db, w.Clock), tenant, db, w.Clock,
            NullLogger<WebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return controller.Receive(to ?? w.Connection, default);
    }

    private const string Event = "{\"eventType\":\"ticket.updated\",\"ticketId\":\"1001\",\"id\":\"evt-1\"}";

    [Fact]
    public async Task A_signed_delivery_is_accepted_and_filed_under_the_connections_own_organization()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        // The body claims another tenant. Nothing in a delivery can name one.
        var body = "{\"eventType\":\"ticket.updated\",\"ticketId\":\"1001\",\"id\":\"evt-1\",\"organizationId\":\"" + OrgB + "\",\"connectionId\":\"" + Guid.NewGuid() + "\"}";

        var result = await DeliverAsync(w, body);

        result.Should().BeOfType<AcceptedResult>();
        var evt = await w.Db.SyncEvents.SingleAsync();
        (evt.MspOrganizationId, evt.PsaConnectionId, evt.IdempotencyKey).Should().Be((OrgA, w.Connection, "evt-1"));
        (await w.Db.BackgroundJobs.SingleAsync()).MspOrganizationId.Should().Be(OrgA);
    }

    [Fact]
    public async Task A_connection_with_no_webhook_secret_accepts_nothing()
    {
        // The key was "" when none was stored, so this exact request - signed with the empty
        // string, which anyone can do - was accepted.
        var w = await WorldAsync(secret: null);
        await using var _ = w.Db;

        var result = await DeliverAsync(w, Event, signedWith: "");

        result.Should().BeOfType<UnauthorizedResult>();
        (await w.Db.SyncEvents.CountAsync()).Should().Be(0);
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("wrong-secret", null)]
    [InlineData(null, null)]              // no signature at all
    [InlineData(Secret, "deadbeef")]      // a signature, not this one
    public async Task A_delivery_that_is_not_signed_with_the_connections_secret_is_refused(string? signedWith, string? signature)
    {
        var w = await WorldAsync();
        await using var _ = w.Db;

        var result = await DeliverAsync(w, Event, signedWith, signature: signature);

        result.Should().BeOfType<UnauthorizedResult>();
        (await w.Db.SyncEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_captured_delivery_is_refused_when_replayed_later_even_with_the_time_changed()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var sent = w.Clock.GetUtcNow() - TimeSpan.FromHours(1);

        // As captured: an hour old.
        (await DeliverAsync(w, Event, signedAt: sent)).Should().BeOfType<UnauthorizedResult>();
        // Its signature kept, its timestamp header moved to now.
        (await DeliverAsync(w, Event, signedAt: sent, headerTime: w.Clock.GetUtcNow())).Should().BeOfType<UnauthorizedResult>();
        (await w.Db.SyncEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_same_delivery_twice_is_acknowledged_and_queued_once()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;

        (await DeliverAsync(w, Event)).Should().BeOfType<AcceptedResult>();
        (await DeliverAsync(w, Event)).Should().BeOfType<OkObjectResult>();

        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_or_disabled_connection_is_not_found()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        (await DeliverAsync(w, Event, to: Guid.NewGuid())).Should().BeOfType<NotFoundResult>();

        var off = await WorldAsync(enabled: false);
        await using var __ = off.Db;
        (await DeliverAsync(off, Event)).Should().BeOfType<NotFoundResult>();
        (await off.Db.SyncEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_signed_delivery_that_is_not_a_notification_is_a_bad_request_not_an_error()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;

        (await DeliverAsync(w, "this is not json")).Should().BeOfType<BadRequestResult>();
        (await w.Db.BackgroundJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_body_larger_than_a_notification_is_refused_before_it_is_read()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;

        var result = await DeliverAsync(w, new string('x', WebhooksController.MaxBodyBytes + 1));

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task A_connection_whose_connector_cannot_be_built_refuses_the_delivery_without_an_error()
    {
        var w = await WorldAsync(resolveFailure: new ValidationFailedException("'CW' has no valid stored credentials"));
        await using var _ = w.Db;

        (await DeliverAsync(w, Event)).Should().BeOfType<UnauthorizedResult>();
    }

    // ---- the shared check itself --------------------------------------------------------------

    private static WebhookRequest Request(string body, string timestamp, string? signature, DateTimeOffset receivedAt)
        => new(new Dictionary<string, string> { [WebhookSignature.TimestampHeader] = timestamp }, body, signature, receivedAt);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void With_no_secret_no_signature_is_valid(string? secret)
    {
        var now = new TestClock().GetUtcNow();
        var timestamp = now.ToString("o");

        var result = WebhookSignature.Validate(Request("{}", timestamp, WebhookSignature.Compute(timestamp, "{}", ""), now), secret, TimeSpan.FromMinutes(5));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("secret");
    }

    [Fact]
    public void The_signature_covers_the_timestamp_and_the_body()
    {
        var now = new TestClock().GetUtcNow();
        var timestamp = now.ToString("o");
        var signature = WebhookSignature.Compute(timestamp, "{\"a\":1}", Secret);
        var skew = TimeSpan.FromMinutes(5);

        WebhookSignature.Validate(Request("{\"a\":1}", timestamp, signature, now), Secret, skew).IsValid.Should().BeTrue();
        WebhookSignature.Validate(Request("{\"a\":2}", timestamp, signature, now), Secret, skew).IsValid.Should().BeFalse("the body changed");
        WebhookSignature.Validate(Request("{\"a\":1}", now.AddSeconds(1).ToString("o"), signature, now), Secret, skew).IsValid.Should().BeFalse("the timestamp changed");
        WebhookSignature.Validate(Request("{\"a\":1}", timestamp, signature, now.AddMinutes(6)), Secret, skew).IsValid.Should().BeFalse("it arrived too late");
    }
}
