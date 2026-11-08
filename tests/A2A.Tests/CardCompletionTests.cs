using System.Security.Cryptography;
using System.Text.Json;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>An Agent Card without interfaces is completed once, at first serve, from the mapped bindings (spec D3).</summary>
public sealed class CardCompletionTests
{
    private static void NoInterfaces(A2AServerOptions o) => o.AgentCard.SupportedInterfaces = [];

    private static async Task<AgentCard> ServedCardAsync(TestAgentHost host)
    {
        using var resp = await host.Http().GetAsync(A2AProtocol.WellKnownAgentCardPath);
        resp.EnsureSuccessStatusCode();
        return A2AJson.Deserialize<AgentCard>(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Empty_interfaces_are_filled_from_mapped_bindings()
    {
        await using var host = await TestAgentHost.StartAsync(NoInterfaces);
        var card = await ServedCardAsync(host);
        Assert.Collection(card.SupportedInterfaces.OrderBy(i => i.ProtocolBinding),
            i => { Assert.Equal(ProtocolBindings.HttpJson, i.ProtocolBinding); Assert.Equal("http://localhost/a2a", i.Url); },
            i => { Assert.Equal(ProtocolBindings.JsonRpc, i.ProtocolBinding); Assert.Equal("http://localhost/a2a", i.Url); });
        // The handler's capability checks read the same completed card.
        Assert.Same(host.App.Services.GetRequiredService<IAgentCardProvider>().Get(), host.Handler.AgentCard);
        Assert.Equal(2, host.Handler.AgentCard.SupportedInterfaces.Count);
    }

    [Fact]
    public async Task Explicit_interfaces_are_untouched()
    {
        await using var host = await TestAgentHost.StartAsync(); // DefaultCard lists two interfaces at BaseUrl
        var card = await ServedCardAsync(host);
        Assert.Equal(2, card.SupportedInterfaces.Count);
        Assert.All(card.SupportedInterfaces, i => Assert.Equal(TestAgentHost.BaseUrl, i.Url));
    }

    [Fact]
    public async Task PublicBaseUrl_wins_over_server_address()
    {
        await using var app = await BuildAsync(o => o.PublicBaseUrl = new Uri("https://agent.example.com/svc/"), start: true);
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        Assert.Equal(2, card.SupportedInterfaces.Count);
        Assert.All(card.SupportedInterfaces, i => Assert.Equal("https://agent.example.com/svc/a2a", i.Url));
        await app.StopAsync();
    }

    [Fact]
    public async Task First_listen_address_is_used_and_wildcards_become_localhost()
    {
        await using var app = await BuildAsync(configureHttp: null, start: true);
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        Assert.All(card.SupportedInterfaces, i => Assert.Equal("http://localhost:8080/a2a", i.Url));
        await app.StopAsync();

        await using var plus = await BuildAsync(configureHttp: null, start: true, addresses: ["http://+:5000/"]);
        Assert.All(plus.Services.GetRequiredService<IAgentCardProvider>().Get().SupportedInterfaces,
            i => Assert.Equal("http://localhost:5000/a2a", i.Url));
        await plus.StopAsync();
    }

    [Fact]
    public async Task Card_built_before_start_is_not_cached()
    {
        await using var app = await BuildAsync(configureHttp: null, start: false);
        var provider = app.Services.GetRequiredService<IAgentCardProvider>();
        Assert.All(provider.Get().SupportedInterfaces, i => Assert.Equal("http://localhost/a2a", i.Url));
        await app.StartAsync();
        var after = provider.Get();
        Assert.All(after.SupportedInterfaces, i => Assert.Equal("http://localhost:8080/a2a", i.Url));
        Assert.Same(after, provider.Get());
        await app.StopAsync();
    }

    [Fact]
    public async Task Extended_card_provider_receives_the_completed_card()
    {
        AgentCard? seen = null;
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.AgentCard.SupportedInterfaces = [];
            o.ExtendedAgentCardProvider = (_, publicCard, _) => { seen = publicCard; return ValueTask.FromResult<AgentCard?>(publicCard); };
        });
        var client = host.Client(ProtocolBindings.JsonRpc);
        await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.SupportedInterfaces.Count);
        Assert.Same(host.Handler.AgentCard, seen);
    }

    [Fact]
    public void PublicBaseUrl_must_be_absolute_http_without_query_or_fragment()
    {
        var http = new A2AHttpOptions();
        Assert.Throws<ArgumentException>(() => http.PublicBaseUrl = new Uri("/rel", UriKind.Relative));
        Assert.Throws<ArgumentException>(() => http.PublicBaseUrl = new Uri("ftp://x/"));
        Assert.Throws<ArgumentException>(() => http.PublicBaseUrl = new Uri("https://x/?q=1"));
        Assert.Throws<ArgumentException>(() => http.PublicBaseUrl = new Uri("https://x/#f"));
        http.PublicBaseUrl = new Uri("https://x/p");
    }

    /// <summary>A server that reports listen addresses when it starts, as Kestrel does; no requests are served.</summary>
    private sealed class FakeServer(string[] addresses) : Microsoft.AspNetCore.Hosting.Server.IServer
    {
        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
        {
            var feature = new Microsoft.AspNetCore.Hosting.Server.Features.ServerAddressesFeature();
            foreach (var a in addresses) feature.Addresses.Add(a);
            Features.Set<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>(feature);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static async Task<WebApplication> BuildAsync(Action<A2AHttpOptions>? configureHttp, bool start, string[]? addresses = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.Server.IServer>(new FakeServer(addresses ?? ["http://[::]:8080", "http://+:5000/"]));
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = TestAgentHost.DefaultCard();
            o.AgentCard.SupportedInterfaces = [];
            o.AllowAnonymous = true;
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);
        if (configureHttp is not null) builder.Services.ConfigureA2AHttp(configureHttp);
        var app = builder.Build();
        app.MapA2A("/a2a");
        if (start) await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Completed_card_is_what_gets_signed()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AgentCardSigningKey(ec, "k1", "ES256");
        await using var host = await TestAgentHost.StartAsync(NoInterfaces, configureHttp: h => h.SigningKey = key);
        using var resp = await host.Http().GetAsync(A2AProtocol.WellKnownAgentCardPath);
        var raw = await resp.Content.ReadAsStringAsync();
        var card = A2AJson.Deserialize<AgentCard>(raw);
        Assert.Equal(2, card.SupportedInterfaces.Count);
        Assert.NotNull(card.Signatures);

        var policy = new CardTrustPolicy();
        policy.TrustedKeys["https://agent.example"] = new JwkSet { Keys = [key.ToPublicJwk()] };
        var result = await new AgentCardVerifier(policy).VerifyAsync(raw, new Uri("https://agent.example/.well-known/agent-card.json"));
        Assert.Equal(AgentCardVerificationStatus.Verified, result.Status);
    }

    [Fact]
    public async Task Host_card_instance_is_not_mutated()
    {
        var hostCard = TestAgentHost.DefaultCard();
        hostCard.SupportedInterfaces = [];
        await using var host = await TestAgentHost.StartAsync(
            o => o.AgentCard = hostCard,
            services: s => s.AddA2AServer().AddExtensionOperation("x.noop", "urn:test:noop", (_, p, _) => ValueTask.FromResult(p), allow: true));
        var served = await ServedCardAsync(host);
        Assert.Equal(2, served.SupportedInterfaces.Count);
        Assert.Contains(served.Capabilities.Extensions!, e => e.Uri == "urn:test:noop");
        Assert.Empty(hostCard.SupportedInterfaces);
        Assert.DoesNotContain(hostCard.Capabilities.Extensions!, e => e.Uri == "urn:test:noop");
        Assert.NotSame(hostCard, host.Handler.AgentCard);
    }

    [Fact]
    public async Task Tenant_template_patterns_are_not_listed()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = TestAgentHost.DefaultCard();
            o.AgentCard.SupportedInterfaces = [];
            o.AllowAnonymous = true;
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);
        await using var app = builder.Build();
        app.MapA2A("/{tenant}/a2a");
        app.MapA2AJsonRpc("/rpc");
        await app.StartAsync();
        var card = app.Services.GetRequiredService<IAgentCardProvider>().Get();
        var only = Assert.Single(card.SupportedInterfaces);
        Assert.Equal(ProtocolBindings.JsonRpc, only.ProtocolBinding);
        Assert.Equal("http://localhost/rpc", only.Url);
        await app.StopAsync();
    }

    [Fact]
    public async Task Card_without_any_interface_fails_at_first_serve_with_a_clear_error()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddA2AServer(o => { o.AgentCard = TestAgentHost.DefaultCard(); o.AgentCard.SupportedInterfaces = []; })
            .AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);
        await using var app = builder.Build();
        app.MapAgentCard();
        await app.StartAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.GetTestClient().GetAsync(A2AProtocol.WellKnownAgentCardPath));
        Assert.Contains("SupportedInterfaces", ex.ToString());
        await app.StopAsync();
    }

    private const string NoNameMessage =
        "The Agent Card has no name. Call AddAgentCard(...) on the A2A builder or set A2AServerOptions.AgentCard.";

    [Fact]
    public async Task Startup_fails_when_the_card_has_no_name()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddA2AServer(o => o.AllowAnonymous = true).AddAgentExecutor<ScenarioAgent>(); // no AddAgentCard
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);
        await using var app = builder.Build();
        app.MapA2A("/a2a");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(NoNameMessage, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Core_provider_fails_at_first_use_when_the_card_has_no_name(string name)
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = name;
        var provider = new AgentCardProvider(options, []);
        Assert.Equal(NoNameMessage, Assert.Throws<InvalidOperationException>(() => provider.Get()).Message);

        options.AgentCard.Name = "Named";
        Assert.Equal("Named", provider.Get().Name);
    }
}
