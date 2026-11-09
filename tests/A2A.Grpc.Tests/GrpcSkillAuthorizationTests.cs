using System.Collections.Concurrent;
using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Grpc.Tests;

/// <summary>Per-skill authorization over gRPC: the same answers the HTTP bindings give (see A2A.Tests for the HTTP side).</summary>
public class GrpcSkillAuthorizationTests
{
    private const string Allowed = "s";
    private const string Denied = "other";
    private const string Unknown = "nope";

    private sealed class SkillHandler : A2AAuthorizationHandler
    {
        public ConcurrentDictionary<string, bool> Allow { get; } = new(StringComparer.Ordinal) { [Allowed] = true };
        public ConcurrentQueue<string> Asked { get; } = new();
        public override ValueTask<bool> AuthorizeSkillAsync(ServerCallContext call, string skillId, CancellationToken cancellationToken)
        {
            Asked.Enqueue(skillId);
            return ValueTask.FromResult(Allow.TryGetValue(skillId, out var ok) && ok);
        }
    }

    private sealed record Seen(string? Authorized, bool AllowedAsked, bool UnknownAsked);

    private sealed class Rig(GrpcHost host, SkillHandler handler) : IAsyncDisposable
    {
        public GrpcHost Host => host;
        public SkillHandler Handler => handler;
        public int Runs;
        public Seen? Last;
        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(SkillAuthorizationMode mode, bool filter = false)
    {
        var handler = new SkillHandler();
        Rig? rig = null;
        var host = await GrpcHost.StartAsync(
            o =>
            {
                o.SkillAuthorization.Mode = mode;
                o.SkillAuthorization.FilterExtendedCard = filter;
                o.ExtendedAgentCardProvider = (_, _, _) =>
                {
                    var card = GrpcHost.Card();
                    card.Skills = [.. card.Skills, new AgentSkill { Id = Denied, Name = "O", Description = "o", Tags = ["t"] }];
                    card.Signatures = [new AgentCardSignature { Protected = "p", Signature = "s" }];
                    return ValueTask.FromResult<AgentCard?>(card);
                };
            },
            services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(handler)),
            server: b => b.AddAgentExecutor(async (ctx, updater, ct) =>
            {
                Interlocked.Increment(ref rig!.Runs);
                rig.Last = new Seen(ctx.AuthorizedSkillId, await ctx.IsSkillAllowedAsync(Allowed, ct), await ctx.IsSkillAllowedAsync(Unknown, ct));
                await updater.CompleteAsync("done", ct);
            }));
        return rig = new Rig(host, handler);
    }

    private static Message Msg(string? id, string messageId = "m1")
    {
        var message = new Message { MessageId = messageId, Role = Role.User, Parts = [Part.FromText("work")] };
        return id is null ? message : message.SetRequestedSkillId(id);
    }

    private static A2AClient Client(Rig rig) => rig.Host.Client(options: new A2AClientOptions { ValidateCapabilities = false });

    [Fact]
    public async Task Allowed_skill_runs_and_the_executor_sees_it()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await Client(rig).SendMessageAsync(Msg(Allowed));
        Assert.Equal(1, Volatile.Read(ref rig.Runs));
        Assert.Equal(new Seen(Allowed, true, false), rig.Last);
    }

    [Fact]
    public async Task Denied_skill_is_refused_and_the_agent_does_not_run()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.WhenNamed);
        var client = Client(rig);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Denied)));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal(0, Volatile.Read(ref rig.Runs));
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
    }

    [Fact]
    public async Task Unknown_skill_gives_the_same_error_as_a_denied_one()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.WhenNamed);
        var client = Client(rig);
        var denied = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Denied)));
        var unknown = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Unknown, "m2")));
        Assert.Equal(denied.Code, unknown.Code);
        Assert.Equal(denied.Message, unknown.Message);
        Assert.Equal(denied.Metadata, unknown.Metadata);
        Assert.DoesNotContain(Unknown, rig.Handler.Asked);
        Assert.Equal(0, Volatile.Read(ref rig.Runs));
    }

    [Fact]
    public async Task Required_refuses_a_message_with_no_skill()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.Required);
        var ex = await Assert.ThrowsAsync<A2AException>(() => Client(rig).SendMessageAsync(Msg(null)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(SkillSelection.ExtensionUri, ex.Metadata?["extension"]);
        Assert.Equal(0, Volatile.Read(ref rig.Runs));
    }

    [Fact]
    public async Task A_malformed_skill_value_is_refused()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.WhenNamed);
        using var doc = JsonDocument.Parse("42");
        var message = new Message
        {
            MessageId = "m1", Role = Role.User, Parts = [Part.FromText("work")],
            Metadata = new() { [SkillSelection.MetadataKey] = doc.RootElement.Clone() },
        };
        var ex = await Assert.ThrowsAsync<A2AException>(() => Client(rig).SendMessageAsync(message));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(0, Volatile.Read(ref rig.Runs));
    }

    [Fact]
    public async Task Streaming_denied_is_refused_and_allowed_runs()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.WhenNamed);
        var client = Client(rig);
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Msg(Denied))) { }
        });
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal(0, Volatile.Read(ref rig.Runs));

        await foreach (var _ in client.SendStreamingMessageAsync(Msg(Allowed, "m2"))) { }
        Assert.Equal(Allowed, rig.Last?.Authorized);
    }

    [Fact]
    public async Task The_extended_card_lists_only_allowed_skills_and_clears_signatures()
    {
        await using var rig = await StartAsync(SkillAuthorizationMode.Off, filter: true);
        var card = await Client(rig).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal([Allowed], card.Skills.Select(s => s.Id));
        Assert.True(card.Signatures is null or { Count: 0 });
    }
}
