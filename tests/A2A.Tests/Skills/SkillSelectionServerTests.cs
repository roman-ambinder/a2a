using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Skills;

public class SkillSelectionServerTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private const string Known = "scenario";
    private const string Unknown = "nope";

    private sealed class RecordingAgent : IAgentExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? Requested { get; private set; }
        public string? FromMessage { get; private set; }

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Requested = context.RequestedSkillId;
            FromMessage = context.Message.GetRequestedSkillId();
            Interlocked.Increment(ref _calls);
            return updater.CompleteAsync("done", ct).AsTask();
        }
    }

    private sealed class Tenants : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            var card = TestAgentHost.DefaultCard();
            card.Capabilities.Extensions = [SkillSelection.CreateExtension(rejectUnknownSkills: scope.Tenant == "strict")];
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    private static readonly A2ARequestOptions Activated = new() { Extensions = [SkillSelection.ExtensionUri] };

    private static Message Msg(string? id = Known, string messageId = "m1")
    {
        var message = new Message { MessageId = messageId, Role = Role.User, Parts = [Part.FromText("work")] };
        return id is null ? message : message.SetRequestedSkillId(id);
    }

    private static Message RawMsg(JsonElement value) => new()
    {
        MessageId = "m1", Role = Role.User, Parts = [Part.FromText("work")],
        Metadata = new() { [SkillSelection.MetadataKey] = value },
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<(TestAgentHost Host, RecordingAgent Agent)> StartAsync(
        bool? optIn, Action<A2AServerOptions>? configure = null, bool requireAuth = true)
    {
        var agent = new RecordingAgent();
        var host = await TestAgentHost.StartAsync(o =>
        {
            if (optIn is { } reject) o.AgentCard.Capabilities.Extensions = [SkillSelection.CreateExtension(reject)];
            configure?.Invoke(o);
        }, requireAuth: requireAuth, services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));
        return (host, agent);
    }

    private static A2AClient ClientFor(TestAgentHost host, string binding, string? user = "alice", A2AClientOptions? options = null) =>
        host.Client(binding, user, options ?? new A2AClientOptions { ValidateCapabilities = false });

    [Theory, MemberData(nameof(Bindings))]
    public async Task Declared_without_opt_in_known_id_reaches_the_agent(string binding)
    {
        var (host, agent) = await StartAsync(optIn: false);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Known), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal(Known, agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Declared_without_opt_in_unknown_id_is_not_refused(string binding)
    {
        var (host, agent) = await StartAsync(optIn: false);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Unknown), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Equal(Unknown, agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_known_id_reaches_the_agent(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Known), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Equal(Known, agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_unknown_id_is_refused_before_the_agent_runs(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        var client = ClientFor(host, binding);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Unknown), options: Activated));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_unknown_id_is_refused_on_streaming(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        var client = ClientFor(host, binding);
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Msg(Unknown), options: Activated)) { }
        });
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    private static HttpRequestMessage RawSend(string binding, string id)
    {
        var body = "{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}],\"metadata\":{\"skillId\":\"" + id + "\"}}";
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\",\"params\":{\"message\":" + body + "}}", Encoding.UTF8, "application/json") }
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/message:send") { Content = new StringContent("{\"message\":" + body + "}", Encoding.UTF8, "application/a2a+json") };
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        req.Headers.Add(A2AProtocol.Headers.Extensions, SkillSelection.ExtensionUri);
        return req;
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Refusal_names_the_extension_on_the_wire(string binding)
    {
        var (host, _) = await StartAsync(optIn: true);
        await using var _ = host;
        using var req = RawSend(binding, Unknown);
        var resp = await host.Http("alice").SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        var details = binding == ProtocolBindings.JsonRpc ? error.GetProperty("data") : error.GetProperty("details");
        Assert.Equal("INVALID_PARAMS", details[0].GetProperty("reason").GetString());
        Assert.Equal(SkillSelection.ExtensionUri, details[0].GetProperty("metadata").GetProperty("extension").GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_non_string_id_is_refused(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        var ex = await Assert.ThrowsAsync<A2AException>(() => ClientFor(host, binding).SendMessageAsync(RawMsg(Json("42")), options: Activated));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_empty_id_is_accepted_and_reads_as_null(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(RawMsg(Json("\"\"")), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Null(agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Caller_that_did_not_activate_is_not_checked(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Unknown));
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Null(agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Extension_not_declared_is_ignored_but_still_readable_from_the_message(string binding)
    {
        var (host, agent) = await StartAsync(optIn: null);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Unknown), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Null(agent.Requested);
        Assert.Equal(Unknown, agent.FromMessage);
    }

    private static void ExtendedCardWithSkill(A2AServerOptions o, Action? onCall = null) =>
        o.ExtendedAgentCardProvider = (_, _, _) =>
        {
            onCall?.Invoke();
            var card = TestAgentHost.DefaultCard();
            card.Skills = [.. card.Skills!, new AgentSkill { Id = "secret", Name = "Secret", Description = "extended only", Tags = ["t"] }];
            return ValueTask.FromResult<AgentCard?>(card);
        };

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_id_only_on_the_extended_card_is_accepted_for_an_authenticated_caller(string binding)
    {
        var (host, agent) = await StartAsync(optIn: true, o => ExtendedCardWithSkill(o));
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg("secret"), options: Activated);
        await host.WaitIdleAsync();
        Assert.Equal("secret", agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Opt_in_id_only_on_the_extended_card_is_refused_for_an_anonymous_caller(string binding)
    {
        var providerCalls = 0;
        var (host, agent) = await StartAsync(optIn: true, o =>
        {
            o.AllowAnonymous = true;
            ExtendedCardWithSkill(o, () => Interlocked.Increment(ref providerCalls));
        }, requireAuth: false);
        await using var _ = host;
        var ex = await Assert.ThrowsAsync<A2AException>(() => ClientFor(host, binding, user: null).SendMessageAsync(Msg("secret"), options: Activated));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
        Assert.Equal(0, Volatile.Read(ref providerCalls));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Each_tenant_card_decides_for_itself(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddTenantCardProvider<Tenants>(), grpc: true);
        A2AClient Tenant(string name) => host.Client(binding, options: new A2AClientOptions { Tenant = name, ValidateCapabilities = false });

        var ex = await Assert.ThrowsAsync<A2AException>(() => Tenant("strict").SendMessageAsync(Msg(Unknown), options: Activated));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.NotNull((await Tenant("lenient").SendMessageAsync(Msg(Unknown, "m2"), options: Activated)).RequireTask());
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Activated_extension_is_echoed_on_success(string binding)
    {
        var (host, _) = await StartAsync(optIn: true);
        await using var _ = host;
        using var req = RawSend(binding, Known);
        var resp = await host.Http("alice").SendAsync(req);
        resp.EnsureSuccessStatusCode();
        Assert.Equal(SkillSelection.ExtensionUri, resp.Headers.GetValues(A2AProtocol.Headers.Extensions).Single());
    }
}
