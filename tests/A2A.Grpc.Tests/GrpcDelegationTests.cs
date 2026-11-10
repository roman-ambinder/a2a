using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Delegation;

namespace A2A.Grpc.Tests;

/// <summary>Delegation identity over gRPC: the same answers the HTTP bindings give (see A2A.Tests for the HTTP side).</summary>
public class GrpcDelegationTests
{
    private sealed class RecordingExchanger : ITokenExchanger
    {
        public List<TokenExchangeRequest> Requests { get; } = [];

        public ValueTask<TokenExchangeResult> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return new(new TokenExchangeResult { AccessToken = "exchanged" });
        }
    }

    private static string Hops(int n) =>
        $$"""{"origin":{"sub":"user:alice"},"actors":[{{string.Join(",", Enumerable.Range(1, n).Select(i => $$"""{"sub":"agent:{{i}}"}"""))}}]}""";

    private static Message Msg(string chainJson)
    {
        using var doc = JsonDocument.Parse(chainJson);
        return new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Role = Role.User,
            Parts = [Part.FromText("work")],
            Metadata = new() { [ActorChain.MetadataKey] = doc.RootElement.Clone() },
        };
    }

    [Fact]
    public async Task Chain_over_the_limit_is_refused_and_the_agent_does_not_run()
    {
        var runs = 0;
        await using var host = await GrpcHost.StartAsync(server: b => b.AddAgentExecutor((_, updater, ct) =>
        {
            Interlocked.Increment(ref runs);
            return updater.CompleteAsync("done", ct).AsTask();
        }));
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client().SendMessageAsync(Msg(Hops(9))));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Well_formed_chain_is_echoed_on_the_task()
    {
        await using var host = await GrpcHost.StartAsync(server: b => b.AddAgentExecutor((_, updater, ct) => updater.CompleteAsync("done", ct).AsTask()));
        const string chain = """{"origin":{"sub":"user:alice"},"actors":[{"sub":"agent:a"}]}""";
        var client = host.Client();
        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync(Msg(chain))).Task;
        var stored = await client.GetTaskAsync(task.Id);
        // gRPC carries the chain as a protobuf Struct, so the spacing differs: compare the JSON values.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(chain), JsonNode.Parse(stored.Metadata![ActorChain.MetadataKey].GetRawText())));
    }

    [Fact]
    public async Task Exchanger_receives_the_callers_bearer_token()
    {
        var exchanger = new RecordingExchanger();
        await using var host = await GrpcHost.StartAsync(server: b => b
            .AddTokenExchanger(exchanger)
            .AddAgentExecutor(async (ctx, updater, ct) =>
            {
                await ctx.ExchangeTokenAsync("api://b", ["read"], ct);
                await updater.CompleteAsync("done", ct);
            }));
        var options = new A2ARequestOptions { Headers = { ["Authorization"] = "Bearer grpc-caller-token" } };
        await host.Client().SendMessageAsync(Message.FromUser("hi"), options: options);
        var request = Assert.Single(exchanger.Requests);
        Assert.Equal("grpc-caller-token", request.SubjectToken);
        Assert.Equal("api://b", request.Audience);
        Assert.Equal(["read"], request.Scopes);
    }
}
