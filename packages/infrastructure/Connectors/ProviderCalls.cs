using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using Desk.PsaCore.Contracts;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Connectors;

/// <summary>How one connection's calls to its PSA are paced, timed and repeated.</summary>
/// <param name="RequestsPerMinute">The sustained rate. Zero or less switches pacing off.</param>
public sealed record ProviderCallPolicy(Guid ConnectionId, string Provider, int RequestsPerMinute)
{
    /// <summary>How long one attempt may take. A PSA that does not answer used to hold the caller for 100 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Attempts in all, the first included.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// The longest this layer will sit and wait before trying again. A provider that asks for
    /// longer is not waited for here: the answer goes back as it is, and the sync - which can
    /// afford to - comes back to that record when the time has passed.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// One budget of requests per connection, so that a connection in the middle of a large import
/// cannot spend another connection's allowance, and so that a sync paces itself instead of running
/// into the provider's limit and being told to stop.
///
/// A token bucket: a burst the size of two minutes' allowance is free, which is every ordinary
/// sync; beyond it requests are let through at the sustained rate. Nothing limited calls before -
/// the per-connection setting that looked as though it did was read by nothing.
/// </summary>
public sealed class ConnectionThrottle(TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    private sealed class Bucket
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public double Tokens;
        public DateTimeOffset At;
    }

    public async Task WaitAsync(Guid connectionId, int requestsPerMinute, CancellationToken ct)
    {
        if (requestsPerMinute <= 0) return;
        var perSecond = requestsPerMinute / 60d;
        var burst = requestsPerMinute * 2d;
        var bucket = _buckets.GetOrAdd(connectionId, _ => new Bucket { Tokens = burst, At = clock.GetUtcNow() });

        // Held while waiting, so callers on one connection are let through in the order they came.
        // It is one connection's gate: no other connection ever waits behind it.
        await bucket.Gate.WaitAsync(ct);
        try
        {
            Refill(bucket, perSecond, burst);
            if (bucket.Tokens < 1)
            {
                await _delay(TimeSpan.FromSeconds((1 - bucket.Tokens) / perSecond), ct);
                Refill(bucket, perSecond, burst);
                // The wait was for exactly one token. A clock that did not move (a test's) must not
                // leave the bucket in debt.
                bucket.Tokens = Math.Max(bucket.Tokens, 1);
            }
            bucket.Tokens -= 1;
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    private void Refill(Bucket bucket, double perSecond, double burst)
    {
        var now = clock.GetUtcNow();
        var elapsed = (now - bucket.At).TotalSeconds;
        if (elapsed > 0) bucket.Tokens = Math.Min(burst, bucket.Tokens + elapsed * perSecond);
        bucket.At = now;
    }
}

/// <summary>
/// Sits under a connector's HttpClient and does what neither connector did for itself: paces the
/// connection, bounds each attempt, and tries again where trying again is safe.
///
/// <list type="bullet">
/// <item><b>429</b> is repeated whatever the request was: the provider did not process it. It waits
/// as long as the provider asked, if that is short; if not, the 429 goes back to the caller with
/// its Retry-After intact.</item>
/// <item><b>A timeout, a connection failure or a 5xx</b> is repeated only for a request that reads.
/// A write may have been carried out before the answer was lost, and Autotask answers a rejected
/// write with a 500: repeating either would create the record twice.</item>
/// <item>Everything else is the caller's to interpret, once.</item>
/// </list>
/// Nothing about the request beyond its method and path is logged: no header, no query, no body.
/// </summary>
public sealed class ProviderCallHandler(
    ProviderCallPolicy policy,
    ConnectionThrottle throttle,
    TimeProvider clock,
    ILogger? logger = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null) : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var reads = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
            || (request.Options.TryGetValue(ProviderRequest.SafeToRepeat, out var marked) && marked);

        for (var attempt = 1; ; attempt++)
        {
            await throttle.WaitAsync(policy.ConnectionId, policy.RequestsPerMinute, ct);

            HttpResponseMessage? response = null;
            ExceptionDispatchInfo? failure = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(policy.Timeout);
                try
                {
                    response = await base.SendAsync(request, timeout.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // This attempt ran out of time; the caller did not give up.
                    failure = ExceptionDispatchInfo.Capture(new TaskCanceledException(
                        $"The PSA did not answer within {policy.Timeout.TotalSeconds:0} seconds.", ex));
                }
                catch (HttpRequestException ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            }

            var wait = attempt < policy.MaxAttempts ? WaitBeforeRetry(response, failure is not null, reads, attempt) : null;
            if (wait is null)
            {
                if (failure is not null)
                {
                    Log(LogLevel.Warning, request, attempt, null, "failed");
                    failure.Throw();
                }
                if ((int)response!.StatusCode >= 400)
                    Log(response.StatusCode == HttpStatusCode.NotFound ? LogLevel.Debug : LogLevel.Warning, request, attempt, response.StatusCode, "answered");
                return response;
            }

            Log(LogLevel.Warning, request, attempt, response?.StatusCode, $"retrying in {wait.Value.TotalMilliseconds:0} ms");
            response?.Dispose();
            await _delay(wait.Value, ct);
        }
    }

    /// <summary>How long to wait before another attempt, or null when there should not be one.</summary>
    private TimeSpan? WaitBeforeRetry(HttpResponseMessage? response, bool failed, bool reads, int attempt)
    {
        if (response?.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var asked = ProviderRequest.Wait(response.Headers.RetryAfter, clock.GetUtcNow());
            var wait = asked ?? Backoff(attempt);
            return wait <= policy.MaxWait ? wait : null;
        }

        var passing = failed || response is { StatusCode: HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout };
        return passing && reads ? Backoff(attempt) : null;
    }

    /// <summary>Half a second, doubling, give or take a fifth so that callers do not return in step.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var ms = policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        ms *= 0.8 + 0.4 * _jitter();
        return TimeSpan.FromMilliseconds(Math.Min(ms, policy.MaxWait.TotalMilliseconds));
    }

    private void Log(LogLevel level, HttpRequestMessage request, int attempt, HttpStatusCode? status, string outcome)
    {
        if (logger is null || !logger.IsEnabled(level)) return;
        // Method and path only. A query string can carry a filter with a customer's name in it, and
        // every credential a connector has travels as a header.
        logger.Log(level, "{Provider} call for connection {ConnectionId}: {Method} {Path} attempt {Attempt} {Outcome} ({Status})",
            policy.Provider, policy.ConnectionId, request.Method.Method, request.RequestUri?.AbsolutePath, attempt, outcome,
            status is { } s ? (int)s : null);
    }
}

/// <summary>
/// Hands a connector factory the HttpClient for one connection: the provider's named pipeline (the
/// egress guard and its pinned transport) underneath, and this connection's own pacing, timeout and
/// retry on top. The pipeline is shared and pooled; the budget is the connection's alone.
/// </summary>
public sealed class ProviderHttpClients(
    IHttpMessageHandlerFactory handlers, ConnectionThrottle throttle, TimeProvider clock, ILoggerFactory loggers)
{
    public HttpClient For(string name, Guid connectionId, string baseUrl, int requestsPerMinute)
    {
        var handler = new ProviderCallHandler(
            new ProviderCallPolicy(connectionId, name, requestsPerMinute), throttle, clock, loggers.CreateLogger<ProviderCallHandler>())
        {
            InnerHandler = handlers.CreateHandler(name),
        };
        // The pooled pipeline is not this client's to dispose. And no overall timeout: each attempt
        // is bounded by the handler, and a client-wide limit would cut a retry short.
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
