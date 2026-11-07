using System.Net;
using System.Net.Sockets;

namespace A2A.Security.Http;

/// <summary>The target resolves to an address the policy forbids. Permanent: callers must not retry.</summary>
/// <param name="message">What was blocked.</param>
public sealed class BlockedAddressException(string message) : HttpRequestException(message);

/// <summary>
/// SSRF guard shared by outbound fetches to peer-chosen URLs: push webhooks (server) and <c>jku</c> key sets (Agent Card
/// verification). Blocks private, loopback, link-local, CGNAT, multicast and reserved ranges, including IPv4 embedded in IPv6
/// (mapped, NAT64, IPv4-compatible, 6to4), and checks the resolved address at connect time.
/// </summary>
public static class PrivateAddressGuard
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
        if (!IPAddress.TryParse(StripZone(uri.Host.Trim('[', ']')), out var ip)) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static string StripZone(string host)
    {
        var i = host.IndexOf('%');
        return i < 0 ? host : host[..i];
    }

    /// <summary>The host without one trailing dot (<c>localhost.</c> is <c>localhost</c>).</summary>
    public static string TrimHost(string host) => host.Length > 1 && host[^1] == '.' ? host[..^1] : host;

    /// <summary>
    /// True for an all-numeric host that is not a canonical dotted quad (octal <c>0177.0.0.1</c>, hex parts, short forms). Resolvers
    /// disagree on how to read these, so they are refused outright.
    /// </summary>
    public static bool IsAmbiguousNumericHost(string host)
    {
        var labels = TrimHost(host).Split('.');
        if (!labels.All(l => l.Length > 0 && (l.All(char.IsAsciiDigit) || (l.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && l.Skip(2).All(char.IsAsciiHexDigit)))))
            return false;
        return labels.Length != 4 || labels.Any(l => l.Length > 1 && l[0] == '0') || labels.Any(l => !l.All(char.IsAsciiDigit));
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
        IPAddress[] addresses;
        if (IPAddress.TryParse(StripZone(host.Trim('[', ']')), out var literal)) addresses = [literal];
        else
        {
            try
            {
                // WaitAsync: getaddrinfo is not cancellable on every platform, so the token must bound the wait itself.
                addresses = await Resolve(host, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new HttpRequestException($"{hostKind} host '{host}' did not resolve.", ex);
            }
        }
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
            // IPv4 hidden inside IPv6: judge the embedded address by the IPv4 rules.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
            {
                if (b[4] == 0 && b[5] == 0x01) return true;                         // 64:ff9b:1::/48 local-use NAT64
                if (b[4..12].All(x => x == 0)) return IsPrivate(new IPAddress(b[12..16])); // 64:ff9b::/96 NAT64
            }
            if (b[0] == 0x20 && b[1] == 0x02) return IsPrivate(new IPAddress(b[2..6]));    // 2002::/16 6to4
            if (b[..12].All(x => x == 0)) return IsPrivate(new IPAddress(b[12..16]));      // ::a.b.c.d IPv4-compatible
            return false;
        }
        return true;
    }

    /// <summary>
    /// Primary handler that enforces the IP policy on every direct connection (resolved IP, so DNS rebinding is blocked too),
    /// disables redirects and cookies, and ignores the system proxy unless <paramref name="proxy"/> is given. <paramref name="hostKind"/>
    /// names the target in error messages ("Webhook", "JWKS"). When a proxy handles a request, the connection to the proxy itself is
    /// not checked: egress policy for proxied requests is the proxy's job. Requests the proxy bypasses are checked like direct ones.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks, string hostKind,
        IReadOnlyCollection<(IPAddress Network, int PrefixLength)>? allowedNetworks = null, IWebProxy? proxy = null) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = proxy is not null,
        Proxy = proxy,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var target = ctx.InitialRequestMessage?.RequestUri;
            var viaProxy = false;
            if (proxy is not null && target is not null && !proxy.IsBypassed(target) && proxy.GetProxy(target) is { } hop)
                viaProxy = string.Equals(hop.IdnHost.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && hop.Port == ctx.DnsEndPoint.Port;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Resolve(host, ct).ConfigureAwait(false);
            if (!viaProxy && !allowPrivateNetworks && addresses.Any(a => IsBlocked(a, allowedNetworks)))
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
        },
    };
}
