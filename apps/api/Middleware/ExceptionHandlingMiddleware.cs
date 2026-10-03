using System.Text.Json;
using Desk.Application.Common;

namespace Desk.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into RFC-7807 problem+json responses. Known
/// <see cref="DeskException"/>s map to their declared status/code; everything else becomes a
/// 500 with no internal detail leaked to the client (details go to the logs only).
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ConflictException ex)
        {
            // What is in the way goes with the refusal, so a screen can show each conflict and offer
            // an override where one is allowed.
            await WriteProblem(context, ex.StatusCode, ex.ErrorCode, ex.Message, ex.Payload);
        }
        catch (DeskException ex)
        {
            await WriteProblem(context, ex.StatusCode, ex.ErrorCode, ex.Message);
        }
        catch (Desk.PsaCore.Contracts.ConnectorException ex)
        {
            // A provider rejected or failed the call. The message is provider-facing and safe
            // (no credentials); surfacing it lets admins fix mappings/permissions without log-diving.
            await WriteProblem(context, 502, "psa_error", ex.Message);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            await WriteProblem(context, 500, "internal_error",
                "An unexpected error occurred. Reference the correlation id when contacting support.");
        }
    }

    // The same casing the API's own responses use, so a payload reads like any other body.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task WriteProblem(HttpContext context, int status, string code, string detail, object? payload = null)
    {
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = $"https://desk.portal/errors/{code}",
            title = code,
            status,
            detail,
            correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString(),
            payload,
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }
}
