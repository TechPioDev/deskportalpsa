namespace Desk.PsaCore.Contracts;

/// <summary>Classifies why a connector call failed, so the resilience layer can react correctly.</summary>
public enum ConnectorFailureKind
{
    /// <summary>Credentials rejected — do not retry; surface for remediation.</summary>
    Authentication,
    /// <summary>Caller lacks rights in the PSA — do not retry.</summary>
    PermissionDenied,
    /// <summary>Provider rate limit hit — retry after <see cref="ConnectorException.RetryAfter"/>.</summary>
    RateLimited,
    /// <summary>Network/timeout — transient, safe to retry.</summary>
    Timeout,
    /// <summary>Referenced entity does not exist — do not retry.</summary>
    NotFound,
    /// <summary>Malformed request the provider rejected — do not retry.</summary>
    InvalidRequest,
    /// <summary>Provider-side 5xx — transient, safe to retry.</summary>
    ProviderError,
}

/// <summary>Uniform error surface every connector throws, regardless of the underlying PSA SDK.</summary>
public sealed class ConnectorException(ConnectorFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectorFailureKind Kind { get; } = kind;

    /// <summary>Hint from the provider for when to retry (rate limiting).</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Whether the resilience layer should retry this failure.</summary>
    public bool IsTransient => Kind is ConnectorFailureKind.Timeout
        or ConnectorFailureKind.ProviderError
        or ConnectorFailureKind.RateLimited;

    /// <summary>
    /// Whether the request certainly did nothing at the provider: it was turned away for its rate
    /// before being looked at, or the provider could not be connected to at all. Then sending it
    /// again cannot do it twice. A request that was sent and whose answer never came is not this:
    /// the provider may have carried it out.
    /// </summary>
    public bool NeverSent => Kind == ConnectorFailureKind.RateLimited
        || InnerException is System.Net.Http.HttpRequestException
        {
            HttpRequestError: System.Net.Http.HttpRequestError.NameResolutionError
                or System.Net.Http.HttpRequestError.ConnectionError
                or System.Net.Http.HttpRequestError.SecureConnectionError
                or System.Net.Http.HttpRequestError.ProxyTunnelError
        };
}
