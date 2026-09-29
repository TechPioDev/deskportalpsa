using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Notifications;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Notifications;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Push notifications for staff: who can sign a device up and where to, the three events found by
/// comparing each ticket with what was seen last time, and delivery through the push service -
/// encrypted to the device, forgotten when the device has gone.
/// </summary>
public class PushTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // A real browser key pair (the one RFC 8291 uses), so a delivered message can be decrypted here.
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Fcm = "https://fcm.googleapis.com/fcm/send/device-1";

    private sealed class FakeSecrets : ISecretStore
    {
        public Dictionary<string, IReadOnlyDictionary<string, string>> Stored { get; } = [];
        public Task<string> WriteAsync(string logicalName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        { var r = $"mem://{Guid.NewGuid():N}"; Stored[r] = data; return Task.FromResult(r); }
        public Task<IReadOnlyDictionary<string, string>> ReadAsync(string secretRef, CancellationToken ct = default)
            => Task.FromResult(Stored[secretRef]);
        public Task<string> RotateAsync(string secretRef, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        { Stored[secretRef] = data; return Task.FromResult(secretRef); }
        public Task DeleteAsync(string secretRef, CancellationToken ct = default) { Stored.Remove(secretRef); return Task.CompletedTask; }
    }

    /// <summary>A push service: records what it was sent and answers as told.</summary>
    private sealed class FakePushService : HttpMessageHandler
    {
        public List<(Uri Uri, byte[] Body, string? Encoding, string? Authorization)> Received { get; } = [];
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request.RequestUri!, await request.Content!.ReadAsByteArrayAsync(ct),
                request.Content.Headers.ContentEncoding.FirstOrDefault(),
                request.Headers.TryGetValues("Authorization", out var a) ? a.First() : null));
            return new HttpResponseMessage(Answer);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Kit(AdminHarness H, PushService Push, PushScanner Scanner, PushDelivery Delivery,
        FakePushService Service, FakeSecrets Secrets, Guid Anika, Guid Rohan, Guid Conn);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        h.Db.MspOrganizations.Add(new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio-" + Guid.NewGuid().ToString("N")[..6] });
        var anika = new AppUser { MspOrganizationId = Org, DisplayName = "Anika Sharma", Email = "anika@techpio.test", IsActive = true };
        var rohan = new AppUser { MspOrganizationId = Org, DisplayName = "Rohan Mehta", Email = "rohan@techpio.test", IsActive = true };
        var conn = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        };
        h.Db.AddRange(anika, rohan, conn);
        await h.Db.SaveChangesAsync();

        var secrets = new FakeSecrets();
        var service = new FakePushService();
        var delivery = new PushDelivery(h.Db, h.Tenant, secrets, new Factory(service), h.Clock, NullLogger<PushDelivery>.Instance);
        return new Kit(h, new PushService(h.Db, h.Tenant, secrets, delivery), new PushScanner(h.Db, h.Clock), delivery,
            service, secrets, anika.Id, rohan.Id, conn.Id);
    }

    private static Task SubscribeAsync(Kit k, Guid user, string endpoint = Fcm)
        => k.Push.SubscribeAsync(user, new PushSubscribeInput(endpoint, UaPublic, Auth, "Chrome on Android"));

    private static async Task<Ticket> TicketAsync(Kit k, Guid? holder = null, DateTimeOffset? due = null)
    {
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Internal, Number = "INT-000014", RequesterName = "Priya", RequesterEmail = "p@acme.test",
            Title = "Printer jam in Finance", PortalStatus = "IN_PROGRESS", AssignedAppUserId = holder, SlaDueAt = due,
        };
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();
        return t;
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://intranet.techpio.local/push")]
    [InlineData("https://evil.example/fcm.googleapis.com")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    public async Task A_device_can_only_be_signed_up_at_a_real_push_service(string endpoint)
    {
        var k = await BuildAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() => SubscribeAsync(k, k.Anika, endpoint));
        (await k.H.Db.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task One_browser_belongs_to_whoever_signed_it_up_last_and_the_key_is_made_once()
    {
        var k = await BuildAsync();
        var first = (await k.Push.StatusAsync(k.Anika)).PublicKey;

        await SubscribeAsync(k, k.Anika);
        await SubscribeAsync(k, k.Rohan); // same browser, Rohan signs in and turns them on

        (await k.H.Db.PushSubscriptions.SingleAsync()).AppUserId.Should().Be(k.Rohan);
        (await k.Push.StatusAsync(k.Anika)).Devices.Should().BeEmpty();
        // The same key every time; the private half lives in the secret store, not on the row.
        (await k.Push.StatusAsync(k.Rohan)).PublicKey.Should().Be(first);
        var org = await k.H.Db.MspOrganizations.SingleAsync();
        org.PushPrivateKeyRef.Should().StartWith("mem://");
        k.Secrets.Stored.Should().ContainSingle();
    }

    [Fact]
    public async Task A_ticket_seen_for_the_first_time_is_remembered_not_announced()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        await TicketAsync(k, holder: k.Anika, due: k.H.Clock.GetUtcNow().AddMinutes(30));

        (await k.Scanner.ScanAsync()).Should().Be(0);
        (await k.H.Db.PushTicketStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Assigning_a_ticket_tells_the_new_holder_once()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        var t = await TicketAsync(k);
        await k.Scanner.ScanAsync();

        t.AssignedAppUserId = k.Anika;
        await k.H.Db.SaveChangesAsync();
        (await k.Scanner.ScanAsync()).Should().Be(1);
        (await k.Scanner.ScanAsync()).Should().Be(0);

        (await k.H.Db.PushNotifications.SingleAsync()).Should().BeEquivalentTo(new
        {
            AppUserId = k.Anika, Kind = PushKind.Assigned, Title = "INT-000014 assigned to you", Body = "Printer jam in Finance",
            Url = $"/dashboard/tickets/{t.Id}",
        });
    }

    [Fact]
    public async Task A_psa_assignment_reaches_the_technician_linked_to_that_psa_account()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Rohan);
        k.H.Db.UserPsaIdentities.Add(new UserPsaIdentity
        { MspOrganizationId = Org, AppUserId = k.Rohan, PsaConnectionId = k.Conn, ExternalTechnicianId = "29682887" });
        var t = new Ticket
        {
            MspOrganizationId = Org, Origin = TicketOrigin.Psa, PsaConnectionId = k.Conn, ExternalTicketId = "T20260929.0001",
            RequesterName = "Priya", RequesterEmail = "p@acme.test", Title = "Outlook keeps asking for the password", PortalStatus = "NEW",
        };
        k.H.Db.Tickets.Add(t);
        await k.H.Db.SaveChangesAsync();
        await k.Scanner.ScanAsync();

        // Assigned in Autotask; the sync writes the resource id.
        t.AssignedTechnicianExternalId = "29682887";
        await k.H.Db.SaveChangesAsync();
        await k.Scanner.ScanAsync();

        (await k.H.Db.PushNotifications.SingleAsync()).Should().BeEquivalentTo(new { AppUserId = k.Rohan, Title = "T20260929.0001 assigned to you" });
    }

    [Fact]
    public async Task A_client_reply_tells_the_holder_with_its_first_line_and_an_internal_note_does_not()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        var t = await TicketAsync(k, holder: k.Anika);
        await k.Scanner.ScanAsync();

        k.H.Db.TicketNotes.AddRange(
            new TicketNote { MspOrganizationId = Org, TicketId = t.Id, AuthorName = "Rohan", Body = "Internal: ordered toner", IsPublic = false, NoteCreatedAt = k.H.Clock.GetUtcNow() },
            new TicketNote { MspOrganizationId = Org, TicketId = t.Id, AuthorName = "Priya", AuthoredByClient = true, IsPublic = true,
                Body = "Still jamming after the restart.\nPhoto attached.", NoteCreatedAt = k.H.Clock.GetUtcNow().AddMinutes(1) });
        await k.H.Db.SaveChangesAsync();
        await k.Scanner.ScanAsync();
        (await k.Scanner.ScanAsync()).Should().Be(0);

        (await k.H.Db.PushNotifications.SingleAsync()).Should().BeEquivalentTo(new
        { Kind = PushKind.ClientReplied, Title = "Priya replied on INT-000014", Body = "Still jamming after the restart." });
    }

    [Fact]
    public async Task A_ticket_about_to_breach_warns_once_per_due_time_and_not_while_paused()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        var now = k.H.Clock.GetUtcNow();
        var t = await TicketAsync(k, holder: k.Anika, due: now.AddHours(5));
        await k.Scanner.ScanAsync();

        k.H.Clock.Advance(TimeSpan.FromHours(3.5)); // now 90 minutes to go
        (await k.Scanner.ScanAsync()).Should().Be(1);
        (await k.Scanner.ScanAsync()).Should().Be(0);
        (await k.H.Db.PushNotifications.SingleAsync()).Title.Should().Be("INT-000014 is due in 1h 30m");

        // Paused while waiting on the customer: not the technician's clock, no warning for the new date.
        t.SlaDueAt = k.H.Clock.GetUtcNow().AddMinutes(40);
        t.SlaPausedAt = k.H.Clock.GetUtcNow();
        await k.H.Db.SaveChangesAsync();
        (await k.Scanner.ScanAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Someone_who_switched_an_event_off_or_has_no_device_is_not_queued_anything()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        await k.Push.SavePreferencesAsync(k.Anika, new PushPreferencesDto(Assigned: false, ClientReplied: true, SlaAtRisk: true));
        var mine = await TicketAsync(k);
        var rohans = await TicketAsync(k);
        await k.Scanner.ScanAsync();

        mine.AssignedAppUserId = k.Anika;   // Anika turned assignments off
        rohans.AssignedAppUserId = k.Rohan; // Rohan has no device
        await k.H.Db.SaveChangesAsync();

        (await k.Scanner.ScanAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_notification_reaches_the_device_encrypted_to_it_and_signed_for_the_push_service()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);

        (await k.Push.SendTestAsync(k.Anika)).Should().Be(1);

        var (uri, body, encoding, authorization) = k.Service.Received.Single();
        uri.ToString().Should().Be(Fcm);
        encoding.Should().Be("aes128gcm");
        authorization.Should().StartWith("vapid t=").And.Contain($"k={(await k.Push.StatusAsync(k.Anika)).PublicKey}");
        // Only the device can read it; here, with the device's private key, it says what it should.
        var message = JsonDocument.Parse(Decrypt(body)).RootElement;
        message.GetProperty("title").GetString().Should().Be("Notifications are working");
        message.GetProperty("url").GetString().Should().Be("/dashboard/profile");
    }

    [Fact]
    public async Task A_device_the_push_service_says_is_gone_is_forgotten_and_failures_stop_after_three_tries()
    {
        var k = await BuildAsync();
        await SubscribeAsync(k, k.Anika);
        k.Service.Answer = HttpStatusCode.InternalServerError;
        k.H.Db.PushNotifications.Add(new PushNotification { MspOrganizationId = Org, AppUserId = k.Anika, Kind = PushKind.Assigned, Title = "x", Body = "y", Url = "/" });
        await k.H.Db.SaveChangesAsync();

        for (var i = 0; i < 4; i++) await k.Delivery.DeliverPendingAsync();
        var note = await k.H.Db.PushNotifications.SingleAsync();
        note.Should().BeEquivalentTo(new { Attempts = 3, Delivered = 0 });
        note.SentAt.Should().NotBeNull("three failures and it is given up, not retried forever");
        k.Service.Received.Should().HaveCount(3);

        k.Service.Answer = HttpStatusCode.Gone;
        await k.Push.SendTestAsync(k.Anika);
        (await k.H.Db.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    private static byte[] Decrypt(byte[] body)
    {
        static byte[] B(string s) => WebPushCrypto.FromBase64Url(s);
        var salt = body[..16];
        var asPublic = body[21..(21 + body[20])];
        var payload = body[(21 + body[20])..];
        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = B(UaPrivate),
            Q = new ECPoint { X = B(UaPublic)[1..33], Y = B(UaPublic)[33..65] },
        });
        using var sender = ECDiffieHellman.Create(new ECParameters
        { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, receiver.DeriveRawSecretAgreement(sender.PublicKey), 32, B(Auth),
            Encoding.ASCII.GetBytes("WebPush: info\0").Concat(B(UaPublic)).Concat(asPublic).ToArray());
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var plain = new byte[payload.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, payload[..^16], payload[^16..], plain);
        return plain[..^1];
    }
}
