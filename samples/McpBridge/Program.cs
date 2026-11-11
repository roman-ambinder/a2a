using System.ComponentModel;
using A2A;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Mcp;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Skills;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

// Two modes, chosen by the first argument:
//   expose  A small A2A agent, served to MCP clients as MCP tools at /mcp.
//   call    An MCP server with an "add" tool at /mcp, run as an A2A agent at /a2a.
// No API keys, no external calls. The server listens on 127.0.0.1 only.

if (args.Length == 0 || args[0] is not ("expose" or "call"))
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/McpBridge -- <expose|call>");
    return 1;
}

const string url = "http://127.0.0.1:5290"; // loopback only
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

if (args[0] == "expose")
{
    var card = new AgentCard
    {
        Name = "Text Agent",
        Description = "Changes text. Two skills: shout and reverse.",
        Version = "1.0.0",
        Capabilities = new AgentCapabilities { Extensions = [SkillSelection.CreateExtension()] },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills =
        [
            new AgentSkill { Id = "shout", Name = "Shout", Description = "Makes the text upper case", Tags = ["text"] },
            new AgentSkill { Id = "reverse", Name = "Reverse", Description = "Reverses the text", Tags = ["text"] }
        ]
    };

    // The A2A agent. AllowAnonymous is fine for a sample that only listens on loopback.
    builder.Services.AddA2A(a => a.AddAgentExecutor<TextAgent>().AddAgentCard(card))
        .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

    // The bridge. The A2A client has its own credentials (none here). Nothing from the MCP caller is forwarded.
    var options = new A2AClientOptions();
    var http = new HttpClient();
    var client = new A2AClient(new JsonRpcTransport(http, new Uri($"{url}/a2a"), options), options, card);
    #region Expose
    var tools = client.AsMcpTools(new A2AMcpToolOptions { ToolNamePrefix = "text_" });

    // /mcp is open here. A real MCP server needs authentication before it serves these tools.
    builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);
    #endregion
}
else
{
    // The MCP server with one tool, and an A2A agent that runs it. Both live in this process only to keep the sample small.
    builder.Services.AddMcpServer().WithHttpTransport().WithTools<MathTools>();

    // The Agent Card, written by hand. In a real host, list the tools of the client with
    // McpToolSkills.ToAgentSkills(await mcpClient.ListToolsAsync()), filtered to the allowlist.
    var card = new AgentCard
    {
        Name = "Math Agent",
        Description = "Runs the add tool of an MCP server. Send a JSON data part such as {\"a\":2,\"b\":3}.",
        Version = "1.0.0",
        Capabilities = new AgentCapabilities { Extensions = [SkillSelection.CreateExtension()] },
        DefaultInputModes = ["application/json"],
        DefaultOutputModes = ["text/plain"],
        Skills =
        [
            new AgentSkill
            {
                Id = "add", Name = "add", Description = "Adds two numbers", Tags = ["mcp"],
                InputModes = ["application/json"]
            }
        ]
    };

    // The host owns the MCP client and its credentials (none here). It connects once the server is up.
    var holder = new McpClientHolder();
    builder.Services.AddSingleton<McpClient>(_ =>
        holder.Client ?? throw new InvalidOperationException("The MCP client is not ready."));
    builder.Services.AddA2A(a => a
            .AddAgentCard(card)
            .AddMcpToolAgent(o => o.AllowTool("add")))
        .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);
    builder.Services.AddSingleton(holder);
}

var app = builder.Build();
app.UseA2A();
app.MapMcp("/mcp");
app.MapA2A("/a2a");

await app.StartAsync();

if (args[0] == "call")
{
    var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{url}/mcp") });
    app.Services.GetRequiredService<McpClientHolder>().Client = await McpClient.CreateAsync(transport);
}

Console.WriteLine($"Mode:  {args[0]}");
Console.WriteLine($"MCP:   {url}/mcp");
Console.WriteLine($"A2A:   {url}/a2a");
// Printed once the MCP client of call mode is connected: samples/run-samples.sh waits for this line.
Console.WriteLine("Press Ctrl+C to stop.");
await app.WaitForShutdownAsync();
return 0;

/// <summary>The A2A agent of the expose mode. It reads the skill the caller chose.</summary>
internal sealed class TextAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var text = context.UserText;
        var answer = context.RequestedSkillId switch
        {
            "shout" => text.ToUpperInvariant(),
            "reverse" => new string(text.Reverse().ToArray()),
            _ => "Pick a skill: shout or reverse."
        };
        return updater.CompleteAsync(answer, cancellationToken).AsTask();
    }
}

/// <summary>The tool of the call mode.</summary>
[McpServerToolType]
internal sealed class MathTools
{
    [McpServerTool(Name = "add")]
    [Description("Adds two numbers.")]
    public static double Add(double a, double b)
    {
        return a + b;
    }
}

/// <summary>Holds the MCP client, which can only be created after the server listens.</summary>
internal sealed class McpClientHolder
{
    public McpClient? Client { get; set; }
}