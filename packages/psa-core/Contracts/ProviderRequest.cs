using System.Net.Http.Headers;

namespace Desk.PsaCore.Contracts;

/// <summary>
/// What a connector tells the HTTP layer beneath it about a request, and how both read the one
/// header a provider uses to tell us to wait.
/// </summary>
public static class ProviderRequest
{
    /// <summary>
    /// Set on a request that reads and changes nothing, although its method does not say so.
    /// Autotask's queries are POSTs. The layer below repeats a GET after a timeout or a server
    /// error without asking; it repeats a POST only when this says it is safe, because repeating a
    /// create makes a second ticket, note or time entry.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> SafeToRepeat = new("desk.provider.safe-to-repeat");

    /// <summary>Marks <paramref name="request"/> as a read; see <see cref="SafeToRepeat"/>.</summary>
    public static HttpRequestMessage AsRead(this HttpRequestMessage request)
    {
        request.Options.Set(SafeToRepeat, true);
        return request;
    }

    /// <summary>
    /// How long the provider asked us to wait. The header comes in two forms - a number of seconds,
    /// or the time to come back at - and only the first used to be read: a provider that sent a
    /// date was retried after a guessed ten seconds, sooner than it had asked.
    /// </summary>
    public static TimeSpan? Wait(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter.Date is { } at) return at > now ? at - now : TimeSpan.Zero;
        return null;
    }
}
