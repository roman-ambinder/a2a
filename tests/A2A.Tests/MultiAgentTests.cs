using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Json;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Two agents on one host: "acme" (streaming and push on, own signing key) and "globex" (both off, global key).</summary>
public sealed class TenantCards : TenantCardProvider
{
    public static readonly AgentCardSigningKey AcmeKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), "acme-key", "ES256");

    public static AgentCard Card(string tenant)
    {
        var card = TestAgentHost.DefaultCard();
        card.Name = $"{tenant} agent";
        card.Capabilities.Streaming = tenant == "acme";
        card.Capabilities.PushNotifications = tenant == "acme";
        return card;
    }

    public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult(scope.Tenant is "acme" or "globex" ? Card(scope.Tenant) : null);

    public override ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext call, CancellationToken cancellationToken)
    {
        var card = Card(call.Tenant);
        card.Description = $"Extended {call.Tenant} card for {call.Principal}";
        return ValueTask.FromResult<AgentCard?>(card);
    }

    public override ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken) =>
        ValueTask.FromResult(tenant == "acme" ? AcmeKey : null);
}

/// <summary>Resolves one agent per known tenant; every reply names the agent.</summary>
public sealed class TenantAgents : AgentExecutorResolver
{
    public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) =>
        tenant is "acme" or "globex" ? new NamedAgent(tenant) : null;

    private sealed class NamedAgent(string name) : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            if (context.UserText == "hello")
            {
                await updater.ReplyAsync($"{name}: hi", ct);
                return;
            }
            if (context.UserText == "input")
            {
                await updater.RequireInputAsync($"{name}: need more", ct);
                return;
            }
            await updater.CompleteAsync($"{name}: {context.UserText}", ct);
        }
    }
}

public class MultiAgentTests : IAsyncLifetime
{
    private static readonly AgentCardSigningKey GlobalKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), "global-key", "ES256");
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync(
        configureHttp: h => h.SigningKey = GlobalKey,
        services: s => s.AddA2AServer().AddTenantCardProvider<TenantCards>().AddAgentExecutorResolver<TenantAgents>(),
        grpc: true);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private A2AClient Client(string binding, string tenant, string user = "alice") =>
        _host.Client(binding, user, new A2AClientOptions { Tenant = tenant, ValidateCapabilities = false });

    [Fact]
    public async Task Each_tenant_serves_its_own_card_and_an_unknown_tenant_has_none()
    {
        var http = _host.Http();
        var acme = await http.GetFromJsonAsync("/acme" + A2AProtocol.WellKnownAgentCardPath, A2AJson.GetTypeInfo<AgentCard>());
        Assert.Equal("acme agent", acme!.Name);
        Assert.True(acme.Capabilities.Streaming);
        var globex = await http.GetFromJsonAsync("/globex" + A2AProtocol.WellKnownAgentCardPath, A2AJson.GetTypeInfo<AgentCard>());
        Assert.Equal("globex agent", globex!.Name);
        Assert.False(globex.Capabilities.Streaming);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/initech" + A2AProtocol.WellKnownAgentCardPath)).StatusCode);
        // The provider knows no default tenant either.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(A2AProtocol.WellKnownAgentCardPath)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Each_tenant_runs_its_own_executor(string binding)
    {
        var acme = (await Client(binding, "acme").SendMessageAsync("work")).RequireTask();
        Assert.Equal("acme: work", acme.Status.Message!.GetText());
        var globex = (await Client(binding, "globex").SendMessageAsync("work")).RequireTask();
        Assert.Equal("globex: work", globex.Status.Message!.GetText());
        Assert.Equal("globex: hi", (await Client(binding, "globex").SendMessageAsync("hello")).RequireMessage().GetText());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Capabilities_come_from_the_tenants_card(string binding)
    {
        var events = new List<StreamResponse>();
        await foreach (var e in Client(binding, "acme").SendStreamingMessageAsync(Message.FromUser("work"))) events.Add(e);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);

        var globex = Client(binding, "globex");
        var stream = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in globex.SendStreamingMessageAsync(Message.FromUser("work"))) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, stream.Code);

        var task = (await globex.SendMessageAsync("input")).RequireTask();
        var subscribe = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in globex.SubscribeToTaskAsync(task.Id)) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, subscribe.Code);
        var push = await Assert.ThrowsAsync<A2AException>(() =>
            globex.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://x.example/h" }));
        Assert.Equal(A2AErrorCode.PushNotificationNotSupported, push.Code);

        var acmeTask = (await Client(binding, "acme").SendMessageAsync("input")).RequireTask();
        await Client(binding, "acme").CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = acmeTask.Id, Url = "https://x.example/h" });
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Agents_never_see_each_others_tasks(string binding)
    {
        var task = (await Client(binding, "acme").SendMessageAsync("input")).RequireTask();
        var globex = Client(binding, "globex");
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => globex.GetTaskAsync(task.Id))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => globex.CancelTaskAsync(task.Id))).Code);
        Assert.DoesNotContain((await globex.ListTasksAsync(new ListTasksRequest())).Tasks, t => t.Id == task.Id);
        Assert.Contains((await Client(binding, "acme").ListTasksAsync(new ListTasksRequest())).Tasks, t => t.Id == task.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task An_unknown_tenant_refuses_like_a_missing_agent(string binding)
    {
        // A task stored under the tenant earlier (here: written directly), so only the missing agent makes it unreachable.
        var key = new Server.Storage.TaskKey("initech", "alice", "t-initech-" + binding);
        var stored = new AgentTask { Id = key.TaskId, ContextId = "c", Status = new AgentTaskStatus { State = TaskState.InputRequired } };
        var now = DateTimeOffset.UtcNow;
        await _host.App.Services.GetRequiredService<Server.Storage.TaskStore>().CreateAsync(new Server.Storage.TaskRecord(key.Tenant, key.Owner, stored, 0, now, now));

        var client = Client(binding, "initech");
        var send = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("work"));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, send.Code);
        Assert.Equal("No agent is configured for this tenant.", send.Message);
        var list = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, list.Code);
        Assert.Equal("No agent is configured for this tenant.", list.Message);
        var extended = await Assert.ThrowsAsync<A2AException>(() => client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, extended.Code);
        Assert.Equal("No agent is configured for this tenant.", extended.Message);

        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(key.TaskId))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(key.TaskId))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SubscribeToTaskAsync(key.TaskId)) { }
        })).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() =>
            client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = key.TaskId, Url = "https://x.example/h" }))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() =>
            client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = key.TaskId, Id = "p" }))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() =>
            client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = key.TaskId }))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() =>
            client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = key.TaskId, Id = "p" }))).Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task An_inline_push_config_is_refused_where_the_tenant_has_no_push(string binding)
    {
        var globex = Client(binding, "globex");
        var request = new SendMessageRequest
        {
            Message = Message.FromUser("work"),
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "https://x.example/h" } },
        };
        Assert.Equal(A2AErrorCode.PushNotificationNotSupported, (await Assert.ThrowsAsync<A2AException>(() => globex.SendMessageAsync(request))).Code);
        Assert.Empty((await globex.ListTasksAsync(new ListTasksRequest())).Tasks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Extended_card_comes_from_the_tenants_provider(string binding)
    {
        var card = await Client(binding, "globex").GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.Equal("Extended globex card for alice", card.Description);
    }

    [Fact]
    public async Task Each_tenants_signed_card_verifies_with_its_own_jwks()
    {
        var http = _host.Http();
        var acmeJwks = await http.GetFromJsonAsync("/acme/.well-known/jwks.json", A2AJson.GetTypeInfo<JwkSet>());
        Assert.Equal("acme-key", Assert.Single(acmeJwks!.Keys).Kid);
        // No key from the provider: the global key, as for a single agent.
        var globexJwks = await http.GetFromJsonAsync("/globex/.well-known/jwks.json", A2AJson.GetTypeInfo<JwkSet>());
        Assert.Equal("global-key", Assert.Single(globexJwks!.Keys).Kid);

        var acmeTrust = new A2ACardResolverOptions();
        acmeTrust.Trust.TrustedKeys["http://localhost"] = acmeJwks;
        var acme = await new A2ACardResolver(http, acmeTrust).ResolveAsync(new Uri("http://localhost/acme" + A2AProtocol.WellKnownAgentCardPath));
        Assert.Equal(AgentCardVerificationStatus.Verified, acme.Verification.Status);
        Assert.Equal("acme agent", acme.Card.Name);
        Assert.Equal(new PeerIdentity("http://localhost", "acme-key"), acme.Identity);

        var globexTrust = new A2ACardResolverOptions();
        globexTrust.Trust.TrustedKeys["http://localhost"] = globexJwks;
        var globex = await new A2ACardResolver(http, globexTrust).ResolveAsync(new Uri("http://localhost/globex" + A2AProtocol.WellKnownAgentCardPath));
        Assert.Equal(AgentCardVerificationStatus.Verified, globex.Verification.Status);
        Assert.Equal("global-key", globex.Identity.KeyId);

        // acme's key does not vouch for globex's card.
        await Assert.ThrowsAsync<AgentCardVerificationException>(() =>
            new A2ACardResolver(http, acmeTrust).ResolveAsync(new Uri("http://localhost/globex" + A2AProtocol.WellKnownAgentCardPath)));
    }

    [Fact]
    public async Task Signed_tenant_card_keeps_a_stable_weak_etag()
    {
        var http = _host.Http();
        var path = "/acme" + A2AProtocol.WellKnownAgentCardPath;
        var first = await http.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag!;
        Assert.True(etag.IsWeak); // signed bodies may differ byte for byte
        using var weak = new HttpRequestMessage(HttpMethod.Get, path);
        weak.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(weak)).StatusCode);
        using var strong = new HttpRequestMessage(HttpMethod.Get, path);
        strong.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag.Tag));
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(strong)).StatusCode);
    }

    [Fact]
    public async Task A_stable_card_is_signed_once()
    {
        var http = _host.Http();
        var path = "/acme" + A2AProtocol.WellKnownAgentCardPath;
        // ECDSA signatures differ on every signing, so equal bodies mean the signature was reused.
        Assert.Equal(await http.GetStringAsync(path), await http.GetStringAsync(path));
    }
}

/// <summary>Card details that only some tenants have: a required extension, another auth scheme.</summary>
public class MultiAgentCardDetailsTests
{
    private sealed class Cards : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            var card = TestAgentHost.DefaultCard();
            if (scope.Tenant == "strict") card.Capabilities.Extensions = [new AgentExtension { Uri = "urn:must", Required = true }];
            if (scope.Tenant == "basic") card.SecuritySchemes = new() { ["basic"] = new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "Basic" }) };
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    [InlineData(ProtocolBindings.Grpc)]
    public async Task Required_extensions_come_from_the_tenants_card(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddTenantCardProvider<Cards>(), grpc: true);
        var strict = host.Client(binding, options: new A2AClientOptions { Tenant = "strict" });
        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, (await Assert.ThrowsAsync<A2AException>(() => strict.SendMessageAsync("hello"))).Code);
        var withExtension = host.Client(binding, options: new A2AClientOptions { Tenant = "strict", Extensions = ["urn:must"] });
        Assert.Equal("hi there", (await withExtension.SendMessageAsync("hello")).RequireMessage().GetText());
        var relaxed = host.Client(binding, options: new A2AClientOptions { Tenant = "relaxed" });
        Assert.Equal("hi there", (await relaxed.SendMessageAsync("hello")).RequireMessage().GetText());
    }

    [Fact]
    public async Task The_authentication_challenge_names_the_tenants_scheme()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.AllowAnonymous = true,
            services: s => s.AddA2AServer().AddTenantCardProvider<Cards>(), requireAuth: false);
        var http = host.Http();
        async Task<string> Challenge(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
            var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            return resp.Headers.WwwAuthenticate.Single().Scheme;
        }
        Assert.Equal("Basic", await Challenge("/a2a/basic/extendedAgentCard"));
        Assert.Equal("Bearer", await Challenge("/a2a/other/extendedAgentCard"));
    }
}

/// <summary>A card provider whose behaviour tests change at runtime: counts calls, can block or throw for one tenant.</summary>
public sealed class ControlledCards : TenantCardProvider
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public void ResetCalls() => Interlocked.Exchange(ref _calls, 0);

    /// <summary>While set, card calls for <see cref="SlowTenant"/> wait for it.</summary>
    public TaskCompletionSource? Block { get; set; }
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string SlowTenant { get; set; } = "slow";

    /// <summary>Card calls for this tenant throw.</summary>
    public string? FailingTenant { get; set; }

    /// <summary>
    /// While set, every card call for <see cref="SlowTenant"/> posts a hold here and waits until the test completes it, so a
    /// test can check, call by call, what the caller holds while the provider is slow.
    /// </summary>
    public System.Threading.Channels.Channel<TaskCompletionSource>? Holds { get; set; }

    /// <summary>The served card accepts artifacts written by the client (A2A 1.1).</summary>
    public bool UserArtifacts { get; set; }

    public override async ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (scope.Tenant == FailingTenant) throw new InvalidOperationException("card store down");
        if (scope.Tenant == SlowTenant && Holds is { } holds)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await holds.Writer.WriteAsync(hold, cancellationToken);
            await hold.Task.WaitAsync(cancellationToken);
        }
        if (scope.Tenant == SlowTenant && Block is { } block)
        {
            Blocked.TrySetResult();
            await block.Task.WaitAsync(cancellationToken);
        }
        var card = TestAgentHost.DefaultCard();
        if (UserArtifacts) card.Capabilities.UserArtifacts = true;
        return card;
    }
}

/// <summary>An executor resolver tests control: no agent for "noexec", a throwing resolve for "boom", counts resolves.</summary>
public sealed class ControlledAgents : AgentExecutorResolver
{
    private int _resolves;
    public int Resolves => Volatile.Read(ref _resolves);

    public override IAgentExecutor? Resolve(string tenant, IServiceProvider services)
    {
        Interlocked.Increment(ref _resolves);
        return tenant switch
        {
            "noexec" => null,
            "boom" => throw new InvalidOperationException("agent registry down"),
            _ => services.GetRequiredService<IAgentExecutor>(),
        };
    }
}

/// <summary>Executor resolution (ruling R) and push capability resolution (one card call per request, never under a task lock).</summary>
public class MultiAgentResolutionTests : IAsyncLifetime
{
    private readonly ControlledCards _cards = new();
    private readonly ControlledAgents _agents = new();
    private TestAgentHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync(services: s =>
    {
        s.AddA2AServer();
        s.AddSingleton<TenantCardProvider>(_cards);
        s.AddSingleton<AgentExecutorResolver>(_agents);
    }, grpc: true);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private A2AClient Client(string tenant) => _host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { Tenant = tenant });

    private Server.Storage.TaskStore Tasks => _host.App.Services.GetRequiredService<Server.Storage.TaskStore>();

    private int IdempotencyEntries => ((Server.Idempotency.InMemoryIdempotencyStore)_host.App.Services.GetRequiredService<Server.Idempotency.IdempotencyStore>()).Count;

    private Task<Server.Storage.TaskKey> StoreInterruptedTaskAsync(string tenant, string taskId) =>
        StoreInterruptedTaskAsync(Tasks, tenant, taskId);

    private static async Task<Server.Storage.TaskKey> StoreInterruptedTaskAsync(Server.Storage.TaskStore tasks, string tenant, string taskId)
    {
        var key = new Server.Storage.TaskKey(tenant, "alice", taskId);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTask
        {
            Id = taskId, ContextId = "c-" + taskId,
            Status = new AgentTaskStatus { State = TaskState.InputRequired, Timestamp = now },
            History = [new Message { MessageId = "m0-" + taskId, Role = Role.User, Parts = [Part.FromText("first")], TaskId = taskId, ContextId = "c-" + taskId }],
        };
        await tasks.CreateAsync(new Server.Storage.TaskRecord(tenant, "alice", task, 0, now, now));
        return key;
    }

    [Fact]
    public async Task A_tenant_with_a_card_but_no_agent_refuses_a_new_task_before_anything_is_stored()
    {
        var client = Client("noexec");
        var request = new SendMessageRequest
        {
            Message = Message.FromUser("work"),
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "https://x.example/h" } },
        };
        var send = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(request));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, send.Code);
        Assert.Equal("No agent is configured for this tenant.", send.Message);
        var stream = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("work"))) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, stream.Code);
        Assert.Equal("No agent is configured for this tenant.", stream.Message);

        await _host.WaitIdleAsync();
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.DoesNotContain(await Tasks.ListOrphanContextsAsync(DateTimeOffset.UtcNow.AddDays(1), 100), c => c.Tenant == "noexec");
        Assert.Equal(0, IdempotencyEntries);
    }

    [Fact]
    public async Task A_tenant_with_a_card_but_no_agent_refuses_a_follow_up_before_it_is_committed()
    {
        var key = await StoreInterruptedTaskAsync("noexec", "t-follow");
        var version = (await Tasks.GetAsync(key))!.Version;
        var client = Client("noexec");
        var followUp = Message.FromUser("more", taskId: key.TaskId);
        var send = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(new SendMessageRequest { Message = followUp }));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, send.Code);
        var streamed = Message.FromUser("more again", taskId: key.TaskId);
        var stream = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in client.SendStreamingMessageAsync(streamed)) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, stream.Code);

        await _host.WaitIdleAsync();
        var record = await Tasks.GetAsync(key);
        Assert.Single(record!.Task.History!);
        Assert.Equal(version, record.Version);
        Assert.Equal(0, IdempotencyEntries);
    }

    [Fact]
    public async Task A_throwing_resolver_fails_the_task_as_a_throwing_agent_does()
    {
        var task = (await Client("boom").SendMessageAsync("work")).RequireTask();
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The agent failed to process the request.", task.Status.Message!.GetText());
    }

    [Fact]
    public async Task The_agent_is_resolved_once_per_execution()
    {
        var before = _agents.Resolves;
        await Client("acme").SendMessageAsync("task");
        await _host.WaitIdleAsync();
        Assert.Equal(before + 1, _agents.Resolves);
    }

    [Fact]
    public async Task Cancel_without_an_agent_still_cancels()
    {
        var key = await StoreInterruptedTaskAsync("noexec", "t-cancel");
        var canceled = await Client("noexec").CancelTaskAsync(key.TaskId);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
    }

    [Fact]
    public async Task An_execution_asks_for_the_card_once_however_many_events_it_stores()
    {
        var client = Client("acme");
        _cards.ResetCalls();
        var request = new SendMessageRequest
        {
            Message = Message.FromUser("task"), // working, two artifact chunks, completed
            Configuration = new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "https://hooks.example/cb" } },
        };
        Assert.Equal(TaskState.Completed, (await client.SendMessageAsync(request)).RequireTask().Status.State);
        await _host.WaitIdleAsync();
        Assert.True(_host.Webhooks.Calls.Count >= 4, $"expected a webhook per event, got {_host.Webhooks.Calls.Count}");
        Assert.Equal(1, _cards.Calls);
    }

    [Fact]
    public async Task A_slow_card_provider_holds_no_task_lock_during_a_system_commit()
    {
        var handler = _host.Engine;
        var slowKey = await StoreInterruptedTaskAsync("slow", "t-slow");
        // A task of another tenant on the same lock stripe.
        var other = Enumerable.Range(0, 10_000).Select(i => "t-other-" + i)
            .First(id => handler.LockStripe("acme", id) == handler.LockStripe("slow", slowKey.TaskId));
        var otherKey = await StoreInterruptedTaskAsync("acme", other);

        _cards.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = handler.CommitSystemStatusAsync(slowKey, TaskState.Canceled, "timed out");
        await _cards.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(await handler.CommitSystemStatusAsync(otherKey, TaskState.Canceled, "timed out").WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            _cards.Block.TrySetResult();
        }
        Assert.True(await slow.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_slow_card_provider_holds_no_task_lock_while_client_artifacts_are_stored()
    {
        // A2A 1.1 SendMessage with artifactUpdates and no message: the tenant's push capability must be resolved before the
        // task lock, or a slow card provider stalls every task on the same lock stripe.
        var cards = new ControlledCards { UserArtifacts = true };
        await using var host = await TestAgentHost.StartAsync(o => o.EnableProtocolV11(), services: s =>
        {
            s.AddA2AServer();
            s.AddSingleton<TenantCardProvider>(cards);
            s.AddSingleton<AgentExecutorResolver>(new ControlledAgents());
        });
        var handler = host.Engine;
        var tasks = host.App.Services.GetRequiredService<Server.Storage.TaskStore>();
        var slowKey = await StoreInterruptedTaskAsync(tasks, "slow", "t-slow-artifacts");
        var other = Enumerable.Range(0, 10_000).Select(i => "t-other-" + i)
            .First(id => handler.LockStripe("acme", id) == handler.LockStripe("slow", slowKey.TaskId));
        var otherKey = await StoreInterruptedTaskAsync(tasks, "acme", other);

        cards.Holds = System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        var upload = new
        {
            jsonrpc = "2.0", id = 1, method = "SendMessage",
            @params = new
            {
                tenant = "slow",
                artifactUpdates = new[] { new { taskId = slowKey.TaskId, artifact = new { artifactId = "in-1", parts = new[] { new { text = "data" } } } } },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(upload), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, "1.1");
        var slow = host.Http("alice").SendAsync(request);

        // Every card call of the request is held in turn. While each one waits, a task of another tenant on the same lock
        // stripe must still get its lock.
        var held = 0;
        while (true)
        {
            var arrival = cards.Holds.Reader.ReadAsync().AsTask();
            if (await Task.WhenAny(arrival, slow).WaitAsync(TimeSpan.FromSeconds(10)) == slow && !arrival.IsCompleted) break;
            var hold = await arrival;
            held++;
            try
            {
                using var _ = await handler.LockAsync(otherKey, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                hold.TrySetResult();
            }
        }
        Assert.True(held > 0, "The request never asked for the card.");
        using var response = await slow;
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"error\"", body);
        Assert.Single((await tasks.GetAsync(slowKey))!.Task.Artifacts!);
    }

    [Fact]
    public async Task A_failing_card_provider_sends_no_webhooks_for_a_system_commit()
    {
        var client = Client("flaky");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/cb" });
        await _host.WaitIdleAsync();
        var sent = _host.Webhooks.Calls.Count;

        _cards.FailingTenant = "flaky";
        Assert.True(await _host.Engine.CommitSystemStatusAsync(new Server.Storage.TaskKey("flaky", "alice", task.Id), TaskState.Canceled, "timed out"));
        await _host.WaitIdleAsync();
        Assert.Equal(sent, _host.Webhooks.Calls.Count);
        _cards.FailingTenant = null;
        Assert.Equal(TaskState.Canceled, (await client.GetTaskAsync(task.Id)).Status.State);
    }
}

/// <summary>Without a provider or resolver, the single configured card and executor serve every tenant (P0 behaviour).</summary>
public class SingleAgentDefaultsTests
{
    [Fact]
    public async Task The_configured_card_and_executor_serve_every_tenant()
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http();
        var response = await http.GetAsync("/acme" + A2AProtocol.WellKnownAgentCardPath);
        Assert.False(response.Headers.ETag!.IsWeak); // unsigned: the same bytes every time
        var card = await response.Content.ReadFromJsonAsync(A2AJson.GetTypeInfo<AgentCard>());
        Assert.Equal("Scenario Agent", card!.Name);
        Assert.Equal("Scenario Agent", (await http.GetFromJsonAsync(A2AProtocol.WellKnownAgentCardPath, A2AJson.GetTypeInfo<AgentCard>()))!.Name);

        var acme = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { Tenant = "acme" });
        Assert.Equal("hi there", (await acme.SendMessageAsync("hello")).RequireMessage().GetText());
        Assert.Equal("Extended card for alice", (await acme.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest())).Description);
    }
}
