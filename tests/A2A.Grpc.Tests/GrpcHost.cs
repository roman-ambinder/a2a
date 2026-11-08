using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A.Client;
using A2A.Client.Grpc;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Grpc.Tests;

internal sealed class ScenarioAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        if (context.Task?.Status.State == TaskState.InputRequired)
        {
            await updater.CompleteAsync($"got: {context.UserText}", ct);
            return;
        }
        switch (context.UserText)
        {
            case "hello": await updater.ReplyAsync("hi there", ct); break;
            case "task":
                await updater.StartWorkAsync("working", ct);
                await updater.AddArtifactAsync([Part.FromText("part1")], artifactId: "a1", name: "result", cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText("part2")], artifactId: "a1", append: true, lastChunk: true, cancellationToken: ct);
                await updater.CompleteAsync("done", ct);
                break;
            case "input": await updater.RequireInputAsync("need more", ct); break;
            case "slow":
                await updater.StartWorkAsync(cancellationToken: ct);
                await Task.Delay(Timeout.Infinite, ct);
                break;
            default: await updater.CompleteAsync(context.UserText, ct); break;
        }
    }
}

/// <summary>Authenticates requests that carry <c>x-test-user</c> (as the <c>sub</c> claim).</summary>
internal sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["x-test-user"].ToString();
        if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var id = new ClaimsIdentity([new Claim("sub", user)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
    }
}

/// <summary>An in-process A2A gRPC server on the ASP.NET Core TestServer, hosted like a production app.</summary>
internal sealed class GrpcHost : IAsyncDisposable
{
    public const string Extension = "urn:test:ext";
    private readonly WebApplication _app;

    private GrpcHost(WebApplication app) => _app = app;

    public IServiceProvider Services => _app.Services;

    public static AgentCard Card() => new()
    {
        Name = "gRPC Scenario Agent", Description = "test", Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "http://localhost", ProtocolBinding = ProtocolBindings.Grpc }],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true, ExtendedAgentCard = true, Extensions = [new AgentExtension { Uri = Extension }] },
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "s", Tags = ["t"] }],
    };

    /// <param name="configure">Server options on top of the defaults.</param>
    /// <param name="services">Extra service registrations.</param>
    /// <param name="requireAuth">Calls <c>RequireAuthorization()</c> on the gRPC endpoints.</param>
    /// <param name="useA2A">Adds the A2A middleware (rate limiter).</param>
    /// <param name="aspNetCore">Calls <c>AddA2AServerAspNetCore()</c>.</param>
    /// <param name="configureEndpoints">Runs on the gRPC endpoint convention builder after mapping.</param>
    /// <param name="configurePipeline">Runs first in the request pipeline.</param>
    /// <param name="server">Extra server registrations on the <see cref="A2AServerBuilder"/> (filters, stores).</param>
    public static async Task<GrpcHost> StartAsync(Action<A2AServerOptions>? configure = null, Action<IServiceCollection>? services = null,
        bool requireAuth = true, bool useA2A = true, bool aspNetCore = true,
        Action<IEndpointConventionBuilder>? configureEndpoints = null, Action<WebApplication>? configurePipeline = null,
        Action<A2AServerBuilder>? server = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        var a2a = builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = Card();
            o.CancelGracePeriod = TimeSpan.FromSeconds(2);
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
            o.ExtendedAgentCardProvider = (call, _, _) =>
            {
                var c = Card();
                c.Description = $"Extended for {call.Principal}";
                return ValueTask.FromResult<AgentCard?>(c);
            };
            configure?.Invoke(o);
        }).AddAgentExecutor<ScenarioAgent>();
        server?.Invoke(a2a);
        if (aspNetCore) builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddA2AGrpc();
        services?.Invoke(builder.Services);
        var app = builder.Build();
        configurePipeline?.Invoke(app);
        app.UseAuthentication();
        if (useA2A) app.UseA2A();
        app.UseAuthorization();
        app.MapAgentCard();
        var grpc = app.MapA2AGrpc();
        if (requireAuth) grpc.RequireAuthorization();
        configureEndpoints?.Invoke(grpc);
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        return new GrpcHost(app);
    }

    public GrpcChannel Channel(string? user = null)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        if (user is not null) handler = new UserHandler(user) { InnerHandler = handler };
        return GrpcChannel.ForAddress(_app.GetTestServer().BaseAddress, new GrpcChannelOptions { HttpHandler = handler, MaxSendMessageSize = null });
    }

    public HttpMessageHandler Handler(string? user = null)
    {
        HttpMessageHandler handler = _app.GetTestServer().CreateHandler();
        return user is null ? handler : new UserHandler(user) { InnerHandler = handler };
    }

    public Uri BaseAddress => _app.GetTestServer().BaseAddress;

    /// <summary>A raw generated client (no A2A metadata is added for you).</summary>
    public Lf.A2a.V1.A2AService.A2AServiceClient Raw(string? user = "alice") => new(Channel(user));

    public A2AClient Client(string? user = "alice", A2AClientOptions? options = null)
    {
        options ??= new A2AClientOptions();
        return new A2AClient(new GrpcTransport(Channel(user).CreateCallInvoker(), options), options, Card());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class UserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("x-test-user", user);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
