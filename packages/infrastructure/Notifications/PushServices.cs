using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Notifications;
using Desk.Domain.Notifications;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Notifications;

/// <summary>Where a browser's push endpoint may point. Anything else is refused on sign-up and again
/// before sending: the server POSTs to this URL, and a URL chosen by a user must never be a way into
/// the server's own network.</summary>
public static class PushEndpoints
{
    private static readonly string[] Services =
    [
        "fcm.googleapis.com",          // Chrome, Edge on Android, most Chromium browsers
        "push.services.mozilla.com",   // Firefox
        "push.apple.com",              // Safari (macOS, and iPhone once installed to the Home Screen)
        "notify.windows.com",          // Edge on Windows
    ];

    public static bool IsAllowed(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.IsDefaultPort
           && Services.Any(s => uri.Host.Equals(s, StringComparison.OrdinalIgnoreCase)
                                || uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The organization's Web Push key pair: made once, the private half kept in the secret store.</summary>
public static class PushKeys
{
    public static async Task<(string PublicKey, string PrivateKey)> GetOrCreateAsync(
        DeskDbContext db, ISecretStore secrets, Guid organizationId, CancellationToken ct)
    {
        var org = await db.MspOrganizations.FirstOrDefaultAsync(o => o.Id == organizationId, ct)
            ?? throw new NotFoundException("Organization");
        if (org.PushPublicKey is { } existing && org.PushPrivateKeyRef is { } reference)
        {
            var stored = await secrets.ReadAsync(reference, ct);
            return (existing, stored["privateKey"]);
        }

        var (publicKey, privateKey) = WebPushCrypto.NewVapidKeys();
        org.PushPrivateKeyRef = await secrets.WriteAsync($"push-vapid-{organizationId:N}",
            new Dictionary<string, string> { ["privateKey"] = privateKey }, ct);
        org.PushPublicKey = publicKey;
        await db.SaveChangesAsync(ct);
        return (publicKey, privateKey);
    }
}

/// <summary>A staff member's devices and preferences, from their Profile page.</summary>
public sealed class PushService(
    DeskDbContext db, ITenantContext tenant, ISecretStore secrets, IPushDelivery delivery) : IPushService
{
    public const int MaxDevicesPerPerson = 10;

    public async Task<PushStatusDto> StatusAsync(Guid appUserId, CancellationToken ct = default)
    {
        var (publicKey, _) = await PushKeys.GetOrCreateAsync(db, secrets, Org(), ct);
        var devices = (await db.PushSubscriptions.AsNoTracking()
                .Where(s => s.AppUserId == appUserId)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(ct))
            .Select(Dto).ToList();
        return new PushStatusDto(publicKey, devices, await PreferencesAsync(appUserId, ct));
    }

    public async Task<PushDeviceDto> SubscribeAsync(Guid appUserId, PushSubscribeInput input, CancellationToken ct = default)
    {
        var endpoint = input.Endpoint?.Trim() ?? "";
        if (!PushEndpoints.IsAllowed(endpoint))
            throw new ValidationFailedException("This browser's notification service is not one the portal sends to.");
        if (!KeyLooksRight(input.P256dh, 65) || !KeyLooksRight(input.Auth, 16))
            throw new ValidationFailedException("The browser sent incomplete notification keys. Try turning notifications on again.");
        var label = string.IsNullOrWhiteSpace(input.DeviceLabel) ? null : input.DeviceLabel.Trim()[..Math.Min(80, input.DeviceLabel.Trim().Length)];

        // One browser, one row. Signing in as someone else on the same browser moves it to them: the
        // device belongs to whoever turned notifications on last, never to two people at once.
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (row is null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.AppUserId == appUserId, ct) >= MaxDevicesPerPerson)
                throw new ValidationFailedException($"You already have {MaxDevicesPerPerson} devices signed up. Remove one first.");
            row = new PushSubscription { AppUserId = appUserId, Endpoint = endpoint, P256dh = input.P256dh, Auth = input.Auth };
            db.PushSubscriptions.Add(row);
        }
        row.AppUserId = appUserId;
        row.P256dh = input.P256dh;
        row.Auth = input.Auth;
        row.DeviceLabel = label;
        await db.SaveChangesAsync(ct);
        return Dto(row);
    }

    private static PushDeviceDto Dto(PushSubscription s) => new(s.Id, s.DeviceLabel, s.CreatedAt, s.LastDeliveredAt,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s.Endpoint)))[..16].ToLowerInvariant());

    public async Task RemoveDeviceAsync(Guid appUserId, Guid deviceId, CancellationToken ct = default)
    {
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == deviceId && s.AppUserId == appUserId, ct)
            ?? throw new NotFoundException("Device");
        db.PushSubscriptions.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PushPreferencesDto> SavePreferencesAsync(Guid appUserId, PushPreferencesDto preferences, CancellationToken ct = default)
    {
        var row = await db.PushPreferences.FirstOrDefaultAsync(p => p.AppUserId == appUserId, ct);
        if (row is null)
        {
            row = new PushPreference { AppUserId = appUserId };
            db.PushPreferences.Add(row);
        }
        row.Assigned = preferences.Assigned;
        row.ClientReplied = preferences.ClientReplied;
        row.SlaAtRisk = preferences.SlaAtRisk;
        await db.SaveChangesAsync(ct);
        return preferences;
    }

    public async Task<int> SendTestAsync(Guid appUserId, CancellationToken ct = default)
    {
        if (!await db.PushSubscriptions.AnyAsync(s => s.AppUserId == appUserId, ct))
            throw new ValidationFailedException("Turn notifications on for this device first.");
        var note = new PushNotification
        {
            AppUserId = appUserId, Kind = PushKind.Test, Title = "Notifications are working",
            Body = "This device will be told when a ticket is assigned to you, a client replies, or one is about to breach its SLA.",
            Url = "/dashboard/profile",
        };
        db.PushNotifications.Add(note);
        await db.SaveChangesAsync(ct);
        await delivery.DeliverPendingAsync(ct);
        return (await db.PushNotifications.AsNoTracking().FirstAsync(n => n.Id == note.Id, ct)).Delivered;
    }

    private async Task<PushPreferencesDto> PreferencesAsync(Guid appUserId, CancellationToken ct)
    {
        var row = await db.PushPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.AppUserId == appUserId, ct);
        return row is null ? new PushPreferencesDto(true, true, true) : new PushPreferencesDto(row.Assigned, row.ClientReplied, row.SlaAtRisk);
    }

    private Guid Org() => tenant.OrganizationId ?? throw new ForbiddenException("No organization in scope.");

    private static bool KeyLooksRight(string? value, int bytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return WebPushCrypto.FromBase64Url(value).Length == bytes; }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// The three events, found by comparing each open ticket with what was seen last time: a different
/// person working it, a newer client reply, a due time now inside two hours. Comparing state rather
/// than hooking every code path means an assignment made in Autotask, on a board, by a topic or by a
/// recurring schedule is caught the same way as one made in the portal.
///
/// A ticket seen for the first time is only remembered, never announced - otherwise the day this is
/// switched on, every technician's phone would receive the whole backlog.
/// </summary>
public sealed class PushScanner(DeskDbContext db, TimeProvider clock) : IPushScanner
{
    public static readonly TimeSpan AtRiskWindow = TimeSpan.FromHours(TicketStatusRules.AtRiskHours);

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var subscribed = (await db.PushSubscriptions.AsNoTracking().Select(s => s.AppUserId).Distinct().ToListAsync(ct)).ToHashSet();
        if (subscribed.Count == 0) return 0;
        var now = clock.GetUtcNow();

        var identities = (await db.UserPsaIdentities.AsNoTracking()
                .Select(i => new { i.PsaConnectionId, i.ExternalTechnicianId, i.AppUserId }).ToListAsync(ct))
            .GroupBy(i => (i.PsaConnectionId, i.ExternalTechnicianId)).ToDictionary(g => g.Key, g => g.First().AppUserId);
        var preferences = await db.PushPreferences.AsNoTracking().ToDictionaryAsync(p => p.AppUserId, ct);

        var tickets = await db.Tickets.AsNoTracking()
            .Where(TicketStatusRules.Open())
            .Select(t => new
            {
                t.Id, t.Number, t.ExternalTicketId, t.Title, t.PortalStatus, t.SlaDueAt, t.SlaPausedAt,
                t.AssignedAppUserId, t.PsaConnectionId, t.AssignedTechnicianExternalId,
                LastClientNoteAt = t.Notes.Where(n => n.AuthoredByClient && n.IsPublic).Max(n => (DateTimeOffset?)n.NoteCreatedAt),
            })
            .ToListAsync(ct);
        var ids = tickets.Select(t => t.Id).ToList();
        var states = await db.PushTicketStates.Where(s => ids.Contains(s.TicketId)).ToDictionaryAsync(s => s.TicketId, ct);

        var queued = 0;
        void Queue(Guid? holder, PushKind kind, Guid ticketId, string title, string body)
        {
            if (holder is not { } user || !subscribed.Contains(user)) return;
            if (preferences.TryGetValue(user, out var pref) && !pref.Wants(kind)) return;
            db.PushNotifications.Add(new PushNotification
            {
                AppUserId = user, Kind = kind, TicketId = ticketId, Title = title, Body = body, Url = $"/dashboard/tickets/{ticketId}",
            });
            queued++;
        }

        foreach (var t in tickets)
        {
            var holder = t.AssignedAppUserId
                         ?? (t.PsaConnectionId is { } conn && t.AssignedTechnicianExternalId is { Length: > 0 } ext
                             && identities.TryGetValue((conn, ext), out var mapped) ? mapped : null);
            var reference = t.Number ?? t.ExternalTicketId ?? "A ticket";
            var dueSoon = t.SlaDueAt is { } due && t.SlaPausedAt is null && !SlaClock.IsWaiting(t.PortalStatus)
                          && due > now && due <= now + AtRiskWindow;

            if (!states.TryGetValue(t.Id, out var state))
            {
                db.PushTicketStates.Add(new PushTicketState
                {
                    TicketId = t.Id, HolderAppUserId = holder, LastClientNoteAt = t.LastClientNoteAt,
                    SlaWarnedDueAt = dueSoon ? t.SlaDueAt : null,
                });
                continue;
            }

            if (holder != state.HolderAppUserId)
            {
                Queue(holder, PushKind.Assigned, t.Id, $"{reference} assigned to you", t.Title);
                state.HolderAppUserId = holder;
            }

            if (t.LastClientNoteAt is { } replied && (state.LastClientNoteAt is null || replied > state.LastClientNoteAt))
            {
                var note = await db.TicketNotes.AsNoTracking()
                    .Where(n => n.TicketId == t.Id && n.AuthoredByClient && n.IsPublic && n.NoteCreatedAt == replied)
                    .Select(n => new { n.AuthorName, n.Body }).FirstOrDefaultAsync(ct);
                Queue(holder, PushKind.ClientReplied, t.Id,
                    $"{note?.AuthorName ?? "The client"} replied on {reference}", Snippet(note?.Body) ?? t.Title);
                state.LastClientNoteAt = replied;
            }

            if (dueSoon && state.SlaWarnedDueAt != t.SlaDueAt)
            {
                var minutes = (int)Math.Max(1, (t.SlaDueAt!.Value - now).TotalMinutes);
                Queue(holder, PushKind.SlaAtRisk, t.Id,
                    $"{reference} is due in {(minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes} minutes")}", t.Title);
                state.SlaWarnedDueAt = t.SlaDueAt;
            }
        }

        await db.SaveChangesAsync(ct);
        return queued;
    }

    /// <summary>The first line of a reply, short enough for a lock screen. Nothing more of the thread leaves.</summary>
    private static string? Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= 120 ? line : line[..117].TrimEnd() + "…";
    }
}

/// <summary>Sends queued notifications to each of the person's devices, encrypted to that device.</summary>
public sealed class PushDelivery(
    DeskDbContext db, ITenantContext tenant, ISecretStore secrets, IHttpClientFactory http, TimeProvider clock,
    ILogger<PushDelivery> logger) : IPushDelivery
{
    public const string HttpClientName = "webpush";

    public async Task<int> DeliverPendingAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // An hour old is stale: "due in 2 hours" arriving late is worse than not arriving.
        var cutoff = now.AddHours(-1);
        var pending = await db.PushNotifications
            .Where(n => n.SentAt == null && n.Attempts < PushNotification.MaxAttempts && n.CreatedAt >= cutoff)
            .OrderBy(n => n.CreatedAt).Take(200)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var org = tenant.OrganizationId ?? throw new ForbiddenException("No organization in scope.");
        var (publicKey, privateKey) = await PushKeys.GetOrCreateAsync(db, secrets, org, ct);
        using var signing = WebPushCrypto.VapidKey(publicKey, privateKey);
        var subject = Environment.GetEnvironmentVariable("PORTAL_PUBLIC_URL")?.TrimEnd('/') is { Length: > 0 } u ? u : "https://piomanage.com";
        var client = http.CreateClient(HttpClientName);
        var users = pending.Select(n => n.AppUserId).Distinct().ToList();
        var devices = await db.PushSubscriptions.Where(s => users.Contains(s.AppUserId)).ToListAsync(ct);

        var sent = 0;
        foreach (var note in pending)
        {
            note.Attempts++;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { title = note.Title, body = note.Body, url = note.Url, tag = $"{note.Kind}-{note.TicketId}" });
            string? error = null;
            foreach (var device in devices.Where(d => d.AppUserId == note.AppUserId).ToList())
            {
                var outcome = await SendAsync(client, device, payload, signing, subject, now, ct);
                if (outcome == Outcome.Delivered)
                {
                    note.Delivered++;
                    device.LastDeliveredAt = now;
                }
                else if (outcome == Outcome.Gone)
                {
                    // The browser unsubscribed, or the app was uninstalled: forget the device.
                    db.PushSubscriptions.Remove(device);
                    devices.Remove(device);
                }
                else
                {
                    error = "A push service did not accept the message.";
                }
            }
            if (note.Delivered > 0 || error is null || note.Attempts >= PushNotification.MaxAttempts)
            {
                note.SentAt = now;
                sent++;
            }
            note.LastError = note.Delivered == 0 ? error ?? "No devices to send to." : null;
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private enum Outcome { Delivered, Gone, Failed }

    private async Task<Outcome> SendAsync(HttpClient client, PushSubscription device, byte[] payload,
        System.Security.Cryptography.ECDsa signing, string subject, DateTimeOffset now, CancellationToken ct)
    {
        if (!PushEndpoints.IsAllowed(device.Endpoint)) return Outcome.Gone;
        try
        {
            var body = WebPushCrypto.Encrypt(WebPushCrypto.FromBase64Url(device.P256dh), WebPushCrypto.FromBase64Url(device.Auth), payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, device.Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", "3600");
            request.Headers.TryAddWithoutValidation("Urgency", "high");
            request.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(device.Endpoint, signing, subject, now));
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return Outcome.Delivered;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return Outcome.Gone;
            logger.LogWarning("Push service answered {Status} for a device", (int)response.StatusCode);
            return Outcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or FormatException
                                   or Desk.PsaCore.Contracts.ConnectorException)
        {
            logger.LogWarning(ex, "Could not deliver a push notification");
            return Outcome.Failed;
        }
    }
}

/// <summary>Every organization with a device signed up: scan, then deliver, each in its own scope.</summary>
public sealed class PushRunner(IServiceScopeFactory scopes, ILogger<PushRunner> logger) : IPushRunner
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        List<Guid> orgs;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            orgs = await scope.ServiceProvider.GetRequiredService<DeskDbContext>().PushSubscriptions.AsNoTracking()
                .Select(s => s.MspOrganizationId).Distinct().ToListAsync(ct);
        }

        foreach (var org in orgs)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
            try
            {
                var queued = await scope.ServiceProvider.GetRequiredService<IPushScanner>().ScanAsync(ct);
                var sent = await scope.ServiceProvider.GetRequiredService<IPushDelivery>().DeliverPendingAsync(ct);
                if (queued > 0 || sent > 0) logger.LogInformation("Push: {Queued} queued, {Sent} sent", queued, sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Push pass failed for an organization");
            }
        }
    }
}
