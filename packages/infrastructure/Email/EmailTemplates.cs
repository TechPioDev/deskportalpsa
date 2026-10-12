using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Desk.Infrastructure.Email;

/// <summary>One kind of e-mail the portal sends, with the product's own wording for it.</summary>
public sealed record EmailTemplateDefinition(
    string Key, string Name, string Description, string Subject, string Body, string? ButtonLabel, IReadOnlyList<string> Placeholders)
{
    public bool HasLink => Placeholders.Contains("link");
}

/// <summary>
/// The catalogue of e-mails and their default wording. An organization may rewrite any of them;
/// what it has not rewritten is sent as it stands here. Placeholders are {{name}}, and a template
/// may use only the placeholders its kind offers.
/// </summary>
public static class EmailTemplates
{
    public const string Product = "PioManage";

    public const string UserInvited = "user.invited";
    public const string InviteReminder = "user.invite_reminder";
    public const string InviteExpired = "user.invite_expired";
    public const string UserWelcome = "user.welcome";
    public const string PasswordReset = "user.password_reset";

    private static readonly string[] Person = ["user.name", "user.first_name", "user.email", "organization.name", "product", "product_url"];
    private static readonly string[] WithLink = [.. Person, "link", "expires", "invited_by"];

    public static readonly IReadOnlyList<EmailTemplateDefinition> All =
    [
        new(UserInvited, "Invitation", "Sent when someone is invited to sign in for the first time.",
            "You're invited to {{product}} by {{organization.name}}",
            "Hi {{user.first_name}},\n\n{{invited_by}} has invited you to {{organization.name}}'s {{product}} account. Use the button below to choose your password and sign in.\n\nThis link is yours alone and works until {{expires}}. If you were not expecting it, you can ignore this e-mail.",
            "Set my password and sign in", WithLink),
        new(InviteReminder, "Invitation reminder", "Sent while an invitation is still waiting to be accepted.",
            "Reminder: your {{product}} invitation is waiting",
            "Hi {{user.first_name}},\n\nYour invitation to {{organization.name}}'s {{product}} account has not been used yet. This e-mail carries a fresh link, and the earlier link no longer works. It is good until {{expires}}; after that you will need to ask for a new one.",
            "Set my password and sign in", WithLink),
        new(InviteExpired, "Invitation expired", "Sent once, after an invitation runs out unused.",
            "Your {{product}} invitation has expired",
            "Hi {{user.first_name}},\n\nThe invitation to {{organization.name}}'s {{product}} account expired on {{expires}} without being used. Ask {{invited_by}} or your administrator for a new one.",
            null, [.. Person, "expires", "invited_by"]),
        new(UserWelcome, "Welcome", "Sent after a person has set their password and can sign in.",
            "Welcome to {{product}}, {{user.first_name}}",
            "Hi {{user.first_name}},\n\nYour {{product}} sign-in for {{organization.name}} is ready. Sign in with {{user.email}} and the password you chose.",
            "Open {{product}}", [.. Person, "link"]),
        new(PasswordReset, "Password reset", "Sent when a person asks for a new password.",
            "Reset your {{product}} password",
            "Hi {{user.first_name}},\n\nSomebody asked to reset the {{product}} password for {{user.email}}. If that was you, use the button below; the link works until {{expires}}.\n\nIf it was not you, nothing has changed and you can ignore this e-mail.",
            "Choose a new password", [.. Person, "link", "expires"]),
    ];

    public static EmailTemplateDefinition? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    private static readonly Regex Placeholder = new(@"\{\{\s*([a-z_.]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The placeholders a text uses.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string text)
        => Placeholder.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    /// <summary>The placeholders a text uses that its kind does not offer. Saving such a text is refused.</summary>
    public static IReadOnlyList<string> Unknown(EmailTemplateDefinition definition, string text)
        => PlaceholdersIn(text).Where(p => !definition.Placeholders.Contains(p)).ToList();

    /// <summary>Fills a text. A placeholder with no value is left blank, never left as braces for the reader to see.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
        => Placeholder.Replace(text, m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v : "");

    /// <summary>Sample values for a preview or a test, so every placeholder shows something.</summary>
    public static IReadOnlyDictionary<string, string> SampleValues(string organizationName, string productUrl, string? toEmail = null)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user.name"] = "Asha Rao", ["user.first_name"] = "Asha", ["user.email"] = toEmail ?? "asha@example.com",
            ["organization.name"] = organizationName, ["product"] = Product, ["product_url"] = productUrl,
            ["link"] = productUrl.TrimEnd('/') + "/invite/example-link-not-real", ["expires"] = "Tuesday 14 October 2026, 09:00",
            ["invited_by"] = "Priya Nair",
        };

    /// <summary>
    /// The HTML form of a filled template: the paragraphs of the text, the link as a button where
    /// there is one, and the product's name in the header and footer. Table-based with inline
    /// styles, which is what mail readers render; the text form is always sent beside it.
    /// </summary>
    public static string Html(string filledBody, string? filledButtonLabel, string? link, string productUrl)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><body style=\"margin:0;padding:0;background:#f4f5f7;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f5f7;padding:24px 0;\"><tr><td align=\"center\">")
          .Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;width:100%;background:#ffffff;border-radius:12px;border:1px solid #e5e7eb;font-family:Segoe UI,Arial,sans-serif;color:#1f2933;\">")
          .Append("<tr><td style=\"padding:22px 28px 6px 28px;\"><span style=\"font-size:20px;font-weight:800;color:#14532d;letter-spacing:-.01em;\">Pio<span style=\"color:#15803d;\">Manage</span></span></td></tr>")
          .Append("<tr><td style=\"padding:8px 28px 8px 28px;font-size:15px;line-height:1.55;\">");
        foreach (var paragraph in filledBody.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            sb.Append("<p style=\"margin:0 0 14px 0;\">").Append(E(paragraph.Trim()).Replace("\n", "<br>")).Append("</p>");
        sb.Append("</td></tr>");
        if (!string.IsNullOrEmpty(link) && !string.IsNullOrWhiteSpace(filledButtonLabel))
            sb.Append("<tr><td style=\"padding:6px 28px 18px 28px;\"><a href=\"").Append(E(link)).Append("\" style=\"display:inline-block;background:#15803d;color:#ffffff;text-decoration:none;font-weight:600;font-size:15px;padding:11px 20px;border-radius:8px;\">")
              .Append(E(filledButtonLabel!)).Append("</a>")
              .Append("<p style=\"margin:12px 0 0 0;font-size:12px;color:#6b7280;\">If the button does not work, copy this address into your browser:<br><span style=\"word-break:break-all;\">").Append(E(link)).Append("</span></p></td></tr>");
        sb.Append("<tr><td style=\"padding:14px 28px 22px 28px;border-top:1px solid #e5e7eb;font-size:12px;color:#6b7280;\">Sent by ")
          .Append(productUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
              ? $"<a href=\"{E(productUrl)}\" style=\"color:#15803d;text-decoration:none;font-weight:600;\">{Product}</a>"
              : Product)
          .Append(". This message was sent to you by name; it is not a mailing list.</td></tr>")
          .Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    /// <summary>The text form: the body, then the link on its own line where there is one.</summary>
    public static string Text(string filledBody, string? link)
        => string.IsNullOrEmpty(link) ? filledBody : filledBody.TrimEnd() + "\n\n" + link + "\n";
}
