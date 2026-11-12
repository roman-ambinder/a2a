using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using A2A.Security.Http;
using A2A.Server.Push;

namespace A2A.Tests;

/// <summary>PushNotificationOptions.PinProxyTarget: proxied deliveries go to the address the SDK checked.</summary>
public partial class PushDeliveryTests
{
    /// <summary>
    ///     Loopback proxy that records what it is asked for. A plain (absolute-form) request is answered at once. A
    ///     <c>CONNECT</c> is answered 200 and the tunnel is served by a TLS endpoint with a self-signed certificate for
    ///     the name given, which records the server name (SNI) of the ClientHello and the Host header inside the tunnel.
    /// </summary>
    private sealed class RecordingProxy : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public List<string> RequestLines { get; } = [];
        public List<string> HostHeaders { get; } = [];
        public List<string> TlsServerNames { get; } = [];
        public List<string> TunneledHostHeaders { get; } = [];
        public X509Certificate2? Certificate { get; }

        public RecordingProxy(string? certName = null)
        {
            if (certName is not null)
            {
                using var key = ECDsa.Create();
                var req = new CertificateRequest($"CN={certName}", key, HashAlgorithmName.SHA256);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(certName);
                req.CertificateExtensions.Add(san.Build());
                using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                Certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
            }

            _l.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var c = await _l.AcceptTcpClientAsync();
                        try { await ServeAsync(c.GetStream()); }
                        catch { /* one bad connection */ }
                    }
                }
                catch { /* closed */ }
            });
        }

        public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}");

        private async Task ServeAsync(Stream stream)
        {
            var (line, host) = await ReadHeadAsync(stream);
            lock (this)
            {
                RequestLines.Add(line);
                HostHeaders.Add(host);
            }

            if (!line.StartsWith("CONNECT ", StringComparison.Ordinal) || Certificate is null)
            {
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                return;
            }

            await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
            await using var tls = new SslStream(stream);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate });
            lock (this) TlsServerNames.Add(tls.TargetHostName);
            var (_, innerHost) = await ReadHeadAsync(tls);
            lock (this) TunneledHostHeaders.Add(innerHost);
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            await tls.FlushAsync();
        }

        /// <summary>Reads up to the blank line; returns the request line and the Host header.</summary>
        private static async Task<(string Line, string Host)> ReadHeadAsync(Stream stream)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (head.Count < 16384 && await stream.ReadAsync(one) == 1)
            {
                head.Add(one[0]);
                if (head.Count >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') break;
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..]
                .Trim() ?? "";
            return (lines[0], host);
        }

        public void Dispose()
        {
            _l.Stop();
            Certificate?.Dispose();
        }
    }

    /// <summary>The first lookup of a name answers public, every later one private (DNS rebinding).</summary>
    private static Func<string, IPAddress[]> Rebinding(List<string> lookups) => h =>
    {
        lock (lookups)
        {
            lookups.Add(h);
            return lookups.Count(x => x == h) == 1
                ? [IPAddress.Parse("93.184.216.34")]
                : [IPAddress.Parse("169.254.169.254")];
        }
    };

    private static Task<PushDeliveryService> SendPinnedAsync(SocketsHttpHandler handler, IWebProxy proxy, string url) =>
        SendWithAsync(handler, o =>
        {
            o.AllowInsecureUrls = true;
            o.Proxy = proxy;
            o.PinProxyTarget = true;
        }, Hook(url));

    [Fact]
    public async Task Pinned_proxy_target_sends_the_validated_address_with_the_original_host()
    {
        using var proxy = new RecordingProxy();
        var lookups = new List<string>();
        await WithResolver(Rebinding(lookups), async () =>
        {
            var opts = new PushNotificationOptions { Proxy = new WebProxy(proxy.Address), PinProxyTarget = true };
            using var handler = PushUrlValidator.CreateHandler(opts);
            using var sender = await SendPinnedAsync(handler, opts.Proxy, "http://rebind.example:8080/h?x=1");
            lock (proxy)
            {
                // The proxy is asked for the address the SDK checked, not for the name it would look up again.
                Assert.Equal(["POST http://93.184.216.34:8080/h?x=1 HTTP/1.1"], proxy.RequestLines);
                Assert.Equal(["rebind.example:8080"], proxy.HostHeaders);
            }

            lock (lookups) Assert.Equal(["rebind.example"], lookups); // one lookup, before delivery
        });
    }

    [Fact]
    public async Task Pinned_proxy_target_keeps_the_default_port_out_of_the_host_header()
    {
        using var proxy = new RecordingProxy();
        await WithResolver(_ => [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("93.184.216.34")], async () =>
        {
            var opts = new PushNotificationOptions { Proxy = new WebProxy(proxy.Address), PinProxyTarget = true };
            using var handler = PushUrlValidator.CreateHandler(opts);
            using var sender = await SendPinnedAsync(handler, opts.Proxy, "http://rebind.example/h");
            lock (proxy)
            {
                Assert.Equal(["POST http://[2001:db8::1]/h HTTP/1.1"], proxy.RequestLines); // the first address
                Assert.Equal(["rebind.example"], proxy.HostHeaders);
            }
        });
    }

    [Fact]
    public async Task Pinned_proxy_target_refuses_a_host_that_resolves_private()
    {
        using var proxy = new RecordingProxy();
        await WithResolver(_ => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")], async () =>
        {
            var opts = new PushNotificationOptions { Proxy = new WebProxy(proxy.Address), PinProxyTarget = true };
            using var handler = PushUrlValidator.CreateHandler(opts);
            using var sender = await SendPinnedAsync(handler, opts.Proxy, "http://mixed.example/h");
            // The proxy serves connections in order, so once this probe is answered any earlier connection is recorded.
            using (var plain = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true }))
                (await plain.GetAsync("http://probe.example/", TestContext.Current.CancellationToken)).Dispose();
            lock (proxy) Assert.Equal(["GET http://probe.example/ HTTP/1.1"], proxy.RequestLines);
        });
    }

    [Fact]
    public async Task Pinned_proxy_target_over_https_tunnels_to_the_address_and_keeps_the_tls_name()
    {
        using var proxy = new RecordingProxy("rebind.example");
        var lookups = new List<string>();
        var errors = new List<SslPolicyErrors>();
        await WithResolver(Rebinding(lookups), async () =>
        {
            var opts = new PushNotificationOptions { Proxy = new WebProxy(proxy.Address), PinProxyTarget = true };
            using var handler = PushUrlValidator.CreateHandler(opts);
            // The certificate is self-signed, so its chain does not validate. The name check must still pass.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, e) =>
            {
                lock (errors) errors.Add(e);
                return cert is not null && cert.GetCertHashString() == proxy.Certificate!.GetCertHashString() &&
                       (e & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0;
            };
            using var sender = await SendPinnedAsync(handler, opts.Proxy, "https://rebind.example/h");
            lock (proxy)
            {
                Assert.Equal(["CONNECT 93.184.216.34:443 HTTP/1.1"], proxy.RequestLines); // tunnel to the checked address
                Assert.Equal(["rebind.example"], proxy.TlsServerNames); // SNI is the webhook name
                Assert.Equal(["rebind.example"], proxy.TunneledHostHeaders); // the POST went through the tunnel
            }

            lock (errors) Assert.Equal([SslPolicyErrors.RemoteCertificateChainErrors], errors); // no name mismatch
            lock (lookups) Assert.Equal(["rebind.example"], lookups);
        });
    }

    [Fact]
    public async Task Tls_name_check_fails_against_the_address_without_the_host_header()
    {
        // Control for the test above: the same endpoint reached by address, with no Host header naming the webhook,
        // gets no SNI and reports a name mismatch. So the checks above tell the two apart.
        using var proxy = new RecordingProxy("rebind.example");
        var errors = new List<SslPolicyErrors>();
        using var handler = new SocketsHttpHandler { Proxy = new WebProxy(proxy.Address), UseProxy = true };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, e) =>
        {
            lock (errors) errors.Add(e);
            return true;
        };
        using var http = new HttpClient(handler);
        (await http.GetAsync("https://93.184.216.34/h", TestContext.Current.CancellationToken)).Dispose();
        lock (proxy) Assert.Equal([""], proxy.TlsServerNames);
        lock (errors) Assert.True(errors.Single().HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public async Task Pinned_proxy_target_leaves_bypassed_hosts_to_the_connect_time_check()
    {
        // A bypassed request connects directly, so it is not pinned: the connect-time lookup runs and sees the rebinding.
        using var server = new LocalServer();
        var lookups = new List<string>();
        var calls = 0;
        await WithResolver(h =>
        {
            lock (lookups) lookups.Add(h);
            return Interlocked.Increment(ref calls) == 1 ? [IPAddress.Parse("93.184.216.34")] : [IPAddress.Loopback];
        }, async () =>
        {
            var opts = new PushNotificationOptions { Proxy = new BypassingProxy(), PinProxyTarget = true };
            using var handler = PushUrlValidator.CreateHandler(opts);
            using var sender = await SendWithAsync(handler, o =>
            {
                o.AllowInsecureUrls = true;
                o.Proxy = opts.Proxy;
                o.PinProxyTarget = true;
            }, Hook($"http://evil.example:{server.Port}/h"));
            lock (lookups) Assert.Equal(["evil.example", "evil.example"], lookups); // before delivery, then at connect
            Assert.Equal(0, server.Accepted);
        });
    }
}
