using System.Net;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Desk.Infrastructure.Email;

/// <summary>
/// The server's fallback account, bound from configuration section <c>Email:Smtp</c>
/// (environment: <c>Email__Smtp__Host</c> etc.). Used only by an organization that has not entered its own.
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

    /// <summary>
    /// Refuse organization-entered hosts that resolve to private or reserved addresses. The same switch
    /// as the connectors' egress guard: an administrator's form must not become a way to reach services
    /// inside the host's network. The server's own account is trusted — only the operator can set it.
    /// </summary>
    public bool BlockPrivateHosts { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>One resolved account to send through.</summary>
public sealed record SmtpAccount(
    string Host, int Port, string Security, string? Username, string? Password, string From, string FromName, bool Guarded);

public sealed class SmtpEmailSender(DeskDbContext db, ISecretStore secrets, SmtpOptions server) : IEmailSender
{
    public async Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default)
    {
        var own = await OwnAsync(organizationId, ct);
        if (own is not null) return new EmailSenderStatus(true, own.FromAddress, "organization");
        return server.IsConfigured ? new EmailSenderStatus(true, server.From, "server") : EmailSenderStatus.None;
    }

    public async Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default)
    {
        if (message.To.Count == 0)
            throw new ArgumentException("An email needs at least one recipient.", nameof(message));

        var account = await AccountAsync(organizationId, ct) ?? throw new InvalidOperationException("Email is not configured.");
        if (account.Guarded) await EnsurePublicHostAsync(account.Host, ct);

        using var smtp = new SmtpClient { Timeout = 30_000 };
        await smtp.ConnectAsync(account.Host, account.Port, SecurityFor(account.Security), ct);
        // Sign in only where the server offers it. A Microsoft 365 "direct send" endpoint
        // (<domain>.mail.protection.outlook.com:25) accepts mail for its own domain with no login and
        // advertises no AUTH; insisting on one failed every send with "does not support
        // authentication" whenever a username had been saved. Whether unauthenticated mail is
        // accepted remains the server's decision, and its refusal is reported as it was before.
        if (!string.IsNullOrEmpty(account.Username) && smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await smtp.AuthenticateAsync(account.Username, account.Password ?? "", ct);
        await smtp.SendAsync(Build(message, account.From, account.FromName), ct);
        await smtp.DisconnectAsync(true, ct);
    }

    private Task<Desk.Domain.Tenancy.OrganizationEmailSettings?> OwnAsync(Guid organizationId, CancellationToken ct)
        // Explicit organization, query filters off: the worker calls this from platform scope, and the
        // filter would otherwise decide which organization's account a report goes out through.
        => db.OrganizationEmailSettings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.MspOrganizationId == organizationId, ct);

    private async Task<SmtpAccount?> AccountAsync(Guid organizationId, CancellationToken ct)
    {
        var own = await OwnAsync(organizationId, ct);
        if (own is not null)
        {
            string? password = null;
            if (own.PasswordSecretRef is { } secretRef)
                password = (await secrets.ReadAsync(secretRef, ct)).GetValueOrDefault("Password");
            return new SmtpAccount(own.Host, own.Port, own.Security, own.Username, password, own.FromAddress, own.FromName, server.BlockPrivateHosts);
        }
        return server.IsConfigured
            ? new SmtpAccount(server.Host!, server.Port, server.Security, server.Username, server.Password, server.From!, server.FromName, false)
            : null;
    }

    /// <summary>Resolves the host and refuses private or reserved addresses. Checked again at send time, not only on save, because DNS can change.</summary>
    public static async Task EnsurePublicHostAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (System.Net.Sockets.SocketException)
        {
            throw new ValidationFailedException($"The mail server {host} could not be found. Check the name.");
        }
        if (addresses.Length == 0 || addresses.Any(EgressGuard.IsBlockedAddress))
            throw new ValidationFailedException($"The mail server {host} is on a private network address, which is not allowed.");
    }

    /// <summary>Separate from sending so the message shape is testable without a mail server.</summary>
    public static MimeMessage Build(EmailMessage message, string from, string fromName)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(fromName, from));
        // Several recipients go in Bcc: a client's report schedule lists their own staff, and
        // one person's report email should not hand every other recipient's address around.
        if (message.To.Count == 1) mime.To.Add(MailboxAddress.Parse(message.To[0]));
        else
        {
            mime.To.Add(new MailboxAddress(fromName, from));
            foreach (var to in message.To) mime.Bcc.Add(MailboxAddress.Parse(to));
        }
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var a in message.Attachments ?? [])
            body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        mime.Body = body.ToMessageBody();
        return mime;
    }

    public static readonly string[] SecurityModes = ["StartTls", "SslOnConnect", "None"];

    private static SecureSocketOptions SecurityFor(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sslonconnect" or "ssl" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
