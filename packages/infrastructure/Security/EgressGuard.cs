using System.Net;
using System.Net.Sockets;
using Desk.PsaCore.Contracts;
using Microsoft.Extensions.Configuration;

namespace Desk.Infrastructure.Security;

/// <summary>
/// SSRF egress guard for connector HttpClients. Blocks requests whose host resolves to a loopback,
/// private, link-local, or otherwise reserved address so a misconfigured or malicious connection URL
/// cannot reach internal services (including the cloud metadata endpoint 169.254.169.254).
///
/// On unless the process runs in local mode, in the API and the worker alike. It used to be opt-in
/// and the production worker - which makes every scheduled PSA call - was never opted in. A
/// self-hosted PSA on a private network is allowed by naming its host in
/// <c>Connectors:AllowedHosts</c>: an operator's decision on the server, never a tenant's.
///
/// Two layers. This handler answers first, with a message an administrator can read. The connect
/// callback (<see cref="PinnedHandler"/>) is the one that binds: it checks the address the socket
/// is about to open, so a name that resolves to a public address when checked and a private one
/// when connected gets no further, and neither does a redirect.
/// </summary>
public sealed class EgressGuard(ISet<string> allowedHosts) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host;
        if (host is not null && !allowedHosts.Contains(host))
        {
            var addresses = await ResolveAsync(host, ct);
            if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
                throw Blocked(host);
        }
        return await base.SendAsync(request, ct);
    }

    /// <summary>
    /// Whether connector calls are held to public addresses. An explicit
    /// <c>Connectors:BlockPrivateEgress</c> wins; left unset, it is on everywhere except local mode,
    /// where the PSA on the other end is a fake on this machine.
    /// </summary>
    public static bool IsEnabled(IConfiguration config)
        => config.GetValue<bool?>("Connectors:BlockPrivateEgress") ?? !config.GetValue("LocalMode:Enabled", false);

    public static HashSet<string> AllowedHosts(IConfiguration config)
        => new(config.GetSection("Connectors:AllowedHosts").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The transport for a guarded client. It connects to an address it has just checked, rather
    /// than letting the name be resolved a second time after the check, and it does not follow
    /// redirects: a connector sends its credentials as headers, and a redirect would carry them to
    /// wherever the answer pointed.
    /// </summary>
    public static SocketsHttpHandler PinnedHandler(ISet<string> allowedHosts) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await ResolveAsync(host, ct);
            if (!allowedHosts.Contains(host) && (addresses.Length == 0 || addresses.Any(IsBlockedAddress)))
                throw Blocked(host);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);

    private static ConnectorException Blocked(string host)
        => new(ConnectorFailureKind.InvalidRequest, $"Blocked connector egress to a private/reserved host: {host}");

    /// <summary>Pure classification of an address as private/reserved (not internet-routable).</summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        // An IPv4 address written as IPv6 (::ffff:10.0.0.1) is the IPv4 address. Judged as IPv6 it
        // matched none of the ranges below and went straight through.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local (metadata)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 CGNAT
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)      // 192.0.0.0/24 protocol assignments
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))  // 198.18.0.0/15 benchmarking
                || b[0] >= 224                                  // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast
                || b[0] == 0;                                   // 0.0.0.0/8
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None)) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local

            // 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) carry an IPv4 address inside them, and the
            // packet ends up there: a private one is as private as if it had been written plainly.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.Skip(4).Take(8).All(x => x == 0))
                return IsBlockedAddress(new IPAddress(b.AsSpan(12, 4)));
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsBlockedAddress(new IPAddress(b.AsSpan(2, 4)));
        }

        return false;
    }
}
