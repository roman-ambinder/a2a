using System.Net;
using A2A.Client.Push;
using A2A.Net;
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

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
        var protector = new EphemeralPushSecretProtector();
        var store = new InMemoryPushConfigStore();
        var stored = A2A.Json.A2AJson.Clone(config);
        stored.Id = "c1";
        stored.TaskId = Key.TaskId;
        if (!string.IsNullOrEmpty(stored.Token)) stored.Token = protector.Protect(stored.Token);
        if (stored.Authentication is { Credentials.Length: > 0 } a) a.Credentials = protector.Protect(a.Credentials);
        await store.SaveAsync(new PushConfigRecord(Key, stored));

        var options = new A2AServerOptions();
        options.PushNotifications.InitialBackoff = TimeSpan.FromMilliseconds(5);
        configure(options.PushNotifications);
        var handler = new Scripted(codes.Length == 0 ? [HttpStatusCode.OK] : codes);
        var sender = new HttpPushNotificationSender(new Factory(handler), store, protector, Options.Create(options));
        sender.Enqueue(Key, new SequencedEvent(7, StreamResponse.Of(Message.FromUser("x"))));
        await sender.WhenIdleAsync();
        return (sender, handler);
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
    public void Obfuscated_private_targets_rejected(string url)
    {
        // The guard must see the normalised address, not the spelling the caller used.
        var ip = PrivateAddressGuard.TryGetLiteral(new Uri(url));
        Assert.NotNull(ip);
        Assert.True(PrivateAddressGuard.IsPrivate(ip!));
        if (url.Contains("0x7f") || url.Contains("2130706433")) Assert.Equal(IPAddress.Loopback, ip);
        if (url.Contains("::ffff:")) Assert.Equal(IPAddress.Loopback, ip);

        Assert.Equal(A2AErrorCode.InvalidParams, Assert.Throws<A2AException>(() => PushUrlValidator.Validate(url, new PushNotificationOptions())).Code);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("127.255.255.254")] [InlineData("10.9.9.9")] [InlineData("172.16.0.1")] [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")] [InlineData("169.254.1.1")] [InlineData("100.64.0.1")] [InlineData("100.127.255.255")] [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")] [InlineData("224.0.0.1")] [InlineData("239.255.255.255")] [InlineData("255.255.255.255")] [InlineData("::1")]
    [InlineData("::")] [InlineData("fc00::1")] [InlineData("fdff::1")] [InlineData("fe80::1")] [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")] [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:169.254.169.254")] [InlineData("::ffff:100.64.0.1")]
    public void Private_ranges_blocked(string ip) => Assert.True(PushUrlValidator.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("172.32.0.1")] [InlineData("100.128.0.1")] [InlineData("2001:4860:4860::8888")] [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_allowed(string ip) => Assert.False(PushUrlValidator.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task Hostname_resolving_private_rejected_at_delivery()
    {
        var original = PrivateAddressGuard.Resolve;
        try
        {
            var resolved = new[] { IPAddress.Parse("93.184.216.34") };
            PrivateAddressGuard.Resolve = (_, _) => ValueTask.FromResult(resolved);
            // Static validation at create time passes: the name resolves public.
            PushUrlValidator.Validate("https://evil.example/h", new PushNotificationOptions());

            resolved[0] = IPAddress.Parse("10.0.0.5"); // DNS answer changes before delivery
            var (sender, h) = await SendOneAsync(_ => { }, Hook("https://evil.example/h"));
            using var _ = sender;
            Assert.Empty(h.Requests);
        }
        finally { PrivateAddressGuard.Resolve = original; }
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
}
