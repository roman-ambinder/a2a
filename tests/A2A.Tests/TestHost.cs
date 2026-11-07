using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using A2A.Server.Push;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>Scenario agent driven by the first word of the user's text.</summary>
public sealed class ScenarioAgent : IAgentExecutor
{
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> AuthSignals = new();

    /// <summary>Signals for the <c>gate</c> scenario, keyed by the whole user text.</summary>
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> GateSignals = new();

    /// <summary>The signal for <paramref name="text"/> (a <c>gate ...</c> message).</summary>
    public static TaskCompletionSource Gate(string text) =>
        GateSignals.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var text = context.UserText;
        if (context.Task is not null && context.Task.Status.State == TaskState.InputRequired)
        {
            if (text.StartsWith("gate ", StringComparison.Ordinal)) await Gate(text).Task.WaitAsync(ct); // no WORKING first
            if (text == "ask-again") // stays interrupted: one more turn
            {
                await updater.RequireInputAsync("need even more", ct);
                return;
            }
            await updater.CompleteAsync($"got: {text}", ct);
            return;
        }

        switch (text.Split(' ')[0])
        {
            case "hello":
                await updater.ReplyAsync("hi there", ct);
                break;
            case "task":
                await updater.StartWorkAsync("working", ct);
                var id = await updater.AddArtifactAsync([Part.FromText("part1")], name: "result", cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText("part2")], artifactId: id, append: true, lastChunk: true, cancellationToken: ct);
                await updater.CompleteAsync("done", ct);
                break;
            case "input":
                await updater.RequireInputAsync("need more", ct);
                break;
            case "slow":
                await updater.StartWorkAsync(cancellationToken: ct);
                await Task.Delay(Timeout.Infinite, ct);
                break;
            case "auth":
                await updater.RequireAuthAsync("please authorize", ct);
                var tcs = AuthSignals.GetOrAdd(updater.TaskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                await tcs.Task.WaitAsync(ct);
                await updater.CompleteAsync("authorized", ct);
                break;
            case "gate":
                await updater.StartWorkAsync("waiting at the gate", ct);
                await Gate(text).Task.WaitAsync(ct);
                await updater.CompleteAsync("through the gate", ct);
                break;
            case "throw-on-cancel": // works until cancelled; its cancellation callback throws
                await updater.StartWorkAsync(cancellationToken: ct);
                using (ct.Register(() => throw new InvalidOperationException("cancellation callback failed")))
                    await Task.Delay(Timeout.Infinite, ct);
                break;
            case "halfway": // starts work, then returns without finishing the task
                await updater.StartWorkAsync(cancellationToken: ct);
                break;
            case "big":
                await updater.StartWorkAsync(cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText(new string('x', 2048))], name: "big", cancellationToken: ct);
                await updater.CompleteAsync("done", ct);
                break;
            case "fail":
                throw new InvalidOperationException("boom");
            case "stream-error": // fails after the first event, when a streaming response is already under way
                await updater.StartWorkAsync("about to fail", ct);
                throw new InvalidOperationException("boom");
            case "silent":
                break;
            default:
                await updater.CompleteAsync(text, ct);
                break;
        }
    }
}

/// <summary>
/// Authenticates requests carrying <c>X-Test-User</c>. With <c>X-Test-NoId</c> as well, the user is authenticated but has
/// neither a NameIdentifier nor a <c>sub</c> claim.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = Request.Headers.ContainsKey("X-Test-NoId")
            ? new[] { new Claim(ClaimTypes.Name, user) }
            : new[] { new Claim("sub", user), new Claim(ClaimTypes.Name, user) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Records webhook calls.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    /// <summary>The status to answer a call with. Default: 200 OK for every call.</summary>
    public Func<HttpRequestMessage, System.Net.HttpStatusCode> Status { get; set; } = _ => System.Net.HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue((request, body));
        return new HttpResponseMessage(Status(request));
    }
}

public sealed class TestAgentHost : IAsyncDisposable
{
    public const string BaseUrl = "http://localhost/a2a";
    public const string OptionalExtension = "urn:test:ext";

    private TestAgentHost(WebApplication app, RecordingHandler webhooks)
    {
        App = app;
        Webhooks = webhooks;
    }

    public WebApplication App { get; }
    public RecordingHandler Webhooks { get; }
    public A2ARequestHandler Handler => App.Services.GetRequiredService<A2ARequestHandler>();
    public InMemoryPushOutbox PushOutbox => (InMemoryPushOutbox)App.Services.GetRequiredService<PushOutbox>();

    public static AgentCard DefaultCard() => new()
    {
        Name = "Scenario Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities
        {
            Streaming = true, PushNotifications = true, ExtendedAgentCard = true,
            Extensions = [new AgentExtension { Uri = OptionalExtension, Description = "optional test extension" }],
        },
        SecuritySchemes = new() { ["bearer"] = SecurityScheme.Bearer() },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "scenario", Name = "Scenario", Description = "Runs test scenarios", Tags = ["test"] }],
    };

    public static async Task<TestAgentHost> StartAsync(Action<A2AServerOptions>? configure = null, Action<A2AHttpOptions>? configureHttp = null,
        Action<IServiceCollection>? services = null, bool requireAuth = true,
        Action<RouteGroupBuilder>? configureRoutes = null, bool useA2A = true, Action<WebApplication>? configurePipeline = null,
        bool grpc = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = DefaultCard();
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
            o.PushDelivery.InitialBackoff = TimeSpan.FromMilliseconds(10);
            o.PushDelivery.PollInterval = TimeSpan.FromMilliseconds(20);
            o.CancelGracePeriod = TimeSpan.FromSeconds(2);
            o.ExtendedAgentCardProvider = (call, _) =>
            {
                var card = DefaultCard();
                card.Description = $"Extended card for {call.Principal}";
                return ValueTask.FromResult<AgentCard?>(card);
            };
            configure?.Invoke(o);
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        if (grpc) builder.Services.AddA2AGrpc();
        services?.Invoke(builder.Services);
        var webhooks = new RecordingHandler();
        builder.Services.AddHttpClient(PushDeliveryService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => webhooks);
        if (configureHttp is not null) builder.Services.ConfigureA2AHttp(configureHttp);

        var app = builder.Build();
        configurePipeline?.Invoke(app);
        app.UseAuthentication();
        if (useA2A) app.UseA2A();
        app.UseAuthorization();
        app.MapAgentCard();
        var a2a = app.MapA2A("/a2a");
        if (requireAuth) a2a.RequireAuthorization();
        configureRoutes?.Invoke(a2a);
        if (grpc)
        {
            var g = app.MapA2AGrpc();
            if (requireAuth) g.RequireAuthorization();
        }
        await app.StartAsync();
        return new TestAgentHost(app, webhooks);
    }

    /// <summary>Raw HttpClient against the test server, optionally authenticated as <paramref name="user"/>.</summary>
    public HttpClient Http(string? user = null)
    {
        var client = App.GetTestClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    public A2AClient Client(string binding, string? user = "alice", A2AClientOptions? options = null)
    {
        options ??= new A2AClientOptions();
        IA2AClient transport = binding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(Http(user), new Uri(BaseUrl), options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(Http(user), new Uri(BaseUrl), options),
            ProtocolBindings.Grpc => new GrpcTransport(GrpcChannel(user).CreateCallInvoker(), options),
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, null),
        };
        return new A2AClient(transport, options, DefaultCard());
    }

    /// <summary>A gRPC channel to the test server (start the host with <c>grpc: true</c>), optionally authenticated as <paramref name="user"/>.</summary>
    public global::Grpc.Net.Client.GrpcChannel GrpcChannel(string? user = null)
    {
        HttpMessageHandler handler = App.GetTestServer().CreateHandler();
        if (user is not null) handler = new TestUserHandler(user) { InnerHandler = handler };
        return global::Grpc.Net.Client.GrpcChannel.ForAddress(App.GetTestServer().BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class TestUserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-User", user);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Waits until no agent runs and every committed push delivery was sent or dead-lettered.</summary>
    public async Task WaitIdleAsync()
    {
        await Handler.WhenIdleAsync();
        await WaitForPushIdleAsync();
    }

    /// <summary>Waits until the push outbox is empty: every delivery was sent, dropped or dead-lettered.</summary>
    public async Task WaitForPushIdleAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (PushOutbox.Count > 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{PushOutbox.Count} push deliveries are still pending.");
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Test helpers to narrow the closed response unions.</summary>
public static class UnionTestExtensions
{
    public static AgentTask RequireTask(this SendMessageResponse r) =>
        (r as TaskResponse)?.Task ?? throw new Xunit.Sdk.XunitException($"Expected a task response but got {r.GetType().Name}");

    public static Message RequireMessage(this SendMessageResponse r) =>
        (r as MessageResponse)?.Message ?? throw new Xunit.Sdk.XunitException($"Expected a message response but got {r.GetType().Name}");

    public static AgentTask? TryTask(this SendMessageResponse r) => (r as TaskResponse)?.Task;
}
