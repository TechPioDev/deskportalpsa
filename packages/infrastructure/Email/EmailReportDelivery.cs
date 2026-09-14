using System.Text;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Email;

/// <summary>
/// Emails a generated report as a CSV attachment. Every outcome is written back as the run's
/// delivery note — the portal always keeps the run for download, so a failed send loses nothing,
/// but the person who scheduled it must be able to see that nobody received it and why.
/// </summary>
public sealed class EmailReportDelivery(IEmailSender email, ILogger<EmailReportDelivery> logger) : IReportDelivery
{
    public async Task<ReportDeliveryResult> DeliverAsync(Guid organizationId, string? recipients, string subject, string fileName, string csv, CancellationToken ct = default)
    {
        var (valid, invalid) = EmailAddresses.Parse(recipients);
        if (valid.Count == 0 && invalid.Count == 0)
            return new(false, "No recipients set — report available in the portal.");
        if (!(await email.StatusAsync(organizationId, ct)).Configured)
            return new(false, "Email delivery is not configured; report available for download in the portal.");
        if (valid.Count == 0)
            return new(false, $"No valid email address in recipients ({string.Join(", ", invalid)}); report available in the portal.");
        if (valid.Count > EmailAddresses.MaxRecipients)
            return new(false, $"{valid.Count} recipients is more than the limit of {EmailAddresses.MaxRecipients}; report available in the portal.");

        try
        {
            await email.SendAsync(organizationId, new EmailMessage(
                valid, subject,
                $"{subject}\n\nThe report is attached as {fileName}. It is also available to download in the portal.\n",
                Attachments: [new EmailAttachment(fileName, "text/csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray())]), ct);

            var note = $"Emailed to {valid.Count} recipient{(valid.Count == 1 ? "" : "s")}.";
            if (invalid.Count > 0) note += $" Skipped invalid: {string.Join(", ", invalid)}.";
            return new(true, note);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception type and message only: SMTP errors name the server's reason (bad
            // credentials, relay refused) and never carry the password.
            logger.LogWarning(ex, "Report email '{Subject}' could not be sent", subject);
            return new(false, $"Email could not be sent ({ex.GetType().Name}: {ex.Message}); report available in the portal.");
        }
    }
}
