using Desk.Domain.Common;

namespace Desk.Domain.Notifications;

/// <summary>What a push notification is about. Each is one a technician can switch off for themselves.</summary>
public enum PushKind
{
    /// <summary>A ticket now has them as the person working it.</summary>
    Assigned = 0,

    /// <summary>The client replied on a ticket they are working.</summary>
    ClientReplied = 1,

    /// <summary>A ticket they are working is due within two hours.</summary>
    SlaAtRisk = 2,

    /// <summary>Sent from their Profile page to check a device works; never switched off.</summary>
    Test = 3,
}

/// <summary>
/// One device a staff member turned notifications on for: the browser's push endpoint and the keys it
/// gave, which is everything needed to send it a message and nothing that identifies the device.
/// </summary>
public class PushSubscription : TenantEntity
{
    public Guid AppUserId { get; set; }

    /// <summary>The push service URL the browser handed out. Unique: one browser, one row.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's P-256 public key, base64url - what the message is encrypted to.</summary>
    public required string P256dh { get; set; }

    /// <summary>The browser's 16-byte authentication secret, base64url.</summary>
    public required string Auth { get; set; }

    /// <summary>What the person will recognise it by: "Chrome on Android", "Safari on iPhone".</summary>
    public string? DeviceLabel { get; set; }

    public DateTimeOffset? LastDeliveredAt { get; set; }
}

/// <summary>Which events one staff member wants pushed. No row means everything, the default.</summary>
public class PushPreference : TenantEntity
{
    public Guid AppUserId { get; set; }
    public bool Assigned { get; set; } = true;
    public bool ClientReplied { get; set; } = true;
    public bool SlaAtRisk { get; set; } = true;

    public bool Wants(PushKind kind) => kind switch
    {
        PushKind.Assigned => Assigned,
        PushKind.ClientReplied => ClientReplied,
        PushKind.SlaAtRisk => SlaAtRisk,
        _ => true,
    };
}

/// <summary>
/// What the notification scan last saw of one ticket: who was working it, the latest client reply, and
/// the due time it already warned about. A change against this is an event; the same state seen again
/// is not, which is what keeps a scan every minute from repeating itself.
/// </summary>
public class PushTicketState : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid? HolderAppUserId { get; set; }
    public DateTimeOffset? LastClientNoteAt { get; set; }
    public DateTimeOffset? SlaWarnedDueAt { get; set; }
}

/// <summary>A notification sent, or still to send: the record behind "why did my phone buzz".</summary>
public class PushNotification : TenantEntity
{
    public Guid AppUserId { get; set; }
    public PushKind Kind { get; set; }
    public Guid? TicketId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>Where tapping it goes, relative to the portal: /dashboard/tickets/{id}.</summary>
    public required string Url { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>How many of the person's devices accepted it.</summary>
    public int Delivered { get; set; }

    public const int MaxAttempts = 3;
}
