using Desk.Domain.Enums;

namespace Desk.Application.Tickets;

/// <summary>
/// Builds a deep link from a portal ticket back to the same record in the PSA, so a technician
/// verifying a time entry or a note is one click from the system of record rather than hunting for
/// it by reference.
///
/// Derived from the connection's API endpoint alone — no credentials, so no vault round-trip on a
/// page render. Each provider serves its API and its web UI from related but different hosts, and
/// the mapping between them is the whole job here.
///
/// Returns null whenever the endpoint does not match a shape we recognise. A missing link is a
/// small inconvenience; a confidently wrong one sends someone to verify a record that is not the
/// one in front of them, which is worse than offering nothing.
/// </summary>
public static class PsaTicketLink
{
    /// <param name="tenantIdentifier">
    /// The connection's tenant identifier. ConnectWise's web router refuses a record link without
    /// the company name ("Cannot route blank company name"), so for ConnectWise this is required and
    /// no link is built without it. Autotask needs nothing beyond the zone in the endpoint.
    /// </param>
    public static string? For(ProviderType provider, string? apiEndpoint, string? externalTicketId, string? tenantIdentifier = null)
    {
        if (string.IsNullOrWhiteSpace(apiEndpoint) || string.IsNullOrWhiteSpace(externalTicketId))
            return null;
        if (!Uri.TryCreate(apiEndpoint.Trim(), UriKind.Absolute, out var api))
            return null;
        if (api.Scheme != Uri.UriSchemeHttps && api.Scheme != Uri.UriSchemeHttp)
            return null;

        var id = Uri.EscapeDataString(externalTicketId.Trim());

        return provider switch
        {
            ProviderType.ConnectWisePsa => ConnectWise(api, id, tenantIdentifier),
            ProviderType.AutotaskPsa => Autotask(api, id),
            _ => null,
        };
    }

    /// <summary>
    /// ConnectWise serves its API from an "api-" prefixed host of the same site as the web UI:
    /// api-na.myconnectwise.net -> na.myconnectwise.net. A self-hosted instance serves both from
    /// one host, so an endpoint with no prefix is used unchanged.
    /// </summary>
    private static string? ConnectWise(Uri api, string id, string? companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName)) return null;
        var host = api.Host.StartsWith("api-", StringComparison.OrdinalIgnoreCase)
            ? api.Host[4..]
            : api.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;

        // The path carries the release segment (…/v4_6_release/apis/3.0/); the UI router lives
        // under that same release rather than at the site root.
        var release = api.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(s => s.StartsWith("v", StringComparison.OrdinalIgnoreCase) && s.Contains("release"));
        if (release is null) return null;

        // The service-ticket request route, the form ConnectWise publishes for linking to a ticket.
        // The generic openrecord router looked equivalent but answers "Invalid Token" from a plain
        // link: it expects a token minted by the ConnectWise UI itself.
        return $"https://{host}/{release}/services/system_io/Service/fv_sr100_request.rails" +
               $"?service_recid={id}&companyName={Uri.EscapeDataString(companyName.Trim())}";
    }

    /// <summary>
    /// Autotask pairs each API zone with a UI zone of the same number: webservices31.autotask.net
    /// -> ww31.autotask.net. Zones are not interchangeable, so an unnumbered or unfamiliar host is
    /// left alone rather than guessed at.
    /// </summary>
    private static string? Autotask(Uri api, string id)
    {
        var host = api.Host;
        const string prefix = "webservices";
        if (!host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = host[prefix.Length..];
        var dot = rest.IndexOf('.');
        if (dot <= 0) return null;

        var zone = rest[..dot];
        if (!zone.All(char.IsAsciiDigit)) return null;

        // The service desk route, as Autotask itself produces it. ids[0] is percent-encoded because
        // it is a literal bracketed parameter name, and the id appears twice — the grid selection
        // and the record to open are separate parameters that happen to carry the same value.
        return $"https://ww{zone}{rest[dot..]}/Mvc/ServiceDesk/TicketDetail.mvc" +
               $"?workspace=False&ids%5B0%5D={id}&ticketId={id}";
    }
}
