namespace Desk.Application.Admin;

/// <summary>
/// One thing an administrator should look at. <paramref name="Kind"/> is a stable machine key
/// ("connection-failed", "closed-without-date", …) so the portal can route and tests can assert;
/// <paramref name="Severity"/> is "critical" (something is not flowing) or "warning" (something is
/// degraded or will be wrong in a report). <paramref name="Link"/> is the portal page that fixes it.
/// </summary>
public sealed record AttentionItem(
    string Kind,
    string Severity,
    string Title,
    string Detail,
    int Count,
    string? Link);

public sealed record AttentionDigestDto(string? Recipients, DateOnly? LastSentOn);

public sealed record AttentionDto(
    IReadOnlyList<AttentionItem> Items,
    AttentionDigestDto Digest,
    DateTimeOffset CheckedAt);

public sealed record AttentionDigestSendResult(bool Sent, string Message);

/// <summary>
/// The "needs attention" list for the current organization: the conditions that make a ticket,
/// a reply or a report silently wrong, gathered in one place instead of scattered across pages.
/// Every rule is a database question; nothing here calls a provider.
/// </summary>
public interface IAttentionService
{
    Task<AttentionDto> ListAsync(CancellationToken ct = default);

    /// <summary>Sets who receives the daily digest; null or blank switches it off. Returns the addresses that did not parse.</summary>
    Task<(AttentionDigestDto Digest, IReadOnlyList<string> Invalid)> SetDigestRecipientsAsync(string? recipients, CancellationToken ct = default);

    /// <summary>Emails the current list to the digest recipients now, even when it is empty, as proof the digest works.</summary>
    Task<AttentionDigestSendResult> SendDigestNowAsync(CancellationToken ct = default);
}

/// <summary>Worker entry point: sends each organization's digest once a day, only when it has something to say.</summary>
public interface IAttentionDigestRunner
{
    Task<int> RunDueAsync(CancellationToken ct = default);
}
