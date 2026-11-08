using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using A2A;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Client.Push;
using A2A.Integrity;
using A2A.Json;
using A2A.Signing;

namespace A2A.NetStandardSmoke;

/// <summary>
/// Runs the netstandard2.0 builds of A2A.Protocol and A2A.Client against a real agent (samples/EchoAgent) and a small local server.
/// Usage: A2A.NetStandard.Smoke &lt;agent-url&gt; &lt;api-key&gt;. Exit code 0 only when every check passes.
/// </summary>
internal static class Program
{
    private const string ExpectedNetStandard = ".NETStandard,Version=v2.0";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WholeRunTimeout = TimeSpan.FromMinutes(4);

    // Computed once from the real net10.0 assets of A2A.Protocol and A2A.Client (not the netstandard2.0 build).
    private const string ExpectedTextHash = "sha256:ea8cf0895dcf864071c08c29435f7c11ddcd4126d56f98d8f68edd063ddc2650";
    private const string ExpectedNumbersHash = "sha256:1d64567aadba6a8cff9c0fbab1368d2d57a6463d82568d9d900377ac46147186";
    private const string ExpectedRegistryOutcome = "emoji=True,mixed=False,dup=DuplicateName,deseret=True,cut=True,lone=ok";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: A2A.NetStandard.Smoke <agent-url> <api-key>");
            return 2;
        }

        // Last line of defence: the program never hangs CI, whatever a network call does.
        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine("SMOKE FAIL: the whole run exceeded " + WholeRunTimeout);
            Environment.Exit(3);
        }, null, WholeRunTimeout, Timeout.InfiniteTimeSpan);

        var agentUrl = new Uri(args[0]);
        var apiKey = args[1];
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("assemblies are the netstandard2.0 builds", _ => { AssertNetStandardAssemblies(); return Task.FromResult(0); }),
            ("1. resolve the Agent Card", ct => ResolveCard(agentUrl, apiKey, ct)),
            ("2. SendMessage on every served transport", ct => SendMessage(agentUrl, apiKey, ct)),
            ("3a. streaming returns events", ct => Streaming(agentUrl, apiKey, ct)),
            ("3b. cancelling a blocked stream ends it promptly", CancelBlockedStream),
            ("4. sign and verify: ES256, RS256, PS256", SignAndVerify),
            ("5. jku card is Unverified, refused with trust", JkuCard),
            ("6. agent name with supplementary-plane characters", _ => { RegistryNames(); return Task.FromResult(0); }),
            ("7. webhook token check and artifact hash", _ => { WebhookAndHash(); return Task.FromResult(0); }),
            ("8. canonical JSON numbers", _ => { CanonicalNumbers(); return Task.FromResult(0); }),
            ("assembly guard: the SSRF guard is not present", _ => { AssertNoSecurityHttp(); return Task.FromResult(0); }),
        };

        foreach (var (name, run) in steps)
        {
            using var cts = new CancellationTokenSource(StepTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                var task = run(cts.Token);
                // Not every step honours the token. This keeps a stuck one from hanging the run.
                if (await Task.WhenAny(task, Task.Delay(StepTimeout + TimeSpan.FromSeconds(5))).ConfigureAwait(false) != task)
                    throw new SmokeException("did not finish within " + StepTimeout);
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SMOKE FAIL: " + name);
                Console.Error.WriteLine(ex is SmokeException ? ex.Message : ex.ToString());
                return 1;
            }
            Console.WriteLine("ok   " + name + " (" + sw.ElapsedMilliseconds + " ms)");
        }

        Console.WriteLine("SMOKE OK");
        return 0;
    }

    // ---- startup proof ----

    private static string FrameworkOf(Assembly assembly) =>
        assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "(none)";

    private static void AssertNetStandardAssemblies()
    {
        foreach (var assembly in new[] { typeof(A2AClient).Assembly, typeof(AgentCard).Assembly })
        {
            var framework = FrameworkOf(assembly);
            Console.WriteLine("     " + assembly.GetName().Name + " " + assembly.GetName().Version + " built for " + framework + " (" + assembly.Location + ")");
            Check(framework == ExpectedNetStandard, assembly.GetName().Name + " is built for '" + framework + "', expected '" + ExpectedNetStandard + "'");
        }
    }

    private static void AssertNoSecurityHttp() =>
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "A2A.Security.Http"),
            "A2A.Security.Http is loaded; the netstandard2.0 client must not reference it");

    // ---- checks against the real agent ----

    private static HttpClient NewClient(string apiKey) =>
        new HttpClient(new ApiKeyHandler(apiKey, new HttpClientHandler())) { Timeout = StepTimeout };

    private static async Task<ResolvedAgentCard> Resolve(Uri agentUrl, string apiKey, CancellationToken ct)
    {
        using var http = NewClient(apiKey);
        return await new A2ACardResolver(http).ResolveAsync(agentUrl, ct).ConfigureAwait(false);
    }

    private static async Task ResolveCard(Uri agentUrl, string apiKey, CancellationToken ct)
    {
        var resolved = await Resolve(agentUrl, apiKey, ct).ConfigureAwait(false);
        Console.WriteLine("     card '" + resolved.Card.Name + "', interfaces: " +
            string.Join(", ", resolved.Card.SupportedInterfaces.Select(i => i.ProtocolBinding + " " + i.Url)));
        Check(resolved.Card.Name == "Echo Agent", "unexpected card name '" + resolved.Card.Name + "'");
        Check(resolved.Verification.Status == AgentCardVerificationStatus.Unsigned, "EchoAgent card should be Unsigned, got " + resolved.Verification.Status);
        Check(resolved.Card.SupportedInterfaces.Count > 0, "card lists no interface");
    }

    private static IEnumerable<string> ServedBindings(AgentCard card) =>
        card.SupportedInterfaces.Select(i => i.ProtocolBinding)
            .Where(b => b == ProtocolBindings.JsonRpc || b == ProtocolBindings.HttpJson).Distinct().ToList();

    private static A2AClient ClientFor(AgentCard card, HttpClient http, string binding) =>
        A2AClientFactory.Create(card, http, new A2AClientOptions { SupportedBindings = new List<string> { binding } });

    private static async Task SendMessage(Uri agentUrl, string apiKey, CancellationToken ct)
    {
        var resolved = await Resolve(agentUrl, apiKey, ct).ConfigureAwait(false);
        var bindings = ServedBindings(resolved.Card).ToList();
        Check(bindings.Count > 0, "the agent serves neither JSON-RPC nor HTTP+JSON");
        using var http = NewClient(apiKey);
        foreach (var binding in bindings)
        {
            var client = ClientFor(resolved.Card, http, binding);
            var response = await client.SendMessageAsync("ping", cancellationToken: ct).ConfigureAwait(false);
            var text = (response as MessageResponse)?.Message.GetText();
            Console.WriteLine("     " + binding + ": ping -> " + text);
            Check(text == "pong", binding + ": expected 'pong', got '" + text + "'");
        }
    }

    private static async Task Streaming(Uri agentUrl, string apiKey, CancellationToken ct)
    {
        var resolved = await Resolve(agentUrl, apiKey, ct).ConfigureAwait(false);
        using var http = NewClient(apiKey);
        foreach (var binding in ServedBindings(resolved.Card))
        {
            var client = ClientFor(resolved.Card, http, binding);
            var count = 0;
            var echoed = new StringBuilder();
            var terminal = TaskState.Unspecified;
            await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("hello from netstandard"), cancellationToken: ct).ConfigureAwait(false))
            {
                count++;
                if (evt is ArtifactUpdateEvent a) echoed.Append(string.Concat(a.ArtifactUpdate.Artifact.Parts.OfType<TextPart>().Select(p => p.Text)));
                if (evt is StatusUpdateEvent s && s.StatusUpdate.Status.State.IsTerminal()) terminal = s.StatusUpdate.Status.State;
            }
            Console.WriteLine("     " + binding + ": " + count + " events, echoed '" + echoed + "', final " + terminal);
            Check(count >= 2, binding + ": expected several events, got " + count);
            Check(echoed.ToString() == "hello from netstandard", binding + ": echoed '" + echoed + "'");
            Check(terminal == TaskState.Completed, binding + ": final state " + terminal);
        }
    }

    // ---- local server: holds an SSE response open, serves cards ----

    private static async Task CancelBlockedStream(CancellationToken outer)
    {
        using var server = new LocalServer();
        server.Start();
        foreach (var binding in new[] { ProtocolBindings.HttpJson, ProtocolBindings.JsonRpc })
        {
            var card = MakeCard("Holding agent");
            card.Capabilities = new AgentCapabilities { Streaming = true };
            card.SupportedInterfaces = new List<AgentInterface>
            {
                new AgentInterface { Url = server.BaseUrl + (binding == ProtocolBindings.JsonRpc ? "rpc" : "rest"), ProtocolBinding = binding },
            };
            using var http = new HttpClient { Timeout = StepTimeout };
            var client = ClientFor(card, http, binding);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            var enumerator = client.SendStreamingMessageAsync(Message.FromUser("hold"), cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
            try
            {
                var first = await WithLimit(enumerator.MoveNextAsync().AsTask(), TimeSpan.FromSeconds(10), binding + ": first event").ConfigureAwait(false);
                Check(first, binding + ": the stream ended before the first event");
                // The server now sends nothing more and keeps the response open: the next read blocks.
                var blocked = enumerator.MoveNextAsync().AsTask();
                await Task.Delay(300, outer).ConfigureAwait(false);
                Check(!blocked.IsCompleted, binding + ": the read did not block");
                var sw = Stopwatch.StartNew();
                cts.Cancel();
                var ended = false;
                try { await WithLimit(blocked, TimeSpan.FromSeconds(5), binding + ": the cancelled read").ConfigureAwait(false); ended = true; }
                catch (OperationCanceledException) { /* expected */ }
                Check(!ended, binding + ": the read returned data after cancel");
                Console.WriteLine("     " + binding + ": blocked read ended " + sw.ElapsedMilliseconds + " ms after cancel");
                Check(sw.Elapsed < TimeSpan.FromSeconds(3), binding + ": cancel took " + sw.ElapsedMilliseconds + " ms");
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> WithLimit<T>(Task<T> task, TimeSpan limit, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) != task)
            throw new SmokeException(what + " did not finish within " + limit);
        return await task.ConfigureAwait(false);
    }

    // ---- signing ----

    private static AgentCard MakeCard(string name) => new AgentCard
    {
        Name = name,
        Description = "Smoke test card",
        Version = "1.0.0",
        SupportedInterfaces = new List<AgentInterface> { new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc } },
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = new List<string> { "text/plain" },
        DefaultOutputModes = new List<string> { "text/plain" },
        Skills = new List<AgentSkill> { new AgentSkill { Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = new List<string> { "echo" } } },
    };

    private static AsymmetricAlgorithm NewKey(string alg)
    {
        if (alg == "ES256") return ECDsa.Create(ECCurve.NamedCurves.nistP256)!;
#if NETFRAMEWORK
        // RSA.Create() may hand back RSACryptoServiceProvider, which has no PSS. RSACng does.
        return new RSACng(2048);
#else
        return RSA.Create(2048);
#endif
    }

    private static async Task SignAndVerify(CancellationToken ct)
    {
        var source = new Uri("https://agent.example/.well-known/agent-card.json");
        var origin = CardTrustPolicy.OriginOf(source);
        foreach (var alg in new[] { "ES256", "RS256", "PS256" })
        {
            using var key = NewKey(alg);
            var signingKey = new AgentCardSigningKey(key, "key-" + alg, alg);
            var policy = new CardTrustPolicy();
            policy.TrustedKeys[origin] = new JwkSet { Keys = new List<Jwk> { signingKey.ToPublicJwk() } };
            var verifier = new AgentCardVerifier(policy);

            var signed = AgentCardSigner.Sign(MakeCard("Signed agent"), signingKey);
            var ok = await verifier.VerifyAsync(signed, source, ct).ConfigureAwait(false);
            Console.WriteLine("     " + alg + ": " + ok.Status + (ok.Reason is null ? "" : " (" + ok.Reason + ")"));
            Check(ok.Status == AgentCardVerificationStatus.Verified, alg + ": expected Verified, got " + ok.Status + " " + ok.Reason);

            signed.Description = "Tampered";
            var bad = await verifier.VerifyAsync(signed, source, ct).ConfigureAwait(false);
            Console.WriteLine("     " + alg + " tampered: " + bad.Status);
            Check(bad.Status != AgentCardVerificationStatus.Verified, alg + ": a tampered card verified");
        }
    }

    private static async Task JkuCard(CancellationToken ct)
    {
        using var server = new LocalServer();
        server.Start();
        var origin = CardTrustPolicy.OriginOf(new Uri(server.BaseUrl));

        using var es = ECDsa.Create(ECCurve.NamedCurves.nistP256)!;
        var jkuKey = new AgentCardSigningKey(es, "jku-1", "ES256", "https://jwks.example/jwks.json");
        server.Cards["/jku/agent-card.json"] = A2AJson.Serialize(AgentCardSigner.Sign(MakeCard("Jku agent"), jkuKey));

        var pinnedKey = new AgentCardSigningKey(es, "pinned-1", "ES256");
        server.Cards["/pinned/agent-card.json"] = A2AJson.Serialize(AgentCardSigner.Sign(MakeCard("Pinned agent"), pinnedKey));
        var tampered = AgentCardSigner.Sign(MakeCard("Pinned agent"), pinnedKey);
        tampered.Description = "Tampered after signing";
        server.Cards["/tampered/agent-card.json"] = A2AJson.Serialize(tampered);

        using var http = new HttpClient { Timeout = StepTimeout };

        // A jku host is allowed, but there is no fetcher on netstandard2.0: Unverified. It resolves only when a signature is not required.
        var lenient = new CardTrustPolicy { RequireSignature = false };
        lenient.AllowJkuHost(origin, "jwks.example");
        var resolved = await new A2ACardResolver(http, new A2ACardResolverOptions { Trust = lenient, RequireHttps = false })
            .ResolveAsync(new Uri(server.BaseUrl + "jku/agent-card.json"), ct).ConfigureAwait(false);
        Console.WriteLine("     jku card: " + resolved.Verification.Status + " (" + resolved.Verification.Reason + ")");
        Check(resolved.Verification.Status == AgentCardVerificationStatus.Unverified, "jku card: expected Unverified, got " + resolved.Verification.Status);
        Check((resolved.Verification.Reason ?? "").IndexOf("no JWKS fetcher configured", StringComparison.Ordinal) >= 0,
            "jku card: reason is '" + resolved.Verification.Reason + "'");

        // With trust configured (jku host allowed, signature required by default) the same card is refused.
        var strict = new CardTrustPolicy();
        strict.AllowJkuHost(origin, "jwks.example");
        try
        {
            await new A2ACardResolver(http, new A2ACardResolverOptions { Trust = strict, RequireHttps = false })
                .ResolveAsync(new Uri(server.BaseUrl + "jku/agent-card.json"), ct).ConfigureAwait(false);
            throw new SmokeException("jku card: resolved although trust is configured");
        }
        catch (AgentCardVerificationException ex)
        {
            Console.WriteLine("     jku card with trust: refused (" + ex.Result.Status + ")");
            Check(ex.Result.Status == AgentCardVerificationStatus.Unverified, "jku card with trust: status " + ex.Result.Status);
        }

        // A card signed by a pinned key resolves as Verified; a tampered one is refused as Failed.
        var pinned = new CardTrustPolicy();
        pinned.TrustedKeys[origin] = new JwkSet { Keys = new List<Jwk> { pinnedKey.ToPublicJwk() } };
        var good = await new A2ACardResolver(http, new A2ACardResolverOptions { Trust = pinned, RequireHttps = false })
            .ResolveAsync(new Uri(server.BaseUrl + "pinned/agent-card.json"), ct).ConfigureAwait(false);
        Console.WriteLine("     pinned card: " + good.Verification.Status);
        Check(good.Verification.Status == AgentCardVerificationStatus.Verified, "pinned card: expected Verified, got " + good.Verification.Status);
        try
        {
            await new A2ACardResolver(http, new A2ACardResolverOptions { Trust = pinned, RequireHttps = false })
                .ResolveAsync(new Uri(server.BaseUrl + "tampered/agent-card.json"), ct).ConfigureAwait(false);
            throw new SmokeException("tampered card: resolved");
        }
        catch (AgentCardVerificationException ex)
        {
            Console.WriteLine("     tampered card: refused (" + ex.Result.Status + ")");
            Check(ex.Result.Status == AgentCardVerificationStatus.Failed, "tampered card: status " + ex.Result.Status);
        }
    }

    // ---- name normalization through the registry (behind it: a polyfilled Rune) ----

    private static ResolvedAgentCard ResolvedWithName(string name, string host)
    {
        var uri = new Uri("https://" + host + "/.well-known/agent-card.json");
        var verification = new AgentCardVerificationResult(AgentCardVerificationStatus.Unsigned, null, CardTrustPolicy.OriginOf(uri), "the card has no signatures");
        return new ResolvedAgentCard(MakeCard(name), "{}", uri, verification);
    }

    private static void RegistryNames()
    {
        var outcome = new StringBuilder();
        var registry = new AgentRegistry();

        // Emoji (surrogate pair) in the name.
        var emoji = registry.Register(ResolvedWithName("Agent \U0001F600 Bot", "a.example"));
        outcome.Append("emoji=").Append(registry.TryGetByName("AGENT \U0001F600   BOT", out _)).Append(",mixed=").Append(emoji.MixedScriptName);
        try
        {
            registry.Register(ResolvedWithName("agent \U0001F600 bot", "b.example"));
            outcome.Append(",dup=none");
        }
        catch (AgentRegistrationException ex) { outcome.Append(",dup=").Append(ex.Reason); }

        // A supplementary-plane letter with case (Deseret capital letter long I, U+10400).
        registry.Register(ResolvedWithName("\U00010400bc", "c.example"));
        outcome.Append(",deseret=").Append(registry.TryGetByName("\U00010428BC", out _));

        // A name cut inside a surrogate pair, and an unpaired surrogate.
        var tiny = new AgentRegistry(new AgentRegistryOptions { MaxNameLength = 3 });
        var cut = tiny.Register(ResolvedWithName("ab\U0001F600cd", "d.example"));
        outcome.Append(",cut=").Append(cut.Name.ToString().Length <= 3);
        try
        {
            registry.Register(ResolvedWithName("lone\uD800surrogate", "e.example"));
            outcome.Append(",lone=ok");
        }
        catch (AgentRegistrationException ex) { outcome.Append(",lone=").Append(ex.Reason); }

        Console.WriteLine("     outcome: " + outcome);
        Check(outcome.ToString() == ExpectedRegistryOutcome, "outcome '" + outcome + "' differs from net10.0 '" + ExpectedRegistryOutcome + "'");
    }

    // ---- webhook token and artifact hash ----

    private static void WebhookAndHash()
    {
        var headers = new Dictionary<string, string> { [A2AProtocol.Headers.NotificationToken] = "right-token" };
        Check(new PushNotificationReceiver().Validate(headers, "right-token") == PushValidationResult.Valid, "the right token was refused");
        var wrong = new PushNotificationReceiver().Validate(headers, "wrong-token");
        Check(wrong == PushValidationResult.InvalidToken, "a wrong token gave " + wrong);

        var hash = ArtifactHash.Compute(new Part[] { Part.FromText("héllo \U0001F600 a2a") });
        Console.WriteLine("     text hash " + hash);
        Check(hash == ExpectedTextHash, "text hash '" + hash + "' differs from net10.0 '" + ExpectedTextHash + "'");
    }

    private static void CanonicalNumbers()
    {
        const string json = "{\"n\":[0.1234567890123456,1.1000000000000001,0.30000000000000004,5e-324,1.7976931348623157e308,1e21,123456789012345680000]}";
        using var doc = JsonDocument.Parse(json);
        var hash = ArtifactHash.Compute(new Part[] { new DataPart(doc.RootElement.Clone()) });
        Console.WriteLine("     numbers hash " + hash);
        Check(hash == ExpectedNumbersHash, "numbers hash '" + hash + "' differs from net10.0 '" + ExpectedNumbersHash + "'");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new SmokeException(message);
    }
}

internal sealed class SmokeException : Exception
{
    public SmokeException(string message) : base(message) { }
}

/// <summary>Adds the X-Api-Key header the EchoAgent sample asks for.</summary>
internal sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly string _key;

    public ApiKeyHandler(string key, HttpMessageHandler inner) : base(inner) => _key = key;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", _key);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// A loopback server. Stream requests get one event and then the response stays open until the server stops; GET requests
/// for a path in <see cref="Cards"/> get that card JSON. Works on net10.0 and net48 (HttpListener).
/// </summary>
internal sealed class LocalServer : IDisposable
{
    private const string Event =
        "{\"statusUpdate\":{\"taskId\":\"t1\",\"contextId\":\"c1\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}";

    private readonly HttpListener _listener = new HttpListener();
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();

    public LocalServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = "http://127.0.0.1:" + port + "/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public string BaseUrl { get; }

    public Dictionary<string, string> Cards { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && Cards.TryGetValue(path, out var card))
            {
                var bytes = Encoding.UTF8.GetBytes(card);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "POST")
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync().ConfigureAwait(false);
                var data = Event;
                if (path == "/rpc")
                {
                    using var doc = JsonDocument.Parse(body);
                    data = "{\"jsonrpc\":\"2.0\",\"id\":" + doc.RootElement.GetProperty("id").GetRawText() + ",\"result\":" + Event + "}";
                }
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                var bytes = Encoding.UTF8.GetBytes("data: " + data + "\n\n");
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false); // hold the response open
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client went away or the server stopped.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (Exception) { /* closing */ }
        _stop.Dispose();
    }
}
