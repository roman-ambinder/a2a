using System.Collections.Concurrent;
using A2A.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace A2A.Tests.Credentials;

internal sealed record SeenRequest(string Path, IReadOnlyDictionary<string, string> Headers, string Body = "")
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A real Kestrel listener on 127.0.0.1 that records every request it receives.</summary>
internal sealed class TestOrigin : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestOrigin(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    public Uri Url { get; }
    public ConcurrentQueue<SeenRequest> Requests { get; } = new();

    public IEnumerable<SeenRequest> At(string path) => Requests.Where(r => r.Path == path);

    public Uri Uri(string path) => new(Url, path);

    public static async Task<TestOrigin> StartAsync(Func<HttpContext, TestOrigin, Task> handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        TestOrigin? origin = null;
        app.Run(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            origin!.Requests.Enqueue(new SeenRequest(context.Request.Path.Value ?? "",
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            await handler(context, origin);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        origin = new TestOrigin(app, new Uri(address));
        return origin;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    public static AgentCard Card(Uri agentOrigin, Dictionary<string, SecurityScheme> schemes, List<SecurityRequirement> requirements,
        Uri? interfaceOrigin = null) => new()
        {
            Name = "agent", Description = "d", Version = "1",
            SupportedInterfaces = [new AgentInterface { Url = new Uri(interfaceOrigin ?? agentOrigin, "/a2a").ToString(), ProtocolBinding = ProtocolBindings.JsonRpc }],
            SecuritySchemes = schemes,
            SecurityRequirements = requirements,
        };

    public static async Task WriteJson(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    public static Task WriteCard(HttpContext context, AgentCard card) => WriteJson(context, A2AJson.Serialize(card));

    public const string RpcOk = """{"jsonrpc":"2.0","id":"x","result":{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}}}""";
}
