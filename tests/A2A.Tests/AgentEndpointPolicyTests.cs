using System.Diagnostics.Metrics;
using System.Net;
using A2A.Client;
using A2A.Tests.Telemetry;

namespace A2A.Tests;

/// <summary>The Agent Card endpoint policy. Process-wide seams (DNS, meter listener) are used, so these tests never run in parallel.</summary>
[Collection(TelemetryCollection.Name)]
public sealed class AgentEndpointPolicyTests : IDisposable
{
    private static readonly Uri PublicCard = new("https://203.0.113.5/");
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress Private = IPAddress.Parse("10.0.0.5");

    private static AgentEndpointCheck Check(Uri endpoint, Uri? cardSource, IPAddress[] endpointAddresses, IPAddress[]? cardAddresses = null) =>
        new(endpoint, ProtocolBindings.JsonRpc, cardSource, endpointAddresses, cardAddresses ?? []);

    private static AgentCard Card(params (string Binding, string Url)[] interfaces) => new()
    {
        Name = "Secret card text", Description = "d", Version = "1",
        SupportedInterfaces = interfaces.Select(i => new AgentInterface { Url = i.Url, ProtocolBinding = i.Binding, ProtocolVersion = "1.0" }).ToList(),
        Capabilities = new(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
    };

    public void Dispose() => A2AClientFactory.ResolveHost = A2AClientFactory.DefaultResolveHost;

    // ---------------------------------------------------------------- pure policies

    [Fact]
    public void Match_card_network_refuses_a_private_endpoint_of_a_public_card() =>
        Assert.False(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("http://internal/a2a"), PublicCard, [Private], [Public])));

    [Fact]
    public void Match_card_network_allows_a_private_endpoint_of_a_private_card() =>
        Assert.True(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("http://internal/a2a"), new Uri("http://intranet/"), [Private], [IPAddress.Parse("10.0.0.9")])));

    [Fact]
    public void Match_card_network_does_not_trust_a_card_host_with_a_public_address() =>
        Assert.False(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("http://internal/a2a"), new Uri("https://card.example/"), [Private], [Public, IPAddress.Parse("10.0.0.9")])));

    [Fact]
    public async Task An_ip_literal_with_a_trailing_dot_is_judged_without_dns()
    {
        A2AClientFactory.ResolveHost = (_, _) => throw new InvalidOperationException("DNS must not be used");
        var card = Card((ProtocolBindings.JsonRpc, "http://10.0.0.5./a2a"));

        await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()));
    }

    [Fact]
    public void Match_card_network_allows_a_card_the_caller_supplied() =>
        Assert.True(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("http://internal/a2a"), null, [Private])));

    [Fact]
    public void Match_card_network_allows_a_public_endpoint_of_a_public_card() =>
        Assert.True(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("https://agent.example/a2a"), PublicCard, [Public], [Public])));

    [Fact]
    public void Presets_allow_unresolved_hosts() =>
        Assert.True(AgentEndpointPolicies.MatchCardNetwork(Check(new Uri("https://agent.example/a2a"), PublicCard, [], [Public])));

    [Fact]
    public void Presets_refuse_an_endpoint_without_a_host()
    {
        var noHost = Check(new Uri("urn:a2a:no-host"), null, []);
        Assert.False(AgentEndpointPolicies.PublicOnly()(noHost));
        Assert.False(AgentEndpointPolicies.MatchCardNetwork(noHost));
    }

    [Fact]
    public void Public_only_refuses_private_addresses_unless_the_network_is_allowed()
    {
        var policy = AgentEndpointPolicies.PublicOnly((IPAddress.Parse("10.0.0.0"), 8));
        Assert.True(policy(Check(new Uri("http://a/"), null, [IPAddress.Parse("10.1.2.3")])));
        Assert.False(policy(Check(new Uri("http://a/"), null, [IPAddress.Parse("192.168.1.1")])));
        Assert.False(policy(Check(new Uri("http://a/"), null, [Public, IPAddress.Parse("192.168.1.1")]))); // any address counts
    }

    [Fact]
    public void Public_only_sees_through_ipv4_mapped_addresses() =>
        Assert.False(AgentEndpointPolicies.PublicOnly()(Check(new Uri("http://a/"), null, [IPAddress.Parse("::ffff:10.0.0.5")])));

    // ---------------------------------------------------------------- EnsureEndpointsAllowedAsync

    [Fact]
    public async Task A_public_card_may_not_name_a_private_endpoint()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://10.0.0.5/a2a"));

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("10.0.0.5", ex.Message);
        Assert.DoesNotContain("Secret card text", ex.Message);
    }

    [Fact]
    public async Task Every_selectable_interface_is_checked_not_only_the_first()
    {
        var card = Card((ProtocolBindings.JsonRpc, "https://203.0.113.9/a2a"), (ProtocolBindings.HttpJson, "http://10.0.0.5/a2a"));

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()));

        Assert.Contains("10.0.0.5", ex.Message);
    }

    [Fact]
    public async Task An_interface_the_client_cannot_select_is_not_checked()
    {
        var card = Card((ProtocolBindings.JsonRpc, "https://203.0.113.9/a2a"), (ProtocolBindings.Grpc, "10.0.0.5:50051"));

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()); // GRPC is not a supported binding here
    }

    [Fact]
    public async Task A_bare_grpc_host_and_port_is_judged_by_its_host()
    {
        var card = Card((ProtocolBindings.Grpc, "10.0.0.5:50051"));
        var options = new A2AClientOptions { SupportedBindings = [ProtocolBindings.Grpc] };

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, options));

        Assert.Contains("10.0.0.5", ex.Message);
    }

    [Fact]
    public async Task An_endpoint_with_no_determinable_host_is_refused()
    {
        var card = Card((ProtocolBindings.JsonRpc, "file:///etc/passwd"));

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_custom_binding_with_a_url_that_has_no_host_is_not_judged()
    {
        var card = Card(("X-THIRD-PARTY", "x://agent"), ("X-OTHER", "pipe:/run/agent.sock"));
        var options = new A2AClientOptions { SupportedBindings = ["X-THIRD-PARTY", "X-OTHER"] };

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, options);
    }

    [Fact]
    public async Task A_localhost_card_may_name_a_localhost_endpoint()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://localhost/a2a"), (ProtocolBindings.HttpJson, "http://api.localhost:8080/a2a"));

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, new Uri("http://localhost/"), new A2AClientOptions());
    }

    [Fact]
    public async Task A_public_card_may_not_name_localhost()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://localhost/a2a"));

        await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions()));
    }

    [Fact]
    public async Task Allow_all_and_no_policy_do_no_dns_and_no_check()
    {
        A2AClientFactory.ResolveHost = (_, _) => throw new InvalidOperationException("DNS must not be used");
        var card = Card((ProtocolBindings.JsonRpc, "http://internal.example/a2a"));

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions { EndpointPolicy = AgentEndpointPolicies.AllowAll });
        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, new A2AClientOptions { EndpointPolicy = null });
    }

    [Fact]
    public async Task A_host_name_is_resolved_and_judged_by_its_addresses()
    {
        A2AClientFactory.ResolveHost = (host, _) => Task.FromResult(host == "internal.example" ? new[] { Private } : new[] { Public });
        var bad = Card((ProtocolBindings.JsonRpc, "https://internal.example/a2a"));
        var good = Card((ProtocolBindings.JsonRpc, "https://agent.example/a2a"));

        await Assert.ThrowsAsync<A2AException>(async () => await A2AClientFactory.EnsureEndpointsAllowedAsync(bad, new Uri("https://card.example/"), new A2AClientOptions()));
        await A2AClientFactory.EnsureEndpointsAllowedAsync(good, new Uri("https://card.example/"), new A2AClientOptions());
    }

    [Fact]
    public async Task A_failed_lookup_is_an_empty_list_not_an_error()
    {
        A2AClientFactory.ResolveHost = (_, _) => throw new System.Net.Sockets.SocketException();
        var card = Card((ProtocolBindings.JsonRpc, "https://nowhere.example/a2a"));

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, new Uri("https://card.example/"), new A2AClientOptions());
    }

    [Fact]
    public async Task The_caller_can_cancel_a_slow_lookup()
    {
        A2AClientFactory.ResolveHost = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => Array.Empty<IPAddress>(), TaskScheduler.Default);
        var card = Card((ProtocolBindings.JsonRpc, "https://slow.example/a2a"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await A2AClientFactory.EnsureEndpointsAllowedAsync(card, new Uri("https://card.example/"), new A2AClientOptions(), cts.Token));
    }

    [Fact]
    public async Task A_custom_policy_sees_the_addresses()
    {
        AgentEndpointCheck? seen = null;
        var options = new A2AClientOptions { EndpointPolicy = c => { seen = c; return true; } };
        var card = Card((ProtocolBindings.JsonRpc, "http://10.0.0.5:81/a2a"));

        await A2AClientFactory.EnsureEndpointsAllowedAsync(card, PublicCard, options);

        Assert.Equal(new Uri("http://10.0.0.5:81/a2a"), seen!.Endpoint);
        Assert.Equal(ProtocolBindings.JsonRpc, seen.ProtocolBinding);
        Assert.Equal(PublicCard, seen.CardSource);
        Assert.Equal([Private], seen.EndpointAddresses);
        Assert.Equal([Public], seen.CardSourceAddresses);
    }

    // ---------------------------------------------------------------- Create (sync)

    [Fact]
    public void Create_applies_a_policy_with_literal_addresses()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://10.0.0.5/a2a"));

        var ex = Assert.Throws<A2AException>(() => A2AClientFactory.Create(card, new HttpClient(), new A2AClientOptions { EndpointPolicy = AgentEndpointPolicies.PublicOnly() }));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public void Create_with_a_caller_supplied_card_works_by_default()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://10.0.0.5/a2a"));

        using var http = new HttpClient();
        Assert.NotNull(A2AClientFactory.Create(card, http));
    }

    [Fact]
    public void The_factory_copies_the_policy()
    {
        var card = Card((ProtocolBindings.JsonRpc, "http://localhost/a2a"));
        Func<AgentEndpointCheck, bool> deny = _ => false;

        Assert.Throws<A2AException>(() => A2AClientFactory.Create(card, new HttpClient(), new A2AClientOptions { EndpointPolicy = deny }));
    }

    // ---------------------------------------------------------------- CreateAsync + counter

    private sealed class CardHandler(string cardJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cardJson, System.Text.Encoding.UTF8, "application/json") });
    }

    private static string CardJson(string url) => System.Text.Json.JsonSerializer.Serialize(Card((ProtocolBindings.JsonRpc, url)), A2A.Json.A2AJson.Options);

    [Fact]
    public async Task Create_async_refuses_a_private_endpoint_of_a_public_card_and_counts_it()
    {
        var refused = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "a2a.client.endpoint.refused") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref refused, v));
        listener.Start();
        using var http = new HttpClient(new CardHandler(CardJson("http://10.0.0.5/a2a")));

        var ex = await Assert.ThrowsAsync<A2AException>(() => A2AClientFactory.CreateAsync(PublicCard, http));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Equal(1, Interlocked.Read(ref refused));
    }

    [Fact]
    public async Task Create_async_allows_a_private_endpoint_of_a_loopback_card()
    {
        using var http = new HttpClient(new CardHandler(CardJson("http://10.0.0.5/a2a")));

        var client = await A2AClientFactory.CreateAsync(new Uri("http://localhost/"), http);

        Assert.NotNull(client);
    }
}
