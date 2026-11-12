using A2A.Client;
using A2A.Client.Grpc;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Snippets.Bindings;

internal static class Samples
{
    public static void MapEachBinding(WebApplication app)
    {
        #region MapBoth
        app.MapA2A("/a2a").RequireAuthorization();
        #endregion
    }

    public static void MapSeparately(WebApplication app)
    {
        #region MapSeparately
        app.MapA2AJsonRpc("/rpc").RequireAuthorization();
        app.MapA2AHttpJson("/rest").RequireAuthorization();
        app.MapAgentCard();
        #endregion
    }

    public static void MapGrpc(WebApplicationBuilder builder, AgentCard card)
    {
        #region GrpcServer
        builder.Services.AddA2AServer()
            .AddAgentExecutor<Agent>()
            .AddAgentCard(card);
        builder.Services.AddA2AGrpc();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2AGrpc().RequireAuthorization();
        #endregion
    }

    public static void Http2Port(WebApplicationBuilder builder)
    {
        #region Http2
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(5101, listen => listen.Protocols = HttpProtocols.Http2));
        #endregion
    }

    public static AgentCard CardWithTwoBindings()
    {
        #region CardInterfaces
        return new AgentCard
        {
            Name = "My Agent",
            Description = "Says hello.",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = "https://agent.example.com:443", ProtocolBinding = ProtocolBindings.Grpc },
                new AgentInterface { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
            ],
        };
        #endregion
    }

    public static async Task CreateClients(Uri agentUrl, HttpClient httpClient)
    {
        #region ClientPicksFromCard
        var client = await A2AClientFactory.CreateAsync(agentUrl, httpClient, new A2AClientOptions().UseGrpc());
        #endregion

        #region ClientRequiresGrpc
        var grpcClient = await A2AGrpcClientFactory.CreateAsync(agentUrl, httpClient);
        #endregion

        _ = client;
        _ = grpcClient;
    }

    public static async Task CallOptions(A2AClient client)
    {
        #region RequestOptions
        var options = new A2ARequestOptions
        {
            Headers = { ["X-Request-Source"] = "docs" },
            MaxRetries = 0,
        };
        var reply = await client.SendMessageAsync("hello", options: options);
        #endregion

        _ = reply;
    }
}

internal sealed class Agent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.ReplyAsync("hello", cancellationToken).AsTask();
}
