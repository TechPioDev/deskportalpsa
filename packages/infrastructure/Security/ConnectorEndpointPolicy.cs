using System.Net;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace Desk.Infrastructure.Security;

/// <summary>
/// What a PSA connection's API address may be, checked when it is saved.
///
/// The address used to be stored exactly as typed. The connection's credentials are sent to it on
/// every call, so an address is as sensitive as the credentials are: plain http would send them in
/// the clear, a private address would aim them at the server's own network, and an Autotask
/// connection pointed anywhere but Autotask would simply hand the keys to that host.
/// <see cref="EgressGuard"/> checks again at connect time, where the name is actually resolved;
/// this is the earlier and clearer of the two, and says what to correct.
/// </summary>
public sealed class ConnectorEndpointPolicy(bool allowInsecure, bool blockPrivate, ISet<string> allowedHosts)
{
    /// <summary>https only, public addresses only. What production runs, and the default for tests.</summary>
    public static ConnectorEndpointPolicy Strict { get; } = new(false, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// <c>Connectors:AllowInsecureEndpoints</c> relaxes every rule for a machine whose "PSA" is a
    /// fake on localhost; left unset it follows local mode. <c>Connectors:AllowedHosts</c> names
    /// hosts an operator has decided to trust - a self-hosted PSA on a private network.
    /// </summary>
    public static ConnectorEndpointPolicy From(IConfiguration config) => new(
        config.GetValue<bool?>("Connectors:AllowInsecureEndpoints") ?? config.GetValue("LocalMode:Enabled", false),
        EgressGuard.IsEnabled(config),
        EgressGuard.AllowedHosts(config));

    /// <summary>The address to store, or a <see cref="ValidationFailedException"/> saying what to correct.</summary>
    public string Validate(ProviderType provider, string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new ValidationFailedException("Enter the PSA's API address.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || uri.Scheme is not ("http" or "https"))
            throw new ValidationFailedException("The API address is not a web address. It should start with https://");
        // Everything wrong with it, in one answer. Reported one at a time, an address with three
        // problems took three saves to get right.
        var problems = new List<string>();
        if (!string.IsNullOrEmpty(uri.UserInfo))
            problems.Add("Leave the user name and password out of the API address. Enter the credentials in their own fields.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            problems.Add("The API address cannot contain ? or #.");

        if (!allowInsecure)
        {
            if (uri.Scheme != "https")
                problems.Add("The API address must start with https://. Over plain http the connection's credentials would be sent unencrypted.");

            var host = uri.IdnHost;
            if (!allowedHosts.Contains(host))
            {
                // One sentence about the host, not two: an Autotask address that is not Autotask's is
                // wrong whichever network it is on.
                if (provider == ProviderType.AutotaskPsa && !IsUnder(host, "autotask.net"))
                    problems.Add("An Autotask API address is on autotask.net - for example https://webservices5.autotask.net. "
                                 + "Use the address of your own Autotask zone.");
                else if (blockPrivate && IsPrivate(host))
                    problems.Add("That address is on a private or reserved network. A PSA on a private network has to be allowed by "
                                 + "whoever runs this server (Connectors:AllowedHosts).");
            }
        }

        return problems.Count == 0 ? value : throw new ValidationFailedException(string.Join(" ", problems));
    }

    private static bool IsUnder(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What can be known without resolving the name: a literal address in a private range, or a
    /// name that only ever means this machine. Anything else is judged when it is connected to.
    /// </summary>
    private static bool IsPrivate(string host)
        => IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? EgressGuard.IsBlockedAddress(literal)
            : IsUnder(host, "localhost") || IsUnder(host, "local") || IsUnder(host, "internal");
}
