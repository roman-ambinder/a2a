using System.Diagnostics;
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

    private static readonly TaskKey Key = new("", "alice", "t1");

    private static async Task<(HttpPushNotificationSender Sender, Scripted Handler)> SendOneAsync(
        Action<PushNotificationOptions> configure, TaskPushNotificationConfig config, params HttpStatusCode[] codes)
    {
        var handler = new Scripted(codes.Length == 0 ? [HttpStatusCode.OK] : codes);
        return (await SendWithAsync(handler, configure, config), handler);
    }

    private static async Task<HttpPushNotificationSender> SendWithAsync(HttpMessageHandler handler,
        Action<PushNotificationOptions> configure, TaskPushNotificationConfig config)
    {
        var protector = new EphemeralPushSecretProtector();
        var store = new InMemoryPushConfigStore();
        var stored = config.DeepCopy();
        stored.Id = "c1";
        stored.TaskId = Key.TaskId;
        if (!string.IsNullOrEmpty(stored.Token)) stored.Token = protector.Protect(stored.Token);
        if (stored.Authentication is { Credentials.Length: > 0 } a) a.Credentials = protector.Protect(a.Credentials);
        await store.SaveAsync(new PushConfigRecord(Key, stored));

        var options = new A2AServerOptions();
        options.PushNotifications.InitialBackoff = TimeSpan.FromMilliseconds(5);
        configure(options.PushNotifications);
        var sender = new HttpPushNotificationSender(new HttpMessageInvoker(handler, disposeHandler: false), store, protector, options);
        sender.Enqueue(Key, new SequencedEvent(7, StreamResponse.Of(Message.FromUser("x"))));
        await sender.WhenIdleAsync();
        return sender;
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

    /// <summary>Records each delivery (path, sequence); <see cref="Before"/> can hold request n (1-based) back.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<(string Path, long Seq)> _calls = [];
        private int _n;
        public Func<int, Task>? Before { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _n);
            if (Before is { } before) await before(n);
            var seq = long.Parse(request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single(), System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock) _calls.Add((request.RequestUri!.AbsolutePath, seq));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        public int Count { get { lock (_lock) return _calls.Count; } }
        public long[] Seqs(string path) { lock (_lock) return _calls.Where(c => c.Path == path).Select(c => c.Seq).ToArray(); }
    }

    /// <summary>A clock whose timers fire only when the test advances it.</summary>
    private sealed class TimerClock : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }
        public int ArmedTimers { get { lock (_lock) return _timers.Count(t => t.Due is not null); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<FakeTimer> due;
            lock (_lock)
            {
                _now += by;
                due = _timers.Where(t => t.Due <= _now).ToList();
                foreach (var t in due) t.Due = null;
            }
            foreach (var t in due) t.Fire();
        }

        private sealed class FakeTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._lock)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                    if (!clock._timers.Contains(this)) clock._timers.Add(this);
                }
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._lock) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static (HttpPushNotificationSender Sender, PushConfigService Configs) NewSender(HttpMessageHandler handler,
        Action<A2AServerOptions>? configure = null, TimeProvider? time = null)
    {
        var protector = new EphemeralPushSecretProtector();
        var store = new InMemoryPushConfigStore();
        var options = new A2AServerOptions();
        options.PushNotifications.AllowPrivateNetworks = true;
        options.PushNotifications.InitialBackoff = TimeSpan.FromMilliseconds(5);
        configure?.Invoke(options);
        var sender = new HttpPushNotificationSender(new HttpMessageInvoker(handler, disposeHandler: false), store, protector, options, time: time);
        return (sender, new PushConfigService(store, protector, options, sender));
    }

    private static SequencedEvent Evt(long seq) => new(seq, StreamResponse.Of(Message.FromUser("x")));

    private static Task AddHookAsync(PushConfigService configs, string id) =>
        configs.CreateAsync(Key, new TaskPushNotificationConfig { Id = id, TaskId = Key.TaskId, Url = $"https://hooks.example/{id}" }, default).AsTask();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition not met in 10 s");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task Config_added_mid_task_receives_later_events()
    {
        var atFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Recorder
        {
            Before = n => n switch
            {
                1 => Hold(atFirst, releaseFirst),
                2 => Hold(atSecond, releaseSecond),
                _ => Task.CompletedTask,
            },
        };
        var (sender, configs) = NewSender(h);
        using var _ = sender;
        await AddHookAsync(configs, "c1");

        sender.Enqueue(Key, Evt(1));
        await atFirst.Task.WaitAsync(TimeSpan.FromSeconds(10)); // event 1 is being delivered to c1
        sender.Enqueue(Key, Evt(2));
        sender.Enqueue(Key, Evt(3)); // 2 and 3 wait together: one batch, configs listed once
        releaseFirst.SetResult();
        await atSecond.Task.WaitAsync(TimeSpan.FromSeconds(10)); // event 2 is being delivered to c1
        await AddHookAsync(configs, "c2"); // added mid-batch: the listing is invalidated
        releaseSecond.SetResult();
        await sender.WhenIdleAsync();

        Assert.Equal([1L, 2L, 3L], h.Seqs("/c1"));
        Assert.Equal([3L], h.Seqs("/c2"));

        static async Task Hold(TaskCompletionSource at, TaskCompletionSource release)
        {
            at.TrySetResult();
            await release.Task;
        }
    }

    [Fact]
    public async Task Worker_exits_when_idle()
    {
        var clock = new TimerClock();
        var h = new Recorder();
        var (sender, configs) = NewSender(h, o => o.PushNotifications.WorkerIdleTimeout = TimeSpan.FromSeconds(30), clock);
        using var _ = sender;
        await AddHookAsync(configs, "c1");

        sender.Enqueue(Key, Evt(1));
        await sender.WhenIdleAsync();
        await WaitUntil(() => clock.ArmedTimers > 0); // the worker waits for its next event
        Assert.Equal(1, sender.ActiveWorkers);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, sender.ActiveWorkers); // not idle long enough yet
        clock.Advance(TimeSpan.FromSeconds(1)); // the idle timeout passes
        await WaitUntil(() => sender.ActiveWorkers == 0);

        sender.Enqueue(Key, Evt(2)); // a new worker starts on the next event
        await sender.WhenIdleAsync();
        Assert.Equal([1L, 2L], h.Seqs("/c1"));
        Assert.Equal(1, sender.ActiveWorkers);
    }

    [Fact]
    public async Task Events_keep_order_when_the_channel_is_full()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Recorder { Before = async _ => { await open.Task; await Task.Delay(2); } }; // slow webhook, closed at first
        var (sender, configs) = NewSender(h, o => o.Streaming.ChannelCapacity = 2);
        using var _ = sender;
        await AddHookAsync(configs, "c1");

        var enqueue = Task.Run(() => { for (var i = 1; i <= 20; i++) sender.Enqueue(Key, Evt(i)); });
        await enqueue.WaitAsync(TimeSpan.FromSeconds(5)); // the webhook is still closed: this times out if Enqueue waits for channel space
        Assert.Equal(0, h.Count);

        open.SetResult();
        await sender.WhenIdleAsync();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), h.Seqs("/c1"));
    }

    [Fact]
    public async Task Event_arriving_as_worker_exits_is_delivered()
    {
        var clock = new TimerClock();
        var h = new Recorder();
        var (sender, configs) = NewSender(h, o => o.PushNotifications.WorkerIdleTimeout = TimeSpan.FromSeconds(30), clock);
        using var _ = sender;
        await AddHookAsync(configs, "c1");

        const int n = 100;
        for (var i = 1; i <= n; i++)
        {
            if (i > 1)
            {
                await WaitUntil(() => clock.ArmedTimers > 0); // the worker waits for its next event
                clock.Advance(TimeSpan.FromSeconds(30)); // the idle timeout fires: the worker is deciding to exit
            }
            sender.Enqueue(Key, Evt(i)); // ... while this event arrives
            await WaitUntil(() => h.Count == i);
        }
        await sender.WhenIdleAsync();
        Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), h.Seqs("/c1"));
    }
}
