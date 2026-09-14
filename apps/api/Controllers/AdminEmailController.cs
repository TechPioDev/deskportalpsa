using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Outbound email: whether it works, the organization's own mail account, and a way to prove it.
/// The password is write-only - accepted on save, stored encrypted, never returned by any endpoint.
/// </summary>
[ApiController]
[Route("api/admin/email")]
public sealed class AdminEmailController(
    IEmailSender email, IEmailSettingsService settings, ICurrentUser user, ITenantContext tenant, IAuditWriter audit) : ControllerBase
{
    public sealed record TestEmailInput(string? To);
    public sealed record TestEmailResult(bool Sent, string Message);

    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    [HttpGet]
    [RequirePermission(Permissions.IntegrationHealthView, Permissions.OrgManage)]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var s = await email.StatusAsync(Org, ct);
        return Ok(new { configured = s.Configured, from = s.FromAddress, source = s.Source });
    }

    [HttpGet("settings")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await settings.GetAsync(ct));

    [HttpPut("settings")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> SaveSettings([FromBody] EmailSettingsInput input, CancellationToken ct) => Ok(await settings.SaveAsync(input, ct));

    [HttpDelete("settings")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> RemoveSettings(CancellationToken ct) => Ok(await settings.RemoveAsync(ct));

    [HttpPost("test")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> Test([FromBody] TestEmailInput? input, CancellationToken ct)
    {
        if (!(await email.StatusAsync(Org, ct)).Configured)
            return Ok(new TestEmailResult(false, "Email is not set up yet — add the mail account first."));

        var (valid, _) = EmailAddresses.Parse(string.IsNullOrWhiteSpace(input?.To) ? user.Email : input.To);
        if (valid.Count != 1)
            return Ok(new TestEmailResult(false, "Enter one valid email address to send the test to."));

        try
        {
            await email.SendAsync(Org, new EmailMessage(valid,
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
