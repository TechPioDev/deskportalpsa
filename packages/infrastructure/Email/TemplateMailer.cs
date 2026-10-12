using Desk.Application.Common;
using Desk.Domain.Identity;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Email;

/// <summary>Where the portal is reached from outside, for the links it mails.</summary>
public sealed class PortalOptions
{
    public string PublicUrl { get; init; } = "http://localhost:3000";
    public string Link(string path) => PublicUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

/// <summary>
/// Sends one of the catalogue's e-mails to one person: the organization's wording where it has
/// its own, the product's otherwise; the text and HTML forms; and a log row saying whether it
/// went. The caller supplies the values; what the mail says is the template's business.
/// </summary>
public sealed class TemplateMailer(DeskDbContext db, IEmailSender sender, PortalOptions portal, TimeProvider clock, ILogger<TemplateMailer> logger)
{
    /// <summary>The template as the organization has it: its own row, or the default.</summary>
    public async Task<(EmailTemplateDefinition Definition, string Subject, string Body, string? ButtonLabel, EmailTemplate? Row)> ResolveAsync(
        Guid organizationId, string key, CancellationToken ct = default)
    {
        var definition = EmailTemplates.Find(key) ?? throw new NotFoundException("E-mail template");
        var row = await db.EmailTemplates.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.MspOrganizationId == organizationId && t.Key == key, ct);
        return row is null
            ? (definition, definition.Subject, definition.Body, definition.ButtonLabel, null)
            : (definition, row.Subject, row.Body, row.ButtonLabel, row);
    }

    /// <summary>The message as it would go, filled in.</summary>
    public EmailMessage Render(string to, string subject, string body, string? buttonLabel, IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue("link", out var link);
        var filledBody = EmailTemplates.Fill(body, values);
        var filledSubject = EmailTemplates.Fill(subject, values).Replace("\r", " ").Replace("\n", " ").Trim();
        var filledButton = buttonLabel is null ? null : EmailTemplates.Fill(buttonLabel, values);
        return new EmailMessage([to], filledSubject, EmailTemplates.Text(filledBody, link), EmailTemplates.Html(filledBody, filledButton, link, portal.PublicUrl));
    }

    /// <summary>
    /// Sends and logs. A failure is logged and returned, not thrown: the thing that caused the
    /// mail (an invitation made, a password set) has happened and stays happened.
    /// </summary>
    public async Task<(bool Sent, string? Error)> SendAsync(
        Guid organizationId, string key, string to, IReadOnlyDictionary<string, string> values,
        string? subjectType = null, Guid? subjectId = null, CancellationToken ct = default)
    {
        var (_, subject, body, button, _) = await ResolveAsync(organizationId, key, ct);
        var message = Render(to, subject, body, button, values);
        string? error = null;
        try
        {
            var status = await sender.StatusAsync(organizationId, ct);
            if (!status.Configured) error = "No outgoing mail account is set up for this organization.";
            else await sender.SendAsync(organizationId, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "E-mail {Key} to {To} could not be sent", key, to);
            error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }
        db.EmailLog.Add(new EmailLogEntry
        {
            MspOrganizationId = organizationId, TemplateKey = key, To = to, Subject = message.Subject,
            SentAt = clock.GetUtcNow(), Succeeded = error is null, Error = error, SubjectType = subjectType, SubjectId = subjectId,
        });
        await db.SaveChangesAsync(ct);
        return (error is null, error);
    }
}
