using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Whether outbound email works, and a way to prove it. Configuration itself is deliberately NOT
/// editable here: the mail server and its password live in the server's environment, so nobody with
/// a portal login can read them or point the portal's mail somewhere else.
/// </summary>
[ApiController]
[Route("api/admin/email")]
public sealed class AdminEmailController(IEmailSender email, ICurrentUser user, IAuditWriter audit) : ControllerBase
{
    public sealed record EmailStatusDto(bool Configured, string? From);
    public sealed record TestEmailInput(string? To);
    public sealed record TestEmailResult(bool Sent, string Message);

    [HttpGet]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public IActionResult Status() => Ok(new EmailStatusDto(email.IsConfigured, email.FromAddress));

    [HttpPost("test")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> Test([FromBody] TestEmailInput? input, CancellationToken ct)
    {
        if (!email.IsConfigured)
            return Ok(new TestEmailResult(false, "Email is not configured on the server yet."));

        var (valid, _) = EmailAddresses.Parse(string.IsNullOrWhiteSpace(input?.To) ? user.Email : input.To);
        if (valid.Count != 1)
            return Ok(new TestEmailResult(false, "Enter one valid email address to send the test to."));

        try
        {
            await email.SendAsync(new EmailMessage(valid,
                "Desk Portal test email",
                "This is a test from Desk Portal. If you can read it, scheduled reports will be delivered by email.\n"), ct);
            await audit.WriteAsync("email.test.sent", "Email", null, new { To = valid[0] }, ct);
            return Ok(new TestEmailResult(true, $"Test email sent to {valid[0]}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await audit.WriteAsync("email.test.failed", "Email", null, new { To = valid[0], Error = ex.GetType().Name }, ct);
            return Ok(new TestEmailResult(false, $"The mail server refused the message: {ex.Message}"));
        }
    }
}
