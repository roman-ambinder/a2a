using System.Collections.Concurrent;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>
///     The startup check of the Agent Card's interfaces (spec 5.2, 8.3.1), of encryption (13.4) and of its required
///     arrays (5.7). It only warns: the app
///     always starts.
/// </summary>
public class CardInterfaceCheckTests
{
    private const int Local = 702, NotOnCard = 703, TenantPattern = 704, Plain = 705, NoHttps = 706, EmptyArray = 708;

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<(int Id, string Message)> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Events.Enqueue((eventId.Id, formatter(state, exception)));
        }

        public List<string> Of(int id) => Events.Where(e => e.Id == id).Select(e => e.Message).ToList();
    }

    private static async Task<Capture> StartAsync(string environment, Action<AgentCard>? card = null,
        Uri? publicBaseUrl = null, Action<WebApplication>? map = null, bool kestrel = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        if (kestrel) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var log = new Capture();
        builder.Logging.AddProvider(log);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = TestAgentHost.DefaultCard();
            o.AgentCard.SupportedInterfaces = [];
            card?.Invoke(o.AgentCard);
            o.AllowAnonymous = true;
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);
        if (publicBaseUrl is not null) builder.Services.ConfigureA2AHttp(h => h.PublicBaseUrl = publicBaseUrl);
        var app = builder.Build();
        if (map is null) app.MapA2A("/a2a");
        else map(app);
        await app.StartAsync();
        await app.StopAsync();
        await app.DisposeAsync();
        return log;
    }

    [Fact]
    public async Task A_card_on_localhost_warns_in_production()
    {
        var log = await StartAsync(Environments.Production);

        Assert.Equal(2, log.Of(Local).Count); // JSON-RPC and HTTP+JSON, completed at http://localhost
        Assert.Contains(log.Of(Local), m => m.Contains("PublicBaseUrl", StringComparison.Ordinal));
        Assert.Empty(log.Of(NotOnCard));
    }

    [Fact]
    public async Task Development_does_not_warn_about_local_or_plain_addresses()
    {
        var log = await StartAsync(Environments.Development);

        Assert.Empty(log.Of(Local));
        Assert.Empty(log.Of(Plain));
        Assert.Empty(log.Of(NoHttps));
    }

    [Fact]
    public async Task A_public_https_base_url_gives_no_warning()
    {
        var log = await StartAsync(Environments.Production, publicBaseUrl: new Uri("https://agent.example"));

        Assert.DoesNotContain(log.Events, e => e.Id is >= Local and <= NoHttps);
    }

    [Fact]
    public async Task A_public_plain_http_interface_warns_once_per_interface()
    {
        var log = await StartAsync(Environments.Production, publicBaseUrl: new Uri("http://agent.example"));

        Assert.Equal(2, log.Of(Plain).Count);
        Assert.Empty(log.Of(NoHttps)); // already said per interface
    }

    [Fact]
    public async Task A_mapped_binding_missing_from_a_host_card_warns()
    {
        var log = await StartAsync(Environments.Production, card: c => c.SupportedInterfaces =
            [new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }]);

        var missing = Assert.Single(log.Of(NotOnCard));
        Assert.Contains(ProtocolBindings.HttpJson, missing, StringComparison.Ordinal);
        Assert.Empty(log.Of(Local));
    }

    [Fact]
    public async Task A_tenant_pattern_warns_that_it_is_not_listed()
    {
        var log = await StartAsync(Environments.Production, map: app =>
        {
            app.MapA2A("/{tenant}/a2a");
            app.MapA2AJsonRpc("/rpc");
        });

        Assert.Equal(2, log.Of(TenantPattern).Count); // JSON-RPC and HTTP+JSON at the tenant pattern
        Assert.Single(log.Of(Local)); // the plain /rpc binding, completed at localhost
        Assert.Empty(log.Of(NotOnCard));
    }

    [Fact]
    public async Task The_default_tenant_routes_of_MapA2A_do_not_warn()
    {
        var log = await StartAsync(Environments.Production, publicBaseUrl: new Uri("https://agent.example"));

        Assert.Empty(log.Of(TenantPattern));
    }

    [Fact]
    public async Task A_card_with_every_required_array_filled_gives_no_array_warning()
    {
        var log = await StartAsync(Environments.Development);

        Assert.Empty(log.Of(EmptyArray));
    }

    [Fact]
    public async Task Each_empty_required_array_of_the_card_warns_in_every_environment()
    {
        var log = await StartAsync(Environments.Development, card: c =>
        {
            c.DefaultInputModes = [];
            c.DefaultOutputModes = [];
            c.Skills = [new AgentSkill { Id = "a", Name = "A", Description = "a", Tags = ["x"] }, new AgentSkill { Id = "b", Name = "B", Description = "b" }];
        });

        var warnings = log.Of(EmptyArray);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, m => m.Contains("defaultInputModes", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("defaultOutputModes", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("skills[id=b].tags", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_card_with_no_skills_warns()
    {
        var log = await StartAsync(Environments.Production, card: c => c.Skills = []);

        Assert.Contains("skills is empty", Assert.Single(log.Of(EmptyArray)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_with_only_plain_http_addresses_warns()
    {
        // The card is on a public https address, so no interface warning; the listen address itself is plain HTTP.
        var log = await StartAsync(Environments.Production, kestrel: true, card: c => c.SupportedInterfaces =
        [
            new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
        ]);

        var warning = Assert.Single(log.Of(NoHttps));
        Assert.Contains("http://127.0.0.1:", warning, StringComparison.Ordinal);
    }
}
