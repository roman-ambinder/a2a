using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.V0_3Compat;
using A2A.Signing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.V0_3Compat.Tests;

/// <summary>Scenario agent driven by the first word of the user's text.</summary>
public sealed class ScenarioAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var text = context.UserText;
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
            default:
                await updater.CompleteAsync(text, ct);
                break;
        }
    }
}

/// <summary>Authenticates requests that carry <c>X-Test-User</c>.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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

/// <summary>An in-memory agent host with <c>MapA2A("/a2a", o => o.EnableV03Compat())</c> (or without compat).</summary>
public sealed class V03Host : IAsyncDisposable
{
    public const string BaseUrl = "http://localhost/a2a";
    public const string OptionalExtension = "urn:test:ext";

    private V03Host(WebApplication app) => App = app;

    public WebApplication App { get; }

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
        SecurityRequirements = [new SecurityRequirement { Schemes = new() { ["bearer"] = new StringList() } }],
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "scenario", Name = "Scenario", Description = "Runs test scenarios", Tags = ["test"] }],
    };

    public static async Task<V03Host> StartAsync(bool compat = true, bool requireAuth = true, Action<A2AServerOptions>? configure = null,
        Action<A2AHttpOptions>? configureHttp = null, string pattern = "/a2a", Action<IServiceCollection>? services = null,
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
            o.CancelGracePeriod = TimeSpan.FromMilliseconds(300);
            // No shutdown drain: disposing a host whose agent is still running would otherwise wait the default 20 s.
            o.ShutdownDrainTimeout = TimeSpan.Zero;
            o.ExtendedAgentCardProvider = (call, _, _) =>
            {
                var card = DefaultCard();
                card.Description = $"Extended card for {call.Principal}";
                return ValueTask.FromResult<AgentCard?>(card);
            };
            configure?.Invoke(o);
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        if (grpc) A2A.Server.Grpc.V0_3Compat.A2AGrpcV03CompatExtensions.AddA2AGrpcV03Compat(builder.Services);
        services?.Invoke(builder.Services);
        if (configureHttp is not null) builder.Services.ConfigureA2AHttp(configureHttp);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var group = compat ? app.MapA2A(pattern, o => o.EnableV03Compat()) : app.MapA2A(pattern);
        if (requireAuth) group.RequireAuthorization();
        if (grpc)
        {
            var v1 = A2A.Server.Grpc.A2AGrpcExtensions.MapA2AGrpc(app);
            var v03 = A2A.Server.Grpc.V0_3Compat.A2AGrpcV03CompatExtensions.MapA2AGrpcV03Compat(app);
            if (requireAuth)
            {
                v1.RequireAuthorization();
                v03.RequireAuthorization();
            }
        }

        await app.StartAsync();
        return new V03Host(app);
    }

    /// <summary>A gRPC channel to the test server (start with <c>grpc: true</c>), authenticated as <paramref name="user"/>.</summary>
    public global::Grpc.Net.Client.GrpcChannel GrpcChannel(string? user = "alice")
    {
        HttpMessageHandler handler = App.GetTestServer().CreateHandler();
        if (user is not null) handler = new UserHandler(user) { InnerHandler = handler };
        return global::Grpc.Net.Client.GrpcChannel.ForAddress(App.GetTestServer().BaseAddress,
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class UserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-User", user);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A client for the test server, authenticated as <paramref name="user"/> (null: anonymous).</summary>
    public HttpClient Http(string? user = "alice")
    {
        var client = App.GetTestClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }

    // ---- request helpers ----

    public static StringContent Json(string json, string mediaType = "application/json") => new(json, Encoding.UTF8, mediaType);

    /// <summary>Posts a v0.3 JSON-RPC request to <paramref name="path"/>; returns the status and the parsed body.</summary>
    public static async Task<(HttpResponseMessage Response, JsonObject Body)> RpcAsync(HttpClient http, string method, JsonNode? parameters,
        string path = "/a2a", string? version = null, JsonNode? id = null, string? extensions = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id ?? 1, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(request.ToJsonString()) };
        if (version is not null) message.Headers.Add("A2A-Version", version);
        if (extensions is not null) message.Headers.Add("A2A-Extensions", extensions);
        var response = await http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        return (response, (JsonObject)JsonNode.Parse(text)!);
    }

    public static JsonObject SendParams(string text, bool? blocking = null, string? contextId = null, JsonObject? extra = null)
    {
        var message = new JsonObject
        {
            ["kind"] = "message", ["messageId"] = Guid.NewGuid().ToString(), ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = text }),
        };
        if (contextId is not null) message["contextId"] = contextId;
        var p = new JsonObject { ["message"] = message };
        if (blocking is { } b) p["configuration"] = new JsonObject { ["blocking"] = b };
        if (extra is not null) foreach (var (k, v) in extra) p[k] = v?.DeepClone();
        return p;
    }

    /// <summary>The proto-dialect body of a send request.</summary>
    public static string RestSendBody(string text, bool? blocking = null) =>
        new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["messageId"] = Guid.NewGuid().ToString(), ["role"] = "ROLE_USER",
                ["content"] = new JsonArray(new JsonObject { ["text"] = text }),
            },
            ["configuration"] = blocking is { } b ? new JsonObject { ["blocking"] = b } : null,
        }.ToJsonString();

    /// <summary>Reads every <c>data:</c> frame of an SSE response until the stream ends.</summary>
    public static async Task<List<JsonObject>> ReadSseAsync(HttpResponseMessage response, int max = 50, CancellationToken ct = default)
    {
        var frames = new List<JsonObject>();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (frames.Count < max && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) frames.Add((JsonObject)JsonNode.Parse(line[5..].Trim())!);
        }
        return frames;
    }

    public static async Task<HttpResponseMessage> StreamAsync(HttpClient http, HttpRequestMessage request) =>
        await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    public static HttpRequestMessage RpcStream(string method, JsonNode parameters, string path = "/a2a")
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s1", ["method"] = method, ["params"] = parameters }.ToJsonString();
        return new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
    }

    public static AgentCardSigningKey NewKey() => new(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), "k1", "ES256");
}
