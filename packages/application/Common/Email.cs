using System.Net.Mail;

namespace Desk.Application.Common;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string TextBody,
    string? HtmlBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>
/// Outbound email. Deliberately small: the portal sends reports and a test message, nothing that
/// needs templates or tracking. When no mail server is configured <see cref="IsConfigured"/> is
/// false and callers say so rather than pretending a message went out.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>The address messages are sent from, for display. Null when not configured.</summary>
    string? FromAddress { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct = default);
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
