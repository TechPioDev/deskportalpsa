namespace Desk.Application.Notifications;

public sealed record PushDeviceDto(
    Guid Id, string? Label, DateTimeOffset AddedAt, DateTimeOffset? LastDeliveredAt,
    /// <summary>The first 16 hex characters of SHA-256 over the endpoint: enough for a browser to say
    /// "this one is me", without the endpoint itself - a push address - going back out.</summary>
    string EndpointHash);

public sealed record PushPreferencesDto(bool Assigned, bool ClientReplied, bool SlaAtRisk);

/// <summary>Everything the Profile page's notification card shows.</summary>
public sealed record PushStatusDto(
    /// <summary>The organization's Web Push public key, which the browser subscribes against.</summary>
    string PublicKey,
    IReadOnlyList<PushDeviceDto> Devices,
    PushPreferencesDto Preferences);

public sealed record PushSubscribeInput(string Endpoint, string P256dh, string Auth, string? DeviceLabel);

public interface IPushService
{
    Task<PushStatusDto> StatusAsync(Guid appUserId, CancellationToken ct = default);
    Task<PushDeviceDto> SubscribeAsync(Guid appUserId, PushSubscribeInput input, CancellationToken ct = default);
    Task RemoveDeviceAsync(Guid appUserId, Guid deviceId, CancellationToken ct = default);
    Task<PushPreferencesDto> SavePreferencesAsync(Guid appUserId, PushPreferencesDto preferences, CancellationToken ct = default);

    /// <summary>Sends a test notification to every device of the person now; returns how many took it.</summary>
    Task<int> SendTestAsync(Guid appUserId, CancellationToken ct = default);
}

/// <summary>Delivers notifications not yet sent, for the organization in scope.</summary>
public interface IPushDelivery
{
    Task<int> DeliverPendingAsync(CancellationToken ct = default);
}

/// <summary>Looks at the organization's open tickets for the three events, and queues what is new.</summary>
public interface IPushScanner
{
    Task<int> ScanAsync(CancellationToken ct = default);
}

/// <summary>Scan and deliver for every organization with at least one device signed up.</summary>
public interface IPushRunner
{
    Task RunAsync(CancellationToken ct = default);
}
