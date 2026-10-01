using Desk.Api.Middleware;
using Desk.Application.Common;

namespace Desk.Api.Auth;

/// <summary>The id <see cref="CorrelationIdMiddleware"/> gave the request being served.</summary>
public sealed class HttpCorrelationContext(IHttpContextAccessor accessor) : ICorrelationContext
{
    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationIdMiddleware.HeaderName] as string;
}
