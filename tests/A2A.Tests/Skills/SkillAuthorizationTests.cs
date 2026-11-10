using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Skills;

public class SkillAuthorizationTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private const string Allowed = "scenario";
    private const string Hidden = "secret";
    private const string Denied = "other";
    private const string Unknown = "nope";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Allows the ids in <see cref="Allow"/>; counts and records every question.</summary>
    private sealed class SkillHandler : A2AAuthorizationHandler
    {
        public ConcurrentDictionary<string, bool> Allow { get; } = new(StringComparer.Ordinal) { [Allowed] = true, [Hidden] = true };
        public ConcurrentQueue<string> Asked { get; } = new();
        public bool Throws { get; set; }
        public volatile bool DenyAll;
        public TaskCompletionSource? Asked2 { get; set; }
        public override ValueTask<bool> AuthorizeSkillAsync(ServerCallContext call, string skillId, CancellationToken cancellationToken)
        {
            Asked.Enqueue(skillId);
            if (Throws) throw new InvalidOperationException("handler failed");
            if (Asked.Count >= 2) Asked2?.TrySetResult();
            return ValueTask.FromResult(!DenyAll && Allow.TryGetValue(skillId, out var ok) && ok);
        }
    }

    private sealed class RecordingAgent : IAgentExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? Authorized { get; private set; }
        public string? Requested { get; private set; }
        public Dictionary<string, bool> Answers { get; } = [];
        public string[] Ask { get; set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Authorized = context.AuthorizedSkillId;
            Requested = context.RequestedSkillId;
            foreach (var id in Ask) Answers[id] = await context.IsSkillAllowedAsync(id, ct);
            Interlocked.Increment(ref _calls);
            if (context.UserText == "hold")
            {
                await updater.StartWorkAsync(cancellationToken: ct);
                Started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                return;
            }
            if (context.UserText == "input" && context.Task is null)
            {
                await updater.RequireInputAsync("more", ct);
                return;
            }
            await updater.CompleteAsync("done", ct);
        }
    }

    private static AgentCard CardWithHidden(AgentCard? card = null)
    {
        card ??= TestAgentHost.DefaultCard();
        card.Skills =
        [
            .. card.Skills,
            new AgentSkill { Id = Hidden, Name = "Secret", Description = "extended only", Tags = ["t"] },
            new AgentSkill { Id = Denied, Name = "Other", Description = "extended only", Tags = ["t"] },
        ];
        return card;
    }

    private static async Task<(TestAgentHost Host, SkillHandler Handler, RecordingAgent Agent)> StartAsync(
        SkillAuthorizationMode mode, bool filter = false, Action<A2AServerOptions>? configure = null, bool replaceHandler = true)
    {
        var handler = new SkillHandler();
        var agent = new RecordingAgent();
        var host = await TestAgentHost.StartAsync(o =>
        {
            o.SkillAuthorization.Mode = mode;
            o.SkillAuthorization.FilterExtendedCard = filter;
            o.ExtendedAgentCardProvider = (_, _, _) => ValueTask.FromResult<AgentCard?>(CardWithHidden());
            configure?.Invoke(o);
        }, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
            if (replaceHandler) s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(handler));
        });
        return (host, handler, agent);
    }

    private static A2AClient ClientFor(TestAgentHost host, string binding, string? user = "alice", string? tenant = null) =>
        host.Client(binding, user, new A2AClientOptions { ValidateCapabilities = false, Tenant = tenant });

    private static Message Msg(string? id, string messageId = "m1", string text = "work", string? taskId = null)
    {
        var message = new Message { MessageId = messageId, Role = Role.User, Parts = [Part.FromText(text)], TaskId = taskId };
        return id is null ? message : message.SetRequestedSkillId(id);
    }

    private static Message RawMsg(JsonElement value, string messageId = "m1") => new()
    {
        MessageId = messageId, Role = Role.User, Parts = [Part.FromText("work")],
        Metadata = new() { [SkillSelection.MetadataKey] = value },
    };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<A2AException> SendRefusedAsync(A2AClient client, Message message) =>
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(message));

    private static async Task<A2AException> StreamRefusedAsync(A2AClient client, Message message) =>
        await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(message)) { }
        });

    // ---- allowed, denied, unknown ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Allowed_skill_runs_the_agent_and_the_executor_sees_it(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Allowed));
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Equal(Allowed, agent.Authorized);
        Assert.Null(agent.Requested); // the extension was not activated: only the authorization result is set
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Denied_skill_is_refused_the_agent_does_not_run_and_nothing_is_stored(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var client = ClientFor(host, binding);
        var ex = await SendRefusedAsync(client, Msg(Denied));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Equal([Denied], handler.Asked);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Unknown_skill_gives_the_same_error_as_a_denied_one_and_the_handler_is_not_asked(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var client = ClientFor(host, binding);
        // A skill that is only on the extended card is known: denied by the handler. Unknown: refused earlier. Same error.
        handler.Allow[Hidden] = false;
        var denied = await SendRefusedAsync(client, Msg(Hidden));
        var before = handler.Asked.Count;
        var unknown = await SendRefusedAsync(client, Msg(Unknown, "m2"));
        Assert.Equal(before, handler.Asked.Count);
        Assert.Equal(denied.Code, unknown.Code);
        Assert.Equal(denied.Message, unknown.Message);
        Assert.Equal(denied.Metadata, unknown.Metadata);
        Assert.Equal(A2AErrorCode.PermissionDenied, unknown.Code);
        // Which permission is missing (spec §3.3.2), the same for both, and never the skill id the caller sent.
        var info = denied.Metadata ?? Assert.Single(denied.Details!, d => d.GetProperty("@type").GetString() == A2AErrors.ErrorInfoType)
            .GetProperty("metadata").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal("skill", info["permission"]);
        Assert.Equal("SendMessage", info["operation"]);
        Assert.DoesNotContain(Hidden, denied.Message);
        Assert.Equal(denied.Details?.Select(d => d.GetRawText()), unknown.Details?.Select(d => d.GetRawText()));
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Skill_check_runs_before_skill_selection_so_the_two_errors_do_not_differ(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed,
            configure: o => o.AgentCard.Capabilities.Extensions = [SkillSelection.CreateExtension(rejectUnknownSkills: true)]);
        await using var _ = host;
        handler.Allow[Hidden] = false;
        var client = ClientFor(host, binding);
        var options = new A2ARequestOptions { Extensions = [SkillSelection.ExtensionUri] };
        var denied = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Hidden), options: options));
        var unknown = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(Unknown, "m2"), options: options));
        Assert.Equal(A2AErrorCode.PermissionDenied, denied.Code);
        Assert.Equal(denied.Code, unknown.Code);
        Assert.Equal(denied.Message, unknown.Message);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Skill_is_checked_without_the_extension_header_or_a_card_entry(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        Assert.DoesNotContain(host.Handler.AgentCard.Capabilities.Extensions ?? [], e => e.Uri == SkillSelection.ExtensionUri);
        var ex = await SendRefusedAsync(ClientFor(host, binding), Msg(Denied));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Off_mode_does_not_ask_the_handler_and_runs_any_skill(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.Off);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Denied));
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Null(agent.Authorized);
        Assert.Empty(handler.Asked);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_default_handler_denies_every_skill(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed, replaceHandler: false);
        await using var _ = host;
        var ex = await SendRefusedAsync(ClientFor(host, binding), Msg(Allowed));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_throwing_handler_fails_closed(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        handler.Throws = true;
        await Assert.ThrowsAnyAsync<Exception>(() => ClientFor(host, binding).SendMessageAsync(Msg(Allowed)));
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    // ---- modes: none named, malformed ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task When_named_runs_a_message_that_names_no_skill(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var client = ClientFor(host, binding);
        await client.SendMessageAsync(Msg(null));
        await client.SendMessageAsync(RawMsg(Json("\"\""), "m2"));
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Calls);
        Assert.Null(agent.Authorized);
        Assert.Empty(handler.Asked);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Required_refuses_a_message_that_names_no_skill(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        var client = ClientFor(host, binding);
        foreach (var message in new[] { Msg(null), RawMsg(Json("\"\""), "m2") })
        {
            var ex = await SendRefusedAsync(client, message);
            Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        }
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Required_runs_an_allowed_skill(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Msg(Allowed));
        await host.WaitIdleAsync();
        Assert.Equal(Allowed, agent.Authorized);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc, SkillAuthorizationMode.WhenNamed, "42")]
    [InlineData(ProtocolBindings.HttpJson, SkillAuthorizationMode.WhenNamed, "42")]
    [InlineData(ProtocolBindings.JsonRpc, SkillAuthorizationMode.Required, "null")]
    [InlineData(ProtocolBindings.HttpJson, SkillAuthorizationMode.Required, "null")]
    [InlineData(ProtocolBindings.JsonRpc, SkillAuthorizationMode.WhenNamed, "[\"scenario\"]")]
    [InlineData(ProtocolBindings.HttpJson, SkillAuthorizationMode.WhenNamed, "{}")]
    public async Task A_malformed_skill_value_is_refused_as_invalid_params(string binding, SkillAuthorizationMode mode, string json)
    {
        var (host, _, agent) = await StartAsync(mode);
        await using var _ = host;
        var ex = await SendRefusedAsync(ClientFor(host, binding), RawMsg(Json(json)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task A_null_message_is_refused_when_a_mode_is_on()
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Handler.SendMessageAsync(new SendMessageRequest { Message = null! }, AliceCall()));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task The_refusal_names_the_extension_on_the_wire(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        var body = "{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}]}";
        using var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\",\"params\":{\"message\":" + body + "}}", System.Text.Encoding.UTF8, "application/json") }
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/message:send") { Content = new StringContent("{\"message\":" + body + "}", System.Text.Encoding.UTF8, "application/a2a+json") };
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        var resp = await host.Http("alice").SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        var details = binding == ProtocolBindings.JsonRpc ? error.GetProperty("data") : error.GetProperty("details");
        Assert.Equal("INVALID_PARAMS", details[0].GetProperty("reason").GetString());
        Assert.Equal(SkillSelection.ExtensionUri, details[0].GetProperty("metadata").GetProperty("extension").GetString());
        Assert.Equal(0, agent.Calls);
    }

    // ---- streaming ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Streaming_denied_is_refused_before_the_agent_runs(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var ex = await StreamRefusedAsync(ClientFor(host, binding), Msg(Denied));
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Streaming_allowed_runs_the_agent(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        await foreach (var ignored in ClientFor(host, binding).SendStreamingMessageAsync(Msg(Allowed))) { }
        await host.WaitIdleAsync();
        Assert.Equal(Allowed, agent.Authorized);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Streaming_required_without_a_skill_is_refused(string binding)
    {
        var (host, _, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        var ex = await StreamRefusedAsync(ClientFor(host, binding), Msg(null));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task An_open_stream_ends_when_its_skill_is_revoked()
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed,
            configure: o => o.Limits.StreamReauthInterval = TimeSpan.FromMilliseconds(50));
        await using var _ = host;
        handler.Asked2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Msg(Allowed, text: "hold") }, AliceCall()).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Wait));
        var taskId = Assert.IsType<TaskEvent>(stream.Current.Event).Task.Id;
        await agent.Started.Task.WaitAsync(Wait);

        // The re-check runs while the reader waits for the next event, so read in the background until the stream ends.
        var ended = Task.Run(async () =>
        {
            while (await stream.MoveNextAsync()) { }
        });

        // The skill is asked about again on re-authorization (second question). It is still allowed: the stream stays open.
        await handler.Asked2.Task.WaitAsync(Wait);
        Assert.False(ended.IsCompleted);
        handler.DenyAll = true;

        // The "hold" agent never finishes, so a stream that ends within the timeout ended because of the re-check.
        await ended.WaitAsync(Wait);
        handler.DenyAll = false;
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, AliceCall());
    }

    // ---- follow-ups and retries ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_follow_up_is_checked_and_must_name_the_skill_again_when_required(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.Required);
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = (await client.SendMessageAsync(Msg(Allowed, text: "input"))).RequireTask();
        await host.WaitIdleAsync();
        Assert.Equal(TaskState.InputRequired, task.Status.State);

        var none = await SendRefusedAsync(client, Msg(null, "m2", "more", task.Id));
        Assert.Equal(A2AErrorCode.InvalidParams, none.Code);

        var denied = await SendRefusedAsync(client, Msg(Denied, "m3", "more", task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, denied.Code); // the message names a task: a denial looks like a missing task

        handler.Allow[Allowed] = false; // revoked since the first message
        var revoked = await SendRefusedAsync(client, Msg(Allowed, "m4", "more", task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, revoked.Code);

        handler.Allow[Allowed] = true;
        var done = (await client.SendMessageAsync(Msg(Allowed, "m5", "more", task.Id))).RequireTask();
        await host.WaitIdleAsync();
        Assert.Equal(task.Id, done.Id);
        Assert.Equal(2, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_retry_after_revocation_is_refused_and_does_not_replay(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.WhenNamed);
        await using var _ = host;
        var client = ClientFor(host, binding);
        await client.SendMessageAsync(Msg(Allowed));
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);

        handler.Allow[Allowed] = false;
        var ex = await SendRefusedAsync(client, Msg(Allowed)); // the same message id: an idempotent retry
        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
    }

    // ---- per-tenant cards ----

    private sealed class Tenants : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant == "none") return ValueTask.FromResult<AgentCard?>(null);
            var card = TestAgentHost.DefaultCard();
            card.Skills = [new AgentSkill { Id = $"skill-{scope.Tenant}", Name = "S", Description = "s", Tags = ["t"] }];
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Each_tenant_card_decides_which_skills_are_known(string binding)
    {
        var handler = new SkillHandler();
        handler.Allow["skill-a"] = true;
        handler.Allow["skill-b"] = true;
        var agent = new RecordingAgent();
        await using var host = await TestAgentHost.StartAsync(
            o => o.SkillAuthorization.Mode = SkillAuthorizationMode.WhenNamed,
            services: s =>
            {
                s.AddA2AServer().AddTenantCardProvider<Tenants>();
                s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
                s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(handler));
            });

        await ClientFor(host, binding, tenant: "a").SendMessageAsync(Msg("skill-a"));
        await host.WaitIdleAsync();
        Assert.Equal("skill-a", agent.Authorized);

        var wrong = await SendRefusedAsync(ClientFor(host, binding, tenant: "b"), Msg("skill-a", "m2"));
        Assert.Equal(A2AErrorCode.PermissionDenied, wrong.Code);

        var noAgent = await SendRefusedAsync(ClientFor(host, binding, tenant: "none"), Msg("skill-a", "m3"));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, noAgent.Code);
        Assert.Equal(1, agent.Calls);
    }

    // ---- extended card ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_extended_card_lists_only_allowed_skills_and_clears_signatures(string binding)
    {
        var (host, handler, _) = await StartAsync(SkillAuthorizationMode.Off, filter: true, configure: o =>
            o.ExtendedAgentCardProvider = (_, _, _) =>
            {
                var card = CardWithHidden();
                card.Signatures = [new AgentCardSignature { Protected = "p", Signature = "s" }];
                return ValueTask.FromResult<AgentCard?>(card);
            });
        await using var _ = host;
        handler.Allow[Denied] = false;
        var card = await ClientFor(host, binding).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal([Allowed, Hidden], card.Skills.Select(s => s.Id).Order());
        Assert.True(card.Signatures is null or { Count: 0 });
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_extended_card_is_not_filtered_without_the_flag(string binding)
    {
        var (host, _, _) = await StartAsync(SkillAuthorizationMode.WhenNamed, filter: false);
        await using var _ = host;
        var card = await ClientFor(host, binding).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal(3, card.Skills.Count);
    }

    [Fact]
    public async Task The_provider_card_is_never_changed_and_is_returned_as_is_when_nothing_is_removed()
    {
        var provided = CardWithHidden();
        provided.Signatures = [new AgentCardSignature { Protected = "p", Signature = "s" }];
        var (host, handler, _) = await StartAsync(SkillAuthorizationMode.Off, filter: true,
            configure: o => o.ExtendedAgentCardProvider = (_, _, _) => ValueTask.FromResult<AgentCard?>(provided));
        await using var _ = host;

        handler.Allow[Denied] = false;
        var filtered = await host.Handler.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), AliceCall());
        Assert.NotSame(provided, filtered);
        Assert.Equal(3, provided.Skills.Count);
        Assert.Single(provided.Signatures!);
        Assert.Equal(2, filtered.Skills.Count);

        handler.Allow[Denied] = true;
        var same = await host.Handler.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), AliceCall());
        Assert.Same(provided, same);
        Assert.Single(same.Signatures!);
    }

    [Fact]
    public async Task A_throwing_handler_fails_the_extended_card_call()
    {
        var (host, handler, _) = await StartAsync(SkillAuthorizationMode.Off, filter: true);
        await using var _ = host;
        handler.Throws = true;
        await Assert.ThrowsAnyAsync<Exception>(() => host.Handler.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), AliceCall()));
    }

    [Fact]
    public async Task The_public_card_is_not_filtered()
    {
        var (host, handler, _) = await StartAsync(SkillAuthorizationMode.WhenNamed, filter: true);
        await using var _ = host;
        handler.Allow[Allowed] = false;
        Assert.Contains(host.Handler.AgentCard.Skills, s => s.Id == Allowed);
    }

    // ---- the agent's own question ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_executor_can_ask_about_any_skill_in_any_mode(string binding)
    {
        var (host, handler, agent) = await StartAsync(SkillAuthorizationMode.Off);
        await using var _ = host;
        handler.Allow[Denied] = false;
        agent.Ask = [Allowed, Hidden, Denied, Unknown];
        await ClientFor(host, binding).SendMessageAsync(Msg(null));
        await host.WaitIdleAsync();
        Assert.Equal(new() { [Allowed] = true, [Hidden] = true, [Denied] = false, [Unknown] = false }, agent.Answers);
        Assert.Null(agent.Authorized);
        Assert.DoesNotContain(Unknown, handler.Asked);
    }

    [Fact]
    public async Task IsSkillAllowedAsync_rejects_empty_ids_and_is_false_without_an_authorizer()
    {
        var context = new AgentRequestContext
        {
            Message = Msg(null), TaskId = "t", ContextId = "c", Call = AliceCall(),
        };
        await Assert.ThrowsAsync<ArgumentException>(async () => await context.IsSkillAllowedAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await context.IsSkillAllowedAsync(null!));
        Assert.False(await context.IsSkillAllowedAsync(Allowed));
        Assert.Null(context.AuthorizedSkillId);
    }

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
        ProtocolVersion = A2AProtocol.Version,
    };
}
