using A2A.Client;
using A2A.Server;
using A2A.Skills;

namespace A2A.Grpc.Tests;

/// <summary>Skill selection over gRPC: the same answers the HTTP bindings give (see A2A.Tests for the HTTP side).</summary>
public class GrpcSkillSelectionTests
{
    private static readonly A2ARequestOptions Activated = new() { Extensions = [SkillSelection.ExtensionUri] };

    private static Task<GrpcHost> StartAsync(Action<AgentRequestContext> onRun) => GrpcHost.StartAsync(
        o => o.AgentCard.Capabilities.Extensions = [SkillSelection.CreateExtension(rejectUnknownSkills: true)],
        server: b => b.AddAgentExecutor((ctx, updater, ct) =>
        {
            onRun(ctx);
            return updater.CompleteAsync("done", ct).AsTask();
        }));

    private static Message Msg(string id) => Message.FromUser("work").SetRequestedSkillId(id);

    [Fact]
    public async Task Opt_in_with_an_unknown_id_is_refused_and_the_agent_does_not_run()
    {
        var runs = 0;
        await using var host = await StartAsync(_ => Interlocked.Increment(ref runs));
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client().SendMessageAsync(Msg("nope"), options: Activated));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(SkillSelection.ExtensionUri, ex.Metadata?["extension"]);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Opt_in_with_an_unknown_id_is_refused_on_streaming()
    {
        var runs = 0;
        await using var host = await StartAsync(_ => Interlocked.Increment(ref runs));
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in host.Client().SendStreamingMessageAsync(Msg("nope"), options: Activated)) { }
        });
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(SkillSelection.ExtensionUri, ex.Metadata?["extension"]);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Opt_in_with_a_known_id_reaches_the_executor()
    {
        string? seen = null;
        await using var host = await StartAsync(ctx => seen = ctx.RequestedSkillId);
        await host.Client().SendMessageAsync(Msg("s"), options: Activated);
        Assert.Equal("s", seen);
    }
}
