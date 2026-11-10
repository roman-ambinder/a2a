using System.Net.Http.Headers;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace A2A.Mcp.Tests;

/// <summary>
/// Both directions over real HTTP on loopback. The downstream server writes down the headers it received. The caller's
/// credentials must not be among them: the downstream sees only the credential of the bridge itself.
/// </summary>
public sealed class CredentialsTests
{
    private const string CallerToken = "caller-token-111";
    private const string CallerCookie = "session=caller-cookie-222";
    private const string BridgeToken = "bridge-token-999";

    /// <summary>The headers of every request a server received.</summary>
    private sealed class Seen
    {
        public List<Dictionary<string, string>> Requests { get; } = [];

        public void Record(Microsoft.AspNetCore.Http.HttpContext context)
        {
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            lock (Requests) Requests.Add(headers);
        }
    }

    private static async Task<WebApplication> StartKestrel(Action<WebApplicationBuilder> services, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0: the OS picks a free one
        builder.Logging.ClearProviders();
        services(builder);
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static Uri AddressOf(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    private static void AssertOnlyBridgeCredential(Seen seen)
    {
        List<Dictionary<string, string>> requests;
        lock (seen.Requests) requests = seen.Requests.ToList();
        Assert.NotEmpty(requests);
        foreach (var headers in requests)
        {
            Assert.Equal("Bearer " + BridgeToken, headers["Authorization"]);
            Assert.False(headers.ContainsKey("Cookie"));
            Assert.DoesNotContain(headers, h => h.Value.Contains("caller-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Expose_direction_does_not_forward_the_caller_credentials_to_the_a2a_agent()
    {
        // Downstream: a plain A2A agent.
        var downstream = new Seen();
        await using var agent = await StartKestrel(
            b =>
            {
                b.Services.AddAuthorization();
                b.Services.AddA2A(a => a.AddAgentCard(Cards.With("echo")).AddAgentExecutor((ctx, updater, ct) => updater.ReplyAsync("echo: " + ctx.UserText, ct).AsTask()))
                    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);
            },
            app =>
            {
                app.Use(async (context, next) => { downstream.Record(context); await next(); });
                app.UseA2A();
                app.UseAuthorization();
                app.MapA2A("/a2a");
            });

        // The bridge's own credential lives in its A2A client.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BridgeToken);
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(http, new Uri(AddressOf(agent), "/a2a"), options), options, Cards.With("echo"));

        // The bridge: an MCP server over HTTP that serves the A2A skills.
        var tools = client.AsMcpTools();
        await using var bridge = await StartKestrel(
            b => b.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools(tools),
            app => app.MapMcp("/mcp"));

        // The MCP caller sends its own credentials to the bridge.
        await using var caller = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(AddressOf(bridge), "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + CallerToken, ["Cookie"] = CallerCookie },
        }));
        var result = await caller.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "hi" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("completed", result.StructuredContent!.Value.GetProperty("state").GetString());
        Assert.Equal("echo: hi", result.StructuredContent!.Value.GetProperty("remoteContent").GetString());
        AssertOnlyBridgeCredential(downstream);
    }

    [Fact]
    public async Task Call_direction_does_not_forward_the_caller_credentials_to_the_mcp_server()
    {
        // Downstream: a plain MCP server.
        var downstream = new Seen();
        var tool = new ScriptedTool("echo", (_, _) => ValueTask.FromResult(ScriptedTool.Text("tool says hi")));
        await using var mcpServer = await StartKestrel(
            b => b.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools([tool]),
            app =>
            {
                app.Use(async (context, next) => { downstream.Record(context); await next(); });
                app.MapMcp("/mcp");
            });

        // The bridge's own credential lives in its MCP client.
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(AddressOf(mcpServer), "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + BridgeToken },
        }));

        // The bridge: an A2A server over HTTP that runs the MCP tool.
        var skills = McpToolSkills.ToAgentSkills(await mcp.ListToolsAsync());
        var card = Cards.With();
        card.Skills = skills.ToList();
        await using var a2a = await StartKestrel(
            b =>
            {
                b.Services.AddSingleton(mcp);
                b.Services.AddAuthorization();
                b.Services.AddA2A(a => a.AddAgentCard(card).AddMcpToolAgent(o => o.AllowTool("echo")))
                    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);
            },
            app =>
            {
                app.UseA2A();
                app.UseAuthorization();
                app.MapA2A("/a2a");
            });

        // The A2A caller sends its own credentials to the bridge.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CallerToken);
        http.DefaultRequestHeaders.Add("Cookie", CallerCookie);
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(http, new Uri(AddressOf(a2a), "/a2a"), options), options, card);
        var message = new Message { MessageId = Message.NewId(), Role = Role.User, Parts = [Part.FromData(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())] };
        var response = await client.SendMessageAsync(message).WaitAsync(Waiting.Limit);

        var task = Assert.IsType<TaskResponse>(response).Task;
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal("tool says hi", Assert.IsType<TextPart>(Assert.Single(Assert.Single(task.Artifacts!).Parts)).Text);
        AssertOnlyBridgeCredential(downstream);
    }
}
