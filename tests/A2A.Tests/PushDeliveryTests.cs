using System.Net;
using A2A.Client.Push;
using A2A.Security.Http;
using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

[CollectionDefinition("PushGuard", DisableParallelization = true)]
public sealed class PushGuardCollection;

[Collection("PushGuard")]
public class PushDeliveryTests
{
    private sealed class Scripted(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        private int _i;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(codes[Math.Min(_i++, codes.Length - 1)]));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task<(PushDeliveryService Sender, Scripted Handler)> SendOneAsync(
        Action<PushNotificationOptions> configure, TaskPushNotificationConfig config, params HttpStatusCode[] codes)
    {
        var handler = new Scripted(codes.Length == 0 ? [HttpStatusCode.OK] : codes);
        return (await SendWithAsync(handler, configure, config), handler);
    }

    /// <summary>Delivers event 7 of a task to <paramref name="config"/> through the outbox, retries included.</summary>
    private static async Task<PushDeliveryService> SendWithAsync(HttpMessageHandler handler,
        Action<PushNotificationOptions> configure, TaskPushNotificationConfig config)
    {
        var rig = new PushRig(handler);
        rig.Options.PushNotifications.AllowPrivateNetworks = false; // the defaults; tests opt out
        configure(rig.Options.PushNotifications);
        await rig.SaveConfigAsync(config);
        for (var i = 1; i < 7; i++) await rig.CommitAsync();
        await rig.CommitAsync("c1");
        var service = rig.NewService();
        await rig.DrainAsync(service);
        return service;
    }

    private static TaskPushNotificationConfig Hook(string url = "https://hooks.example/cb", string? token = "s3cret") =>
        new() { Url = url, Token = token };

    [Fact]
    public async Task Bearer_and_token_headers_both_sent()
    {
        var (sender, h) = await SendOneAsync(o => o.AllowPrivateNetworks = true, Hook());
        using var _ = sender;
        var req = Assert.Single(h.Requests);
        Assert.Equal("Bearer s3cret", req.Headers.Authorization!.ToString());
        Assert.Equal("s3cret", req.Headers.GetValues(A2AProtocol.Headers.NotificationToken).Single());
        Assert.Equal("7", req.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single());
        Assert.Matches("^[0-9a-f]{32}$", req.Headers.GetValues(A2AProtocol.Headers.DeliveryId).Single());
    }

    [Fact]
    public async Task Webhook_request_carries_protocol_version()
    {
        var (sender, h) = await SendOneAsync(o => o.AllowPrivateNetworks = true, Hook());
        using var _ = sender;
        Assert.Equal(A2AProtocol.Version, Assert.Single(h.Requests).Headers.GetValues(A2AProtocol.Headers.Version).Single());
    }

    [Fact]
    public async Task Explicit_authentication_wins_over_bearer_token()
    {
        var cfg = Hook();
        cfg.Authentication = new AuthenticationInfo { Scheme = "Basic", Credentials = "abc" };
        var (sender, h) = await SendOneAsync(o => o.AllowPrivateNetworks = true, cfg);
        using var _ = sender;
        Assert.Equal("Basic abc", Assert.Single(h.Requests).Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Delivery_id_stable_across_retries()
    {
        var (sender, h) = await SendOneAsync(o => o.AllowPrivateNetworks = true, Hook(), HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        using var _ = sender;
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(h.Requests[0].Headers.GetValues(A2AProtocol.Headers.DeliveryId).Single(),
            h.Requests[1].Headers.GetValues(A2AProtocol.Headers.DeliveryId).Single());
    }

    [Theory]
    [InlineData("https://[::ffff:127.0.0.1]/h")]
    [InlineData("https://0x7f000001/h")]
    [InlineData("https://2130706433/h")]
    [InlineData("https://[fd00::1]/h")]
    [InlineData("https://100.64.0.1/h")]
    [InlineData("https://169.254.169.254/h")]
    [InlineData("https://0177.0.0.1/h")]
    [InlineData("https://[fe80::1%25eth0]/h")]
    [InlineData("https://localhost./h")]
    [InlineData("https://[64:ff9b::7f00:1]/h")]
    [InlineData("https://[2002:7f00:1::]/h")]
    public void Obfuscated_private_targets_rejected(string url)
    {
        // The guard must see the normalised address, not the spelling the caller used.
        var ip = PrivateAddressGuard.TryGetLiteral(new Uri(url));
        if (ip is null) Assert.True(url.Contains("0177") || url.Contains("localhost."), "only name-like hosts may skip the literal path");
        else Assert.True(PrivateAddressGuard.IsPrivate(ip));
        if (url.Contains("0x7f") || url.Contains("2130706433")) Assert.Equal(IPAddress.Loopback, ip);
        if (url.Contains("::ffff:")) Assert.Equal(IPAddress.Loopback, ip);

        Assert.Equal(A2AErrorCode.InvalidParams, Assert.Throws<A2AException>(() => PushUrlValidator.Validate(url, new PushNotificationOptions())).Code);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("127.255.255.254")] [InlineData("10.9.9.9")] [InlineData("172.16.0.1")] [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")] [InlineData("169.254.1.1")] [InlineData("100.64.0.1")] [InlineData("100.127.255.255")] [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")] [InlineData("224.0.0.1")] [InlineData("239.255.255.255")] [InlineData("255.255.255.255")] [InlineData("::1")]
    [InlineData("::")] [InlineData("fc00::1")] [InlineData("fdff::1")] [InlineData("fe80::1")] [InlineData("ff02::1")]
    [InlineData("64:ff9b::7f00:1")] [InlineData("64:ff9b::a9fe:a9fe")] [InlineData("64:ff9b:1::1")] [InlineData("::7f00:1")] [InlineData("::a00:1")]
    [InlineData("2002:7f00:1::")] [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("::ffff:10.0.0.1")] [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:169.254.169.254")] [InlineData("::ffff:100.64.0.1")]
    public void Private_ranges_blocked(string ip) => Assert.True(PushUrlValidator.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("172.32.0.1")] [InlineData("100.128.0.1")] [InlineData("2001:4860:4860::8888")] [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")] [InlineData("2002:808:808::")]
    public void Public_addresses_allowed(string ip) => Assert.False(PushUrlValidator.IsPrivate(IPAddress.Parse(ip)));

    private static async Task WithResolver(Func<string, IPAddress[]> resolve, Func<Task> body)
    {
        var original = PrivateAddressGuard.Resolve;
        try
        {
            PrivateAddressGuard.Resolve = (h, _) => ValueTask.FromResult(resolve(h));
            await body();
        }
        finally { PrivateAddressGuard.Resolve = original; }
    }

    [Fact]
    public async Task Hostname_resolving_private_rejected_at_delivery()
    {
        var resolved = new[] { IPAddress.Parse("93.184.216.34") };
        await WithResolver(_ => resolved, async () =>
        {
            // Accepted at create while the name resolves public.
            await PushUrlValidator.ValidateAsync("https://evil.example/h", new PushNotificationOptions());
            resolved[0] = IPAddress.Parse("10.0.0.5"); // DNS answer changes before delivery
            var (sender, h) = await SendOneAsync(_ => { }, Hook("https://evil.example/h"));
            using var _ = sender;
            Assert.Empty(h.Requests);
        });
    }

    [Fact]
    public async Task Create_time_validation_resolves_dns_and_uses_one_message()
    {
        var opts = new PushNotificationOptions();
        await WithResolver(h => h == "gone.example" ? throw new System.Net.Sockets.SocketException() : [IPAddress.Parse("10.0.0.5")], async () =>
        {
            var priv = await Assert.ThrowsAsync<A2AException>(() => PushUrlValidator.ValidateAsync("https://evil.example/h", opts).AsTask());
            var gone = await Assert.ThrowsAsync<A2AException>(() => PushUrlValidator.ValidateAsync("https://gone.example/h", opts).AsTask());
            Assert.Equal(A2AErrorCode.InvalidParams, priv.Code);
            Assert.Equal(A2AErrorCode.InvalidParams, gone.Code);
            Assert.Equal(PushUrlValidator.UrlNotAllowed, priv.Message);
            Assert.Equal(priv.Message, gone.Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_create_paths_validate_dns(bool inline)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.AllowPrivateNetworks = false);
        await WithResolver(_ => [IPAddress.Parse("10.0.0.5")], async () =>
        {
            var client = host.Client(ProtocolBindings.JsonRpc, "alice");
            var cfg = new TaskPushNotificationConfig { Url = "https://evil.example/cb" };
            A2AException ex;
            if (inline)
                ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Message.FromUser("x"), new SendMessageConfiguration { TaskPushNotificationConfig = cfg }));
            else
            {
                var task = (await client.SendMessageAsync("input")).RequireTask();
                cfg.TaskId = task.Id;
                ex = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(cfg));
            }
            Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
            Assert.Contains(PushUrlValidator.UrlNotAllowed, ex.Message);
        });
    }

    [Fact]
    public async Task Hostname_resolving_public_is_delivered()
    {
        var original = PrivateAddressGuard.Resolve;
        try
        {
            PrivateAddressGuard.Resolve = (_, _) => ValueTask.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
            var (sender, h) = await SendOneAsync(_ => { }, Hook("https://good.example/h"));
            using var _ = sender;
            Assert.Single(h.Requests);
        }
        finally { PrivateAddressGuard.Resolve = original; }
    }

    [Fact]
    public async Task Allowed_network_bypasses_block()
    {
        var options = new PushNotificationOptions();
        Assert.Throws<A2AException>(() => PushUrlValidator.Validate("https://10.0.0.5/h", options));
        options.AllowedNetworks.Add((IPAddress.Parse("10.0.0.0"), 8));
        PushUrlValidator.Validate("https://10.0.0.5/h", options);
        Assert.Throws<A2AException>(() => PushUrlValidator.Validate("https://192.168.0.5/h", options));

        var (sender, h) = await SendOneAsync(o => o.AllowedNetworks.Add((IPAddress.Parse("10.0.0.0"), 8)), Hook("https://10.0.0.5/h"));
        using var _ = sender;
        Assert.Single(h.Requests);
    }

    [Fact]
    public void Receiver_accepts_either_header_and_dedupes()
    {
        var r = new PushNotificationReceiver();
        var viaToken = new Dictionary<string, string> { ["x-a2a-notification-token"] = "s3cret", [A2AProtocol.Headers.DeliveryId] = "d1" };
        Assert.Equal(PushValidationResult.Valid, r.Validate(viaToken, "s3cret"));
        Assert.Equal(PushValidationResult.Duplicate, r.Validate(viaToken, "s3cret"));
        var viaBearer = new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret", [A2AProtocol.Headers.DeliveryId] = "d2" };
        Assert.Equal(PushValidationResult.Valid, r.Validate(viaBearer, "s3cret"));
    }

    [Fact]
    public void Receiver_forgets_delivery_ids_after_ten_minutes()
    {
        var clock = new ManualClock();
        var r = new PushNotificationReceiver(clock);
        var h = new Dictionary<string, string> { [A2AProtocol.Headers.NotificationToken] = "t", [A2AProtocol.Headers.DeliveryId] = "d" };
        Assert.Equal(PushValidationResult.Valid, r.Validate(h, "t"));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(PushValidationResult.Duplicate, r.Validate(h, "t"));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PushValidationResult.Valid, r.Validate(h, "t"));
    }

    [Fact]
    public void Receiver_rejects_wrong_token()
    {
        var r = new PushNotificationReceiver();
        Assert.Equal(PushValidationResult.InvalidToken, r.Validate(new Dictionary<string, string> { [A2AProtocol.Headers.NotificationToken] = "nope" }, "s3cret"));
        Assert.Equal(PushValidationResult.InvalidToken, r.Validate(new Dictionary<string, string>(), "s3cret"));
        // A rejected delivery must not burn its id.
        var h = new Dictionary<string, string> { [A2AProtocol.Headers.NotificationToken] = "bad", [A2AProtocol.Headers.DeliveryId] = "d" };
        Assert.Equal(PushValidationResult.InvalidToken, r.Validate(h, "s3cret"));
        h[A2AProtocol.Headers.NotificationToken] = "s3cret";
        Assert.Equal(PushValidationResult.Valid, r.Validate(h, "s3cret"));
    }

    [Fact]
    public void Push_handler_uses_no_proxy_cookies_or_redirects()
    {
        using var h = PushUrlValidator.CreateHandler(new PushNotificationOptions());
        Assert.False(h.UseProxy);
        Assert.False(h.UseCookies);
        Assert.False(h.AllowAutoRedirect);
        var proxy = new WebProxy("http://proxy.example:3128");
        using var withProxy = PushUrlValidator.CreateHandler(new PushNotificationOptions { Proxy = proxy });
        Assert.True(withProxy.UseProxy);
        Assert.Same(proxy, withProxy.Proxy);
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _l = new(IPAddress.Loopback, 0);
        public int Accepted;
        public LocalServer()
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await _l.AcceptTcpClientAsync();
                        Interlocked.Increment(ref Accepted);
                        var stream = c.GetStream();
                        var buf = new byte[4096];
                        _ = await stream.ReadAsync(buf);
                        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                    }
                }
                catch { /* closed */ }
            });
        }
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public void Dispose() => _l.Stop();
    }

    private sealed class BypassingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }

    [Fact]
    public async Task Connect_callback_blocks_private_resolution_without_connecting()
    {
        using var server = new LocalServer();
        await WithResolver(_ => [IPAddress.Loopback], async () =>
        {
            using var http = new HttpClient(PushUrlValidator.CreateHandler(new PushNotificationOptions()));
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://evil.example:{server.Port}/"));
            Assert.IsType<BlockedAddressException>(ex.InnerException ?? ex);
            await Task.Delay(100);
            Assert.Equal(0, server.Accepted);
        });
    }

    [Fact]
    public async Task Connect_callback_delivers_into_allowed_network()
    {
        using var server = new LocalServer();
        var opts = new PushNotificationOptions();
        opts.AllowedNetworks.Add((IPAddress.Parse("127.0.0.0"), 8));
        await WithResolver(_ => [IPAddress.Loopback], async () =>
        {
            using var http = new HttpClient(PushUrlValidator.CreateHandler(opts));
            using var resp = await http.GetAsync($"http://evil.example:{server.Port}/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(1, server.Accepted);
        });
    }

    [Fact]
    public async Task Proxy_bypassed_host_still_gets_ip_check()
    {
        using var server = new LocalServer();
        var opts = new PushNotificationOptions { Proxy = new BypassingProxy() };
        await WithResolver(_ => [IPAddress.Loopback], async () =>
        {
            using var http = new HttpClient(PushUrlValidator.CreateHandler(opts));
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://evil.example:{server.Port}/"));
            Assert.IsType<BlockedAddressException>(ex.InnerException ?? ex);
            Assert.Equal(0, server.Accepted);
        });
    }

    [Fact]
    public async Task Connect_time_block_is_not_retried()
    {
        using var server = new LocalServer();
        var calls = 0;
        await WithResolver(_ => Interlocked.Increment(ref calls) == 1 ? [IPAddress.Parse("93.184.216.34")] : [IPAddress.Loopback], async () =>
        {
            // First lookup (pre-delivery check) is public; the connect-time lookup is private (rebinding).
            using var handler = PushUrlValidator.CreateHandler(new PushNotificationOptions());
            using var sender = await SendWithAsync(handler, o => o.AllowInsecureUrls = true, Hook($"http://good.example:{server.Port}/h"));
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(0, server.Accepted);
        });
    }

    [Fact]
    public async Task Create_time_dns_is_bounded_by_a_timeout()
    {
        var original = PrivateAddressGuard.Resolve;
        try
        {
            PrivateAddressGuard.Resolve = (_, _) => new ValueTask<IPAddress[]>(new TaskCompletionSource<IPAddress[]>().Task); // never completes
            var opts = new PushNotificationOptions { Timeout = TimeSpan.FromMilliseconds(200) };
            var ex = await Assert.ThrowsAsync<A2AException>(() => PushUrlValidator.ValidateAsync("https://slow.example/h", opts).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(PushUrlValidator.UrlNotAllowed, ex.Message);

            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PushUrlValidator.ValidateAsync("https://slow.example/h", new PushNotificationOptions(), caller.Token).AsTask());
        }
        finally { PrivateAddressGuard.Resolve = original; }
    }

    [Fact]
    public async Task Overlong_host_is_rejected_with_generic_text()
    {
        // Real resolver: Dns.GetHostAddressesAsync throws ArgumentOutOfRangeException for hosts over 255 chars.
        var host = string.Join('.', Enumerable.Repeat(new string('a', 60), 5));
        var ex = await Assert.ThrowsAsync<A2AException>(() => PushUrlValidator.ValidateAsync($"https://{host}/h", new PushNotificationOptions()).AsTask());
        Assert.Equal(PushUrlValidator.UrlNotAllowed, ex.Message);
    }

    [Fact]
    public async Task Non_bypassed_proxy_hop_is_allowed_even_on_loopback()
    {
        using var proxyServer = new LocalServer();
        var opts = new PushNotificationOptions { Proxy = new WebProxy($"http://127.0.0.1:{proxyServer.Port}") };
        using var http = new HttpClient(PushUrlValidator.CreateHandler(opts));
        using var resp = await http.GetAsync("http://evil.example/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, proxyServer.Accepted);
    }
}
