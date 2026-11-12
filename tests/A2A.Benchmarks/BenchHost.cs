using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Benchmarks;

/// <summary>An in-process A2A server (TestServer) plus a JSON-RPC client wired to it.</summary>
internal sealed class BenchHost : IAsyncDisposable
{
    private const string BaseUrl = "http://localhost/a2a";
    private readonly WebApplication _app;

    private BenchHost(WebApplication app, A2AClient client)
    {
        _app = app;
        Client = client;
    }

    public A2AClient Client { get; }

    public static async Task<BenchHost> StartAsync<TExecutor>(Action<A2AServerOptions>? configure = null,
        Action<IServiceCollection>? services = null)
        where TExecutor : class, IAgentExecutor
    {
        var card = new AgentCard
        {
            Name = "Bench Agent", Description = "Benchmark agent", Version = "1.0.0",
            SupportedInterfaces = [new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc }],
            Capabilities = new AgentCapabilities { Streaming = true },
            DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "bench", Name = "Bench", Description = "Benchmark", Tags = ["bench"] }],
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services); // before AddA2AServer, so its TryAdd defaults give way
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = card;
            o.AllowAnonymous = true;
            configure?.Invoke(o);
        }).AddAgentExecutor<TExecutor>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2ARateLimiting(o => o.Enabled = false);

        var app = builder.Build();
        app.MapA2A("/a2a");
        await app.StartAsync();

        var options = new A2AClientOptions();
        var transport = new JsonRpcTransport(app.GetTestClient(), new Uri(BaseUrl), options);
        return new BenchHost(app, new A2AClient(transport, options, card));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
