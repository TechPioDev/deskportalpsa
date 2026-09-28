using System.Text.Json;
using Desk.Application.Boards;
using Desk.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Where a monitoring tool posts its alerts.
///
/// Unauthenticated by design: the request comes from a vendor's cloud with no user and no session
/// behind it. The source's key is the whole of the authentication, and it is read from a header
/// rather than the URL so it does not end up in an access log or a browser history.
///
/// The payload is deliberately forgiving about field names. Every tool lets you write your own JSON,
/// but people copy templates, so the common spellings of each field are all accepted and what
/// cannot be understood is refused with the field that was missing.
/// </summary>
[ApiController]
[Route("api/intake")]
[AllowAnonymous]
public sealed class AlertIntakeController(IAlertIntakeService intake, BoardFeatureOptions features) : ControllerBase
{
    /// <summary>Header the key travels in. Every vendor's webhook form allows a custom header.</summary>
    public const string KeyHeader = "X-Desk-Alert-Key";

    [HttpPost("alerts")]
    public async Task<IActionResult> Receive([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!features.InternalBoards) throw new NotFoundException("Alert source");
        if (!Request.Headers.TryGetValue(KeyHeader, out var key) || string.IsNullOrWhiteSpace(key))
            throw new NotFoundException("Alert source");

        var result = await intake.ReceiveAsync(key.ToString(), Read(body), ct);
        return Ok(result);
    }

    /// <summary>
    /// Reads the documented shape, and the spellings the vendors' own templates use, so a webhook
    /// configured from a tool's example does not have to be rewritten field by field.
    /// </summary>
    public static AlertMessage Read(JsonElement body)
    {
        string? S(params string[] names)
        {
            foreach (var name in names)
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v))
                {
                    var text = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number => v.ToString(),
                        JsonValueKind.True or JsonValueKind.False => v.GetBoolean().ToString(),
                        JsonValueKind.Object => v.TryGetProperty("name", out var inner) ? inner.GetString() : null,
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            return null;
        }

        var status = S("status", "state", "activityResult", "alertStatus");
        var cleared = string.Equals(status, "cleared", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(status, "reset", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(S("cleared", "isCleared", "resolved"), "true", StringComparison.OrdinalIgnoreCase);

        DateTimeOffset? at = DateTimeOffset.TryParse(
            S("occurredAt", "timestamp", "createdAt", "alertTime", "eventTime"), out var parsed) ? parsed : null;

        return new AlertMessage(
            AlertId: S("alertId", "alertUid", "uid", "id", "monitorId", "alertID") ?? "",
            Title: S("title", "subject", "message", "alertMessage", "summary", "description") ?? "",
            Description: S("description", "details", "body", "diagnostics", "message"),
            Severity: S("severity", "priority", "level", "alertSeverity"),
            Device: S("device", "deviceName", "hostname", "computer", "computerName", "deviceHostname"),
            Client: S("client", "clientName", "organization", "organizationName", "site", "siteName", "company"),
            Cleared: cleared,
            OccurredAt: at);
    }
}
