namespace Desk.PsaCore.Models;

public record ConnectionTestResult(bool Success, string? Message, TimeSpan Latency);

public record CreateTicketResult(bool Success, string? ExternalId, string? Error);

public record UpdateTicketResult(bool Success, string? Error)
{
    /// <summary>
    /// The status the provider now holds, in the provider's own words, where the connector had to
    /// work it out (ConnectWise: the name on the ticket's own board). Null where what was asked for
    /// is what was set.
    /// </summary>
    public string? AppliedStatus { get; init; }
}

public record CreateNoteResult(bool Success, string? ExternalId, string? Error);

public record CreateTimeEntryResult(bool Success, string? ExternalId, string? Error);

public record UpdateTimeEntryResult(bool Success, string? Error);

public record CreateAttachmentResult(bool Success, string? ExternalId, string? Error);

/// <summary>Raw inbound webhook request handed to a connector for validation.</summary>
public record WebhookRequest(
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string? RawSignature,
    DateTimeOffset ReceivedAt);

/// <summary>Outcome of signature + timestamp validation of a webhook.</summary>
public record WebhookValidationResult(bool IsValid, string? Reason);

/// <summary>A provider webhook payload normalized into a portal sync event.</summary>
public record NormalizedProviderEvent(
    string EventType,
    string? ExternalTicketId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);
