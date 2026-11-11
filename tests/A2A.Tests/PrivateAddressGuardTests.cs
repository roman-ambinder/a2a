using System.Net;
using System.Net.Sockets;
using A2A.Security.Http;

namespace A2A.Tests;

/// <summary>
///     <see cref="PrivateAddressGuard" />: the private-range table (IPv4, IPv6 and IPv4 inside IPv6), network matching for
///     allowed networks, the host-name checks, the resolve-time check and the connect-time check of the handler.
/// </summary>
/// <remarks>Joins the PushGuard collection: some tests replace the static DNS seam.</remarks>
[Collection("PushGuard")]
public class PrivateAddressGuardTests
{
    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    // ---- IsPrivate: IPv4 ----

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("0.1.2.3", true)]
    [InlineData("10.0.0.0", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("11.0.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("100.63.255.255", false)] // CGNAT is 100.64.0.0/10
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]
    [InlineData("169.254.169.254", true)] // link-local, cloud metadata
    [InlineData("169.253.0.1", false)]
    [InlineData("169.255.0.1", false)]
    [InlineData("172.15.255.255", false)] // 172.16.0.0/12
    [InlineData("172.16.0.0", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.0", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.167.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("192.0.0.8", true)]       // 192.0.0.0/24
    [InlineData("192.0.1.1", false)]
    [InlineData("192.1.0.1", false)]
    [InlineData("192.0.2.1", false)]      // TEST-NET-1 is not blocked
    [InlineData("198.17.255.255", false)] // benchmarking is 198.18.0.0/15
    [InlineData("198.18.0.0", true)]
    [InlineData("198.19.255.255", true)]
    [InlineData("198.20.0.0", false)]
    [InlineData("223.255.255.255", false)]
    [InlineData("224.0.0.1", true)]       // multicast
    [InlineData("240.0.0.1", true)]       // reserved
    [InlineData("255.255.255.255", true)] // broadcast
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.215.14", false)]
    public void Ipv4_ranges(string address, bool expected) => Assert.Equal(expected, PrivateAddressGuard.IsPrivate(Ip(address)));

    // ---- IsPrivate: IPv6, and IPv4 inside IPv6 ----

    [Theory]
    [InlineData("::", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]          // link-local
    [InlineData("fec0::1", true)]          // site-local
    [InlineData("ff02::1", true)]          // multicast
    [InlineData("fc00::1", true)]          // unique local fc00::/7
    [InlineData("fdff:ffff::1", true)]
    [InlineData("fe00::1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:10.0.0.1", true)]  // IPv4-mapped
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("::a00:1", true)]          // IPv4-compatible ::10.0.0.1
    [InlineData("::808:808", false)]       // ::8.8.8.8
    [InlineData("64:ff9b::7f00:1", true)]  // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::808:808", false)] // NAT64 of 8.8.8.8
    [InlineData("64:ff9b:1::808:808", true)] // local-use NAT64 64:ff9b:1::/48
    [InlineData("64:ff9b:0:0:0:1:7f00:1", false)] // not 64:ff9b::/96: bytes 4..11 are not zero
    [InlineData("2002:7f00:1::1", true)]   // 6to4 of 127.0.0.1
    [InlineData("2002:808:808::1", false)] // 6to4 of 8.8.8.8
    // Near misses of the NAT64 and 6to4 prefixes, with a private IPv4 address where those would carry one.
    [InlineData("0:9b::7f00:1", false)]
    [InlineData("0:ff9b::7f00:1", false)]
    [InlineData("1::7f00:1", false)]
    [InlineData("64:aa9b::7f00:1", false)]
    [InlineData("2001:7f00:1::1", false)]
    [InlineData("2102:7f00:1::1", false)]
    public void Ipv6_ranges(string address, bool expected) => Assert.Equal(expected, PrivateAddressGuard.IsPrivate(Ip(address)));

    // ---- InNetwork ----

    [Theory]
    [InlineData("10.1.2.3", "10.0.0.0", 8, true)]
    [InlineData("11.1.2.3", "10.0.0.0", 8, false)]
    [InlineData("1.2.3.4", "0.0.0.0", 0, true)]
    [InlineData("10.1.2.3", "10.1.2.3", 32, true)]
    [InlineData("10.1.2.4", "10.1.2.3", 32, false)]
    [InlineData("10.1.2.3", "10.1.2.3", 33, false)] // longer than the address
    [InlineData("10.1.2.3", "10.1.2.3", -1, false)]
    [InlineData("10.128.0.1", "10.128.0.0", 9, true)] // prefix inside a byte
    [InlineData("10.127.0.1", "10.128.0.0", 9, false)]
    [InlineData("10.255.0.1", "10.128.0.0", 9, true)]
    [InlineData("172.31.255.255", "172.16.0.0", 12, true)]
    [InlineData("172.32.0.0", "172.16.0.0", 12, false)]
    [InlineData("172.15.255.255", "172.16.0.0", 12, false)]
    [InlineData("192.168.1.130", "192.168.1.128", 25, true)]
    [InlineData("192.168.1.127", "192.168.1.128", 25, false)]
    [InlineData("10.0.0.1", "::", 0, false)]          // families differ
    [InlineData("::ffff:10.0.0.1", "10.0.0.0", 8, true)] // mapped address is read as IPv4
    [InlineData("10.0.0.1", "::ffff:10.0.0.0", 8, true)] // and so is a mapped network
    [InlineData("fd00::1", "fc00::", 7, true)]
    [InlineData("fe00::1", "fc00::", 7, false)]
    [InlineData("2001:db8::1", "2001:db8::", 32, true)]
    [InlineData("2001:db9::1", "2001:db8::", 32, false)]
    [InlineData("2001:db8::1", "2001:db8::1", 128, true)]
    [InlineData("2001:db8::1", "2001:db8::1", 129, false)]
    public void In_network(string address, string network, int prefix, bool expected) =>
        Assert.Equal(expected, PrivateAddressGuard.InNetwork(Ip(address), Ip(network), prefix));

    [Fact]
    public void Blocked_means_private_and_not_in_an_allowed_network()
    {
        (IPAddress, int)[] allowed = [(Ip("10.1.0.0"), 16)];

        Assert.True(PrivateAddressGuard.IsBlocked(Ip("10.2.0.1"), allowed));
        Assert.False(PrivateAddressGuard.IsBlocked(Ip("10.1.0.1"), allowed));
        Assert.True(PrivateAddressGuard.IsBlocked(Ip("10.1.0.1"), null));
        Assert.False(PrivateAddressGuard.IsBlocked(Ip("8.8.8.8"), null));
    }

    // ---- host names ----

    [Theory]
    [InlineData("localhost.", "localhost")]
    [InlineData("example.com", "example.com")]
    [InlineData("a..", "a.")]  // one dot only
    [InlineData(".", ".")]     // a lone dot is kept
    [InlineData("a", "a")]
    public void Trim_host_drops_one_trailing_dot(string host, string expected) =>
        Assert.Equal(expected, PrivateAddressGuard.TrimHost(host));

    [Theory]
    [InlineData("1.2.3.4", false)]
    [InlineData("10.0.0.1", false)]     // a zero label alone is canonical
    [InlineData("0.0.0.0", false)]
    [InlineData("1.2.3.4.", false)]     // trailing dot
    [InlineData("example.com", false)]
    [InlineData("1.example.com", false)]
    [InlineData("1a.2.3.4", false)]     // a label that is not a number
    [InlineData("0xfz.0.0.1", false)]   // not hex either
    [InlineData("1.2..", false)]        // an empty label
    [InlineData("1.2.3", true)]         // short form
    [InlineData("1.2.3.4.5", true)]
    [InlineData("2130706433", true)]    // decimal 127.0.0.1
    [InlineData("0177.0.0.1", true)]    // octal
    [InlineData("1.2.3.04", true)]
    [InlineData("0x7f.0.0.1", true)]    // hex label
    [InlineData("0X7F.0.0.1", true)]
    [InlineData("0x7f000001", true)]
    public void Ambiguous_numeric_hosts(string host, bool expected) =>
        Assert.Equal(expected, PrivateAddressGuard.IsAmbiguousNumericHost(host));

    [Theory]
    [InlineData("https://10.0.0.1/a", "10.0.0.1")]
    [InlineData("https://[::1]/a", "::1")]
    [InlineData("https://[::ffff:127.0.0.1]/a", "127.0.0.1")] // unmapped to IPv4
    [InlineData("https://[2001:db8::1]/a", "2001:db8::1")]
    public void Literal_of_an_ip_host(string url, string expected) =>
        Assert.Equal(Ip(expected), PrivateAddressGuard.TryGetLiteral(new Uri(url)));

    [Fact]
    public void Literal_keeps_the_address_family()
    {
        Assert.Equal(AddressFamily.InterNetworkV6, PrivateAddressGuard.TryGetLiteral(new Uri("https://[::1]/a"))!.AddressFamily);
        Assert.Equal(AddressFamily.InterNetwork, PrivateAddressGuard.TryGetLiteral(new Uri("https://[::ffff:10.0.0.1]/a"))!.AddressFamily);
    }

    [Fact]
    public void Dns_name_has_no_literal() => Assert.Null(PrivateAddressGuard.TryGetLiteral(new Uri("https://example.com/a")));

    // ---- EnsureHostAllowedAsync ----

    private static async Task WithResolver(Func<string, IPAddress[]> resolve, Func<Task> test)
    {
        var original = PrivateAddressGuard.Resolve;
        PrivateAddressGuard.Resolve = (host, _) => ValueTask.FromResult(resolve(host));
        try { await test(); }
        finally { PrivateAddressGuard.Resolve = original; }
    }

    private static ValueTask Ensure(string host, IEnumerable<(IPAddress, int)>? allowed = null, bool allowPrivate = false) =>
        PrivateAddressGuard.EnsureHostAllowedAsync(host, allowPrivate, allowed, "Webhook", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Private_networks_allowed_skips_the_lookup() =>
        await WithResolver(_ => throw new InvalidOperationException("resolved"), () => Ensure("hooks.example", allowPrivate: true).AsTask());

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[fe80::1%25eth0]")]
    public async Task Private_literal_is_blocked_without_a_lookup(string host) =>
        await WithResolver(_ => throw new InvalidOperationException("resolved"), async () =>
        {
            var ex = await Assert.ThrowsAsync<BlockedAddressException>(() => Ensure(host).AsTask());
            Assert.Equal($"Webhook host '{host}' resolves to a private address.", ex.Message);
        });

    [Fact]
    public async Task Name_with_one_private_address_among_public_ones_is_blocked() =>
        await WithResolver(_ => [Ip("93.184.215.14"), Ip("10.0.0.1")], async () =>
        {
            var ex = await Assert.ThrowsAsync<BlockedAddressException>(() => Ensure("hooks.example").AsTask());
            Assert.Equal("Webhook host 'hooks.example' resolves to a private address.", ex.Message);
        });

    [Fact]
    public async Task Name_with_only_public_addresses_is_allowed() =>
        await WithResolver(_ => [Ip("93.184.215.14"), Ip("2001:4860:4860::8888")], () => Ensure("hooks.example").AsTask());

    [Fact]
    public async Task Private_address_in_an_allowed_network_is_allowed() =>
        await WithResolver(_ => [Ip("10.1.2.3")], () => Ensure("files.internal", [(Ip("10.1.0.0"), 16)]).AsTask());

    [Fact]
    public async Task Name_with_no_addresses_did_not_resolve() =>
        await WithResolver(_ => [], async () =>
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Ensure("hooks.example").AsTask());
            Assert.Equal("Webhook host 'hooks.example' did not resolve.", ex.Message);
            Assert.Null(ex.InnerException);
        });

    [Fact]
    public async Task Lookup_socket_error_did_not_resolve() =>
        await WithResolver(_ => throw new SocketException((int)SocketError.HostNotFound), async () =>
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Ensure("missing.example").AsTask());
            Assert.Equal("Webhook host 'missing.example' did not resolve.", ex.Message);
            Assert.IsType<SocketException>(ex.InnerException);
        });

    [Fact]
    public async Task Lookup_argument_error_did_not_resolve() =>
        await WithResolver(_ => throw new ArgumentException("bad name"), async () =>
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Ensure("bad..example").AsTask());
            Assert.Equal("Webhook host 'bad..example' did not resolve.", ex.Message);
            Assert.IsType<ArgumentException>(ex.InnerException);
        });

    // ---- CreateHandler: the connect-time check ----

    private static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    [Fact]
    public async Task Handler_blocks_a_name_with_one_private_address() =>
        await WithResolver(_ => [Ip("192.0.2.1"), Ip("127.0.0.1")], async () =>
        {
            using var http = new HttpClient(PrivateAddressGuard.CreateHandler(false, "Webhook")) { Timeout = TimeSpan.FromSeconds(10) };
            var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                http.GetAsync("http://hooks.example:1/", TestContext.Current.CancellationToken));
            Assert.Contains("Webhook host 'hooks.example' resolves to a private address.", Messages(ex));
        });

    [Fact]
    public async Task Handler_names_the_host_it_could_not_connect_to() =>
        // Private networks allowed, so the guard lets it through and the closed port fails the connect.
        await WithResolver(_ => [Ip("127.0.0.1")], async () =>
        {
            using var http = new HttpClient(PrivateAddressGuard.CreateHandler(true, "Webhook")) { Timeout = TimeSpan.FromSeconds(10) };
            var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                http.GetAsync("http://local.test:1/", TestContext.Current.CancellationToken));
            Assert.Contains("Could not connect to webhook host 'local.test'.", Messages(ex));
            Assert.DoesNotContain("private address", Messages(ex));
        });

    [Fact]
    public void Handler_does_not_follow_redirects_or_keep_cookies()
    {
        using var handler = PrivateAddressGuard.CreateHandler(false, "Webhook");
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void Handler_uses_only_the_proxy_it_is_given()
    {
        var proxy = new WebProxy("http://proxy.example:3128");
        using var handler = PrivateAddressGuard.CreateHandler(false, "Webhook", proxy: proxy);
        Assert.True(handler.UseProxy);
        Assert.Same(proxy, handler.Proxy);
    }
}
