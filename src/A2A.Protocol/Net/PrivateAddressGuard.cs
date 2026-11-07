using System.Net;
using System.Net.Sockets;

namespace A2A.Net;

/// <summary>
/// SSRF guard shared by outbound fetches to peer-chosen URLs: push webhooks (server) and <c>jku</c> key sets (Agent Card
/// verification). Moved here from <c>PushUrlValidator</c> without behaviour change so the client can use it too.
/// </summary>
internal static class PrivateAddressGuard
{
    /// <summary>True for addresses that must not be reached by default.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,
                100 => b[1] >= 64 && b[1] <= 127,          // 100.64.0.0/10 CGNAT
                169 => b[1] == 254,                         // link-local, cloud metadata
                172 => b[1] >= 16 && b[1] <= 31,
                192 => (b[1] == 168) || (b[1] == 0 && b[2] == 0), // 192.168/16, 192.0.0/24
                198 => b[1] == 18 || b[1] == 19,            // benchmarking
                >= 224 => true,                             // multicast, reserved, broadcast
                _ => false,
            };
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local
            return false;
        }
        return true;
    }

    /// <summary>
    /// Primary handler that enforces the IP policy on every connection (resolved IP, so DNS rebinding is blocked too) and
    /// disables redirects. <paramref name="hostKind"/> names the target in error messages ("Webhook", "JWKS").
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks, string hostKind) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
            if (!allowPrivateNetworks && addresses.Any(IsPrivate))
                throw new HttpRequestException($"{hostKind} host '{ctx.DnsEndPoint.Host}' resolves to a private address.");
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    last = ex;
                }
            }
            throw new HttpRequestException($"Could not connect to {hostKind.ToLowerInvariant()} host '{ctx.DnsEndPoint.Host}'.", last);
        },
    };
}
