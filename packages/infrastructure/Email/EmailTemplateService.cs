using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Identity;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Email;

/// <summary>
/// An administrator's hand on the organization's e-mail wording: see each template as it would
/// be sent, change it, send it to themselves, put the default back. A template may use only the
/// placeholders its kind offers, so a typo cannot make a blank where a link should be.
/// </summary>
public sealed class EmailTemplateService(
    DeskDbContext db, ITenantContext tenant, ICurrentUser user, TemplateMailer mail, IEmailSender sender, PortalOptions portal,
    IAuditWriter audit, TimeProvider clock) : IEmailTemplateService
{
    private Guid Org => tenant.OrganizationId ?? throw new InvalidOperationException("E-mail templates belong to an organization.");

    public async Task<IReadOnlyList<EmailTemplateDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.EmailTemplates.AsNoTracking().ToListAsync(ct);
        return EmailTemplates.All.Select(d => ToDto(d, rows.FirstOrDefault(r => r.Key == d.Key))).ToList();
    }

    public async Task<EmailTemplateDto> GetAsync(string key, CancellationToken ct = default)
    {
        var definition = EmailTemplates.Find(key) ?? throw new NotFoundException("E-mail template");
        return ToDto(definition, await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct));
    }

    public async Task<EmailTemplateDto> SaveAsync(string key, EmailTemplateInput input, CancellationToken ct = default)
    {
        var definition = EmailTemplates.Find(key) ?? throw new NotFoundException("E-mail template");
        var subject = (input.Subject ?? "").Trim();
        var body = (input.Body ?? "").Replace("\r\n", "\n").Trim();
        var button = string.IsNullOrWhiteSpace(input.ButtonLabel) ? null : input.ButtonLabel.Trim();
        var problems = new List<string>();
        if (subject.Length is 0 or > 200) problems.Add("The subject must be between 1 and 200 characters.");
        if (body.Length is 0 or > 5000) problems.Add("The body must be between 1 and 5,000 characters.");
        if (button is { Length: > 80 }) problems.Add("The button label must be 80 characters or fewer.");
        if (definition.HasLink && button is null) problems.Add("This e-mail carries a link, so it needs a button label.");
        foreach (var unknown in EmailTemplates.Unknown(definition, subject + "\n" + body + "\n" + (button ?? "")))
            problems.Add($"{{{{{unknown}}}}} is not a placeholder this e-mail offers. It offers: {string.Join(", ", definition.Placeholders.Select(p => "{{" + p + "}}"))}.");
        if (problems.Count > 0) throw new ValidationFailedException(string.Join(" ", problems));

        var row = await db.EmailTemplates.FirstOrDefaultAsync(r => r.Key == key, ct);
        var before = row is null ? "default" : row.Subject;
        if (row is null)
        {
            row = new EmailTemplate { MspOrganizationId = Org, Key = key, Subject = subject, Body = body };
            db.EmailTemplates.Add(row);
        }
        (row.Subject, row.Body, row.ButtonLabel, row.UpdatedByUserId, row.UpdatedByName) = (subject, body, button, user.UserId, user.DisplayName);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("email.template_changed", nameof(EmailTemplate), key, new { subjectBefore = before, subject }, ct);
        return ToDto(definition, row);
    }

    public async Task<EmailTemplateDto> ResetAsync(string key, CancellationToken ct = default)
    {
        var definition = EmailTemplates.Find(key) ?? throw new NotFoundException("E-mail template");
        var row = await db.EmailTemplates.FirstOrDefaultAsync(r => r.Key == key, ct);
        if (row is not null)
        {
            db.EmailTemplates.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("email.template_reset", nameof(EmailTemplate), key, null, ct);
        }
        return ToDto(definition, null);
    }

    public async Task<RenderedEmailDto> PreviewAsync(string key, EmailTemplateInput? unsaved = null, CancellationToken ct = default)
    {
        var (definition, subject, body, button, _) = await mail.ResolveAsync(Org, key, ct);
        if (unsaved is not null) (subject, body, button) = (unsaved.Subject ?? "", unsaved.Body ?? "", unsaved.ButtonLabel);
        var to = user.Email ?? "you@example.com";
        var message = mail.Render(to, subject, body, button, await SampleAsync(to, ct));
        return new RenderedEmailDto(to, message.Subject, message.TextBody, message.HtmlBody ?? "");
    }

    public async Task<RenderedEmailDto> SendTestAsync(string key, CancellationToken ct = default)
    {
        var to = user.Email ?? throw new ValidationFailedException("Your account has no e-mail address to send the test to.");
        var status = await sender.StatusAsync(Org, ct);
        if (!status.Configured) throw new ValidationFailedException("No outgoing mail account is set up. Set one under Email delivery first.");
        var (_, subject, body, button, _) = await mail.ResolveAsync(Org, key, ct);
        var message = mail.Render(to, "[Test] " + subject, body, button, await SampleAsync(to, ct));
        await sender.SendAsync(Org, message, ct);
        db.EmailLog.Add(new EmailLogEntry { MspOrganizationId = Org, TemplateKey = key, To = to, Subject = message.Subject, SentAt = clock.GetUtcNow(), Succeeded = true, SubjectType = "Test" });
        await db.SaveChangesAsync(ct);
        return new RenderedEmailDto(to, message.Subject, message.TextBody, message.HtmlBody ?? "");
    }

    public async Task<IReadOnlyList<EmailLogDto>> RecentLogAsync(int take = 100, CancellationToken ct = default)
        => await db.EmailLog.AsNoTracking().OrderByDescending(e => e.SentAt).Take(Math.Clamp(take, 1, 500))
            .Select(e => new EmailLogDto(e.Id, e.TemplateKey, e.To, e.Subject, e.SentAt, e.Succeeded, e.Error)).ToListAsync(ct);

    private async Task<IReadOnlyDictionary<string, string>> SampleAsync(string to, CancellationToken ct)
    {
        var orgName = await db.Set<MspOrganization>().AsNoTracking().Where(o => o.Id == Org).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? EmailTemplates.Product;
        return EmailTemplates.SampleValues(orgName, portal.PublicUrl, to);
    }

    private static EmailTemplateDto ToDto(EmailTemplateDefinition d, EmailTemplate? row)
        => new(d.Key, d.Name, d.Description, row?.Subject ?? d.Subject, row?.Body ?? d.Body, row is null ? d.ButtonLabel : row.ButtonLabel, d.HasLink,
            d.Placeholders, row is null, row?.UpdatedByName, row?.UpdatedAt);
}
