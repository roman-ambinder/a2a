using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Stores.Tests;

/// <summary>
/// A real A2A server in the test process (in-memory stores, TestServer), standing in for a peer agent. Its agent starts work,
/// waits for the test to release it, and completes. It counts the executions, so a test can see how many tasks the peer created.
/// </summary>
public sealed class ClientOutboxPeer : IAsyncDisposable
{
    private const string BaseUrl = "http://localhost/a2a";

    private readonly WebApplication _app;

    private ClientOutboxPeer(WebApplication app, PeerState state)
    {
        _app = app;
        State = state;
    }

    /// <summary>The signals of the peer's agent.</summary>
    public PeerState State { get; }

    public static async Task<ClientOutboxPeer> StartAsync()
    {
        var state = new PeerState();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(state);
        builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o => o.AgentCard = Card()).AddAgentExecutor<PeerAgent>();
        builder.Services.AddA2AServerAspNetCore();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        await app.StartAsync();
        return new ClientOutboxPeer(app, state);
    }

    /// <summary>A client of the peer, signed in as <c>alice</c>.</summary>
    public A2AClient Client()
    {
        var http = _app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        var options = new A2AClientOptions();
        return new A2AClient(new JsonRpcTransport(http, new Uri(BaseUrl), options), options, Card());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static AgentCard Card() => new()
    {
        Name = "Peer",
        Description = "Test peer",
        Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "work", Name = "Work", Description = "Works when the test says so", Tags = ["test"] }],
    };

    private sealed class PeerAgent(PeerState state) : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Interlocked.Increment(ref state.RunCount);
            await updater.StartWorkAsync("working", ct);
            state.Started.TrySetResult();
            await state.Gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("done", ct);
            state.Completed.TrySetResult();
        }
    }

    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].ToString();
            if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", user), new Claim(ClaimTypes.Name, user)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

/// <summary>What the peer's agent does and what the test can wait for.</summary>
public sealed class PeerState
{
    internal int RunCount;

    /// <summary>Completes when an agent run has started (its task is WORKING).</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The test completes it to let the agent finish its task.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after the agent stored the COMPLETED status.</summary>
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many times the agent ran. One per task the peer created.</summary>
    public int Runs => Volatile.Read(ref RunCount);
}
