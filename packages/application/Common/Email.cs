using System.Net.Mail;

namespace Desk.Application.Common;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string TextBody,
    string? HtmlBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>Where an organization's mail goes out from, if anywhere.</summary>
/// <param name="Source">"organization" (entered in the portal), "server" (the host's environment) or "none".</param>
public sealed record EmailSenderStatus(bool Configured, string? FromAddress, string Source)
{
    public static readonly EmailSenderStatus None = new(false, null, "none");
}

/// <summary>
/// Outbound email for one organization. Its own account, entered by its administrator, wins; the
/// server's environment account is the fallback; with neither, <see cref="StatusAsync"/> says so and
/// callers report "not configured" rather than pretending a message went out.
///
/// The organization is passed explicitly rather than read from the tenant context, because scheduled
/// reports are sent by the worker from a platform-scoped loop.
/// </summary>
public interface IEmailSender
{
    Task<EmailSenderStatus> StatusAsync(Guid organizationId, CancellationToken ct = default);
    Task SendAsync(Guid organizationId, EmailMessage message, CancellationToken ct = default);
}

public sealed record EmailSettingsDto(
    bool HasOwnAccount, string? Host, int Port, string Security, string? Username, bool HasPassword,
    string? FromAddress, string? FromName, EmailSenderStatus Status,
    string Method = "Smtp", string? GraphTenantId = null, string? GraphClientId = null);

/// <param name="Password">Null keeps the stored password (or, for Graph, the client secret); an empty string removes it.</param>
/// <param name="Method">"Smtp" (default) or "Graph". For Graph, Host/Port/Security/Username are ignored.</param>
public sealed record EmailSettingsInput(
    string Host, int Port, string Security, string? Username, string? Password, string FromAddress, string? FromName,
    string? Method = null, string? GraphTenantId = null, string? GraphClientId = null);

/// <summary>The current organization's mail account. Never returns the password.</summary>
public interface IEmailSettingsService
{
    Task<EmailSettingsDto> GetAsync(CancellationToken ct = default);
    Task<EmailSettingsDto> SaveAsync(EmailSettingsInput input, CancellationToken ct = default);
    Task<EmailSettingsDto> RemoveAsync(CancellationToken ct = default);
}

public static class EmailAddresses
{
    /// <summary>More than this in one recipients box is a mailing list, not a report audience.</summary>
    public const int MaxRecipients = 20;

    /// <summary>
    /// Splits a recipients box ("a@x.com; b@y.com, c@z.com") into addresses that parse and the
    /// entries that do not, so a typo in one address is reported instead of failing the whole send.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) Parse(string? raw)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return (valid, invalid);

        foreach (var part in raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // MailAddress accepts "Name <a@b>" and bare addresses; require a dot in the domain so
            // "bob@localhost"-style typos from a missing ".com" are caught before a bounce is.
            if (MailAddress.TryCreate(part, out var address) && address.Host.Contains('.'))
            {
                if (!valid.Contains(address.Address, StringComparer.OrdinalIgnoreCase))
                    valid.Add(address.Address);
            }
            else
            {
                invalid.Add(part);
            }
        }
        return (valid, invalid);
    }
}
