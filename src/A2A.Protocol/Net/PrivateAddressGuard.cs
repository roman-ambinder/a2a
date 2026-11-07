using System.Net;
using System.Net.Sockets;

namespace A2A.Net;

/// <summary>
/// SSRF guard shared by outbound fetches to peer-chosen URLs: push webhooks (server) and <c>jku</c> key sets (Agent Card
/// verification). Moved here from <c>PushUrlValidator</c> without behaviour change so the client can use it too.
/// </summary>
/// <summary>The target resolves to an address the policy forbids. Permanent: callers must not retry.</summary>
internal sealed class BlockedAddressException(string message) : HttpRequestException(message);

internal static class PrivateAddressGuard
{
    /// <summary>DNS seam. Defaults to <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>; tests may replace it.</summary>
    internal static Func<string, CancellationToken, ValueTask<IPAddress[]>> Resolve { get; set; } =
        async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

    /// <summary>
    /// Returns the IP literal of <paramref name="uri"/> in normalised form, or null for a DNS name. <see cref="Uri"/> already folds
    /// hex, octal and decimal IPv4 spellings (<c>0x7f000001</c>, <c>2130706433</c>) to dotted form; IPv4-mapped IPv6 is unmapped.
    /// </summary>
    public static IPAddress? TryGetLiteral(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    /// <summary>True when <paramref name="ip"/> lies inside <paramref name="network"/>/<paramref name="prefixLength"/>.</summary>
    public static bool InNetwork(IPAddress ip, IPAddress network, int prefixLength)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        if (ip.AddressFamily != network.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > a.Length * 8) return false;
        var full = prefixLength / 8;
        for (var i = 0; i < full; i++) if (a[i] != n[i]) return false;
        var rem = prefixLength % 8;
        if (rem == 0) return true;
        var mask = (byte)(0xFF << (8 - rem));
        return (a[full] & mask) == (n[full] & mask);
    }

    /// <summary>True when the address is private and not covered by <paramref name="allowedNetworks"/>.</summary>
    public static bool IsBlocked(IPAddress ip, IEnumerable<(IPAddress Network, int PrefixLength)>? allowedNetworks)
    {
        if (!IsPrivate(ip)) return false;
        if (allowedNetworks is not null)
            foreach (var (network, prefix) in allowedNetworks)
                if (InNetwork(ip, network, prefix)) return false;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and throws <see cref="HttpRequestException"/> if any address is blocked. Used before each
    /// delivery attempt; the connect-time check in <see cref="CreateHandler"/> covers the actual connection.
    /// </summary>
    public static async ValueTask EnsureHostAllowedAsync(string host, bool allowPrivateNetworks,
        IEnumerable<(IPAddress Network, int PrefixLength)>? allowedNetworks, string hostKind, CancellationToken ct)
    {
        if (allowPrivateNetworks) return;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Resolve(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new HttpRequestException($"{hostKind} host '{host}' did not resolve.");
        if (addresses.Any(a => IsBlocked(a, allowedNetworks)))
            throw new BlockedAddressException($"{hostKind} host '{host}' resolves to a private address.");
    }

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
    /// Primary handler that enforces the IP policy on every connection (resolved IP, so DNS rebinding is blocked too), disables
    /// redirects and cookies, and ignores the system proxy unless <paramref name="proxy"/> is given. <paramref name="hostKind"/>
    /// names the target in error messages ("Webhook", "JWKS"). With a proxy the proxy makes the connection, so the connect-time
    /// check cannot apply; callers must still run <see cref="EnsureHostAllowedAsync"/> first.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks, string hostKind,
        IReadOnlyCollection<(IPAddress Network, int PrefixLength)>? allowedNetworks = null, IWebProxy? proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = proxy is not null,
            Proxy = proxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (proxy is not null) return handler;
        handler.ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Resolve(host, ct).ConfigureAwait(false);
            if (!allowPrivateNetworks && addresses.Any(a => IsBlocked(a, allowedNetworks)))
                throw new BlockedAddressException($"{hostKind} host '{host}' resolves to a private address.");
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
            throw new HttpRequestException($"Could not connect to {hostKind.ToLowerInvariant()} host '{host}'.", last);
        };
        return handler;
    }
}
