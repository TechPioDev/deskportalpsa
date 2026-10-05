using System.Security.Cryptography;
using System.Text;
using Desk.PsaCore.Models;

namespace Desk.PsaCore.Contracts;

/// <summary>
/// The portal's own signed-delivery check for an inbound webhook: a shared secret, a timestamp, and
/// an HMAC over both the timestamp and the body. One implementation, used by every connector that
/// accepts this format - it was copied into each of them, and each copy had the same two gaps.
///
/// <list type="bullet">
/// <item>No secret means no delivery is valid. With none stored the key was the empty string, so the
/// signature of any body could be computed by anyone who knew the connection's address.</item>
/// <item>The timestamp is part of what is signed. Checked but unsigned, it could be replaced with
/// the current time on a captured delivery, which made the freshness check decorative.</item>
/// </list>
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "X-Timestamp";

    public static WebhookValidationResult Validate(WebhookRequest request, string? secret, TimeSpan maxSkew)
    {
        if (string.IsNullOrEmpty(secret))
            return new WebhookValidationResult(false, "No webhook secret is set for this connection.");

        if (!request.Headers.TryGetValue(TimestampHeader, out var timestamp)
            || !DateTimeOffset.TryParse(timestamp, out var sentAt))
            return new WebhookValidationResult(false, "Missing or invalid timestamp.");
        if (Math.Abs((request.ReceivedAt - sentAt).TotalSeconds) > maxSkew.TotalSeconds)
            return new WebhookValidationResult(false, "Timestamp outside allowed skew.");

        var expected = Compute(timestamp, request.Body, secret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.RawSignature ?? ""))
            ? new WebhookValidationResult(true, null)
            : new WebhookValidationResult(false, "Signature mismatch.");
    }

    /// <summary>Lower-case hex HMAC-SHA256 of <c>"{timestamp}.{body}"</c>, the timestamp exactly as sent in the header.</summary>
    public static string Compute(string timestamp, string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));
}
