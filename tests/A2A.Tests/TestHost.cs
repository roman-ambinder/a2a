using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

/// <summary>Scenario agent driven by the first word of the user's text.</summary>
public sealed class ScenarioAgent : IAgentExecutor
{
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> AuthSignals = new();

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var text = context.UserText;
        if (context.Task is not null && context.Task.Status.State == TaskState.InputRequired)
        {
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
            case "fail":
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue((request, body));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
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
    public HttpPushNotificationSender PushSender => App.Services.GetRequiredService<HttpPushNotificationSender>();

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
        Action<RouteGroupBuilder>? configureRoutes = null, bool useA2A = true)
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
            o.PushNotifications.InitialBackoff = TimeSpan.FromMilliseconds(10);
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
        services?.Invoke(builder.Services);
        var webhooks = new RecordingHandler();
        builder.Services.AddHttpClient(HttpPushNotificationSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => webhooks);
        if (configureHttp is not null) builder.Services.ConfigureA2AHttp(configureHttp);

        var app = builder.Build();
        app.UseAuthentication();
        if (useA2A) app.UseA2A();
        app.UseAuthorization();
        app.MapAgentCard();
        var a2a = app.MapA2A("/a2a");
        if (requireAuth) a2a.RequireAuthorization();
        configureRoutes?.Invoke(a2a);
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
        var http = Http(user);
        IA2AClient transport = binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri(BaseUrl), options)
            : new HttpJsonTransport(http, new Uri(BaseUrl), options);
        return new A2AClient(transport, options, DefaultCard());
    }

    public async Task WaitIdleAsync()
    {
        await Handler.WhenIdleAsync();
        await PushSender.WhenIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
