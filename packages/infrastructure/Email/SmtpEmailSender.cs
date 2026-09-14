using Desk.Application.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Desk.Infrastructure.Email;

/// <summary>
/// Bound from configuration section <c>Email:Smtp</c> (environment: <c>Email__Smtp__Host</c> etc.).
/// Credentials come from the environment the operator sets on the server — never from the database
/// and never from the UI, so no one with portal access can read or change them.
/// </summary>
public sealed class SmtpOptions
{
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? From { get; init; }
    public string FromName { get; init; } = "Desk Portal";

    /// <summary>"StartTls" (587, the default), "SslOnConnect" (465) or "None" (a local relay only).</summary>
    public string Security { get; init; } = "StartTls";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public bool IsConfigured => options.IsConfigured;
    public string? FromAddress => options.IsConfigured ? options.From : null;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("Email is not configured.");
        if (message.To.Count == 0)
            throw new ArgumentException("An email needs at least one recipient.", nameof(message));

        using var smtp = new SmtpClient { Timeout = 30_000 };
        await smtp.ConnectAsync(options.Host!, options.Port, SecurityFor(options.Security), ct);
        if (!string.IsNullOrEmpty(options.Username))
            await smtp.AuthenticateAsync(options.Username, options.Password ?? "", ct);
        await smtp.SendAsync(Build(message, options), ct);
        await smtp.DisconnectAsync(true, ct);
    }

    /// <summary>Separate from sending so the message shape is testable without a mail server.</summary>
    public static MimeMessage Build(EmailMessage message, SmtpOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.From!));
        // Several recipients go in Bcc: a client's report schedule lists their own staff, and
        // one person's report email should not hand every other recipient's address around.
        if (message.To.Count == 1) mime.To.Add(MailboxAddress.Parse(message.To[0]));
        else
        {
            mime.To.Add(new MailboxAddress(options.FromName, options.From!));
            foreach (var to in message.To) mime.Bcc.Add(MailboxAddress.Parse(to));
        }
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var a in message.Attachments ?? [])
            body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static SecureSocketOptions SecurityFor(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sslonconnect" or "ssl" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
