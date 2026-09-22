using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Desk.Application.Common;

namespace Desk.Infrastructure.Email;

/// <summary>
/// Microsoft 365 mail through the Graph API, with the organization's own app registration
/// (client-credentials sign-in, application permission Mail.Send). No SMTP, no password sign-in,
/// and the message leaves from a real mailbox with Microsoft's own SPF/DKIM - so it does not land
/// in Junk the way direct send from an unlisted server does.
///
/// Endpoints are fixed to Microsoft's global cloud; nothing the administrator types becomes a URL
/// other than the tenant id, which is validated to a GUID or a domain name first.
/// </summary>
public sealed partial class GraphMailSender(HttpClient http)
{
    public const string HttpClientName = "graph-mail";
    public const string Host = "graph.microsoft.com";

    /// <summary>Graph's limit for an attachment sent inline with the message (larger ones need an upload session).</summary>
    public const int MaxInlineBytes = 3 * 1024 * 1024;

    [GeneratedRegex(@"^(?=.{3,100}$)[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$")]
    private static partial Regex DomainPattern();

    /// <summary>A directory (tenant) id: a GUID, or a verified domain such as contoso.onmicrosoft.com.</summary>
    public static bool IsValidTenant(string tenant)
        => Guid.TryParse(tenant, out _) || DomainPattern().IsMatch(tenant);

    public async Task SendAsync(SmtpAccount account, EmailMessage message, CancellationToken ct)
    {
        if (account.GraphTenantId is not { } tenant || !IsValidTenant(tenant) || account.GraphClientId is not { } clientId)
            throw new InvalidOperationException("The Microsoft 365 account is incomplete; enter the tenant and client ID again.");
        if (string.IsNullOrEmpty(account.Password))
            throw new InvalidOperationException("The Microsoft 365 client secret is missing; enter it again.");

        var token = await TokenAsync(tenant, clientId, account.Password, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{Host}/v1.0/users/{Uri.EscapeDataString(account.From)}/sendMail")
        {
            Content = JsonContent.Create(Body(message, account.From, account.FromName)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain((int)response.StatusCode, await ErrorAsync(response, ct), account.From));
    }

    private async Task<string> TokenAsync(string tenant, string clientId, string secret, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = $"https://{Host}/.default",
        });
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", form, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (response.IsSuccessStatusCode && doc.RootElement.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } token)
            return token;

        var code = doc.RootElement.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0
            ? codes[0].GetInt32() : 0;
        // The common sign-in failures, in the words of the fix. The raw description is long and
        // carries trace ids; the code alone is what support will ask for.
        throw new InvalidOperationException(code switch
        {
            7000215 => "Microsoft refused the client secret. Copy the secret's Value (not its Secret ID), or create a new one if it expired.",
            7000222 => "The client secret has expired. Create a new one in the app registration and save it here.",
            700016 => "Microsoft does not know that Application (client) ID in this tenant. Check both IDs.",
            90002 or 900023 => "Microsoft does not know that tenant. Use the Directory (tenant) ID from the app registration.",
            _ => $"Microsoft 365 sign-in failed (AADSTS{code}). Check the tenant ID, client ID and secret.",
        });
    }

    /// <summary>The sendMail body. Several recipients go in Bcc, as with SMTP, so no one's address is handed around.</summary>
    public static object Body(EmailMessage message, string from, string fromName)
    {
        var total = 0L;
        var attachments = new List<object>();
        foreach (var a in message.Attachments ?? [])
        {
            if (a.Content.Length > MaxInlineBytes)
                throw new InvalidOperationException($"{a.FileName} is larger than 3 MB, which Microsoft 365 sending does not support yet.");
            total += a.Content.Length;
            attachments.Add(new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = a.FileName,
                ["contentType"] = a.ContentType,
                ["contentBytes"] = Convert.ToBase64String(a.Content),
            });
        }
        if (total > MaxInlineBytes)
            throw new InvalidOperationException("The attachments together are larger than 3 MB, which Microsoft 365 sending does not support yet.");

        static object Address(string address, string? name = null)
            => new { emailAddress = name is null ? (object)new { address } : new { address, name } };

        var to = message.To.Count == 1 ? [Address(message.To[0])] : new[] { Address(from, fromName) };
        var bcc = message.To.Count == 1 ? Array.Empty<object>() : message.To.Select(t => Address(t)).ToArray();
        var html = !string.IsNullOrEmpty(message.HtmlBody);
        return new
        {
            message = new
            {
                subject = message.Subject,
                body = new { contentType = html ? "HTML" : "Text", content = html ? message.HtmlBody : message.TextBody },
                from = Address(from, fromName),
                toRecipients = to,
                bccRecipients = bcc,
                attachments,
            },
            saveToSentItems = false,
        };
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static string Explain(int status, string? code, string mailbox) => (status, code) switch
    {
        (403, _) or (_, "ErrorAccessDenied") =>
            "Microsoft 365 refused to send: the app registration needs the Mail.Send application permission with admin consent, " +
            $"and any application access policy must include {mailbox}.",
        (404, _) or (_, "ErrorInvalidUser") or (_, "MailboxNotEnabledForRESTAPI") =>
            $"Microsoft 365 has no mailbox {mailbox} in this tenant. The From address must be a real, licensed mailbox or a shared mailbox.",
        (429, _) => "Microsoft 365 is throttling this app. The next scheduled send will try again.",
        _ => $"Microsoft 365 did not send the message ({status}{(code is null ? "" : ", " + code)}).",
    };
}
