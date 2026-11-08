using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using A2A.Client;
using A2A.Json;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Two agents on one host: "acme" (streaming and push on, own signing key) and "globex" (both off, global key).</summary>
public sealed class TenantCards : AgentCardProvider
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

    public async Task InitializeAsync() => _host = await TestAgentHost.StartAsync(
        configureHttp: h => h.SigningKey = GlobalKey,
        services: s => s.AddA2AServer().AddAgentCardProvider<TenantCards>().AddAgentExecutorResolver<TenantAgents>(),
        grpc: true);

    public async Task DisposeAsync() => await _host.DisposeAsync();

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
        Assert.Equal(("http://localhost", (string?)"acme-key"), acme.Identity);

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
    public async Task Signed_tenant_card_keeps_a_stable_etag()
    {
        var http = _host.Http();
        var path = "/acme" + A2AProtocol.WellKnownAgentCardPath;
        var first = await http.GetAsync(path);
        first.EnsureSuccessStatusCode();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);
    }
}

/// <summary>Card details that only some tenants have: a required extension, another auth scheme.</summary>
public class MultiAgentCardDetailsTests
{
    private sealed class Cards : AgentCardProvider
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
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddAgentCardProvider<Cards>(), grpc: true);
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
            services: s => s.AddA2AServer().AddAgentCardProvider<Cards>(), requireAuth: false);
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

/// <summary>Without a provider or resolver, the single configured card and executor serve every tenant (P0 behaviour).</summary>
public class SingleAgentDefaultsTests
{
    [Fact]
    public async Task The_configured_card_and_executor_serve_every_tenant()
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http();
        var card = await http.GetFromJsonAsync("/acme" + A2AProtocol.WellKnownAgentCardPath, A2AJson.GetTypeInfo<AgentCard>());
        Assert.Equal("Scenario Agent", card!.Name);
        Assert.Equal("Scenario Agent", (await http.GetFromJsonAsync(A2AProtocol.WellKnownAgentCardPath, A2AJson.GetTypeInfo<AgentCard>()))!.Name);

        var acme = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { Tenant = "acme" });
        Assert.Equal("hi there", (await acme.SendMessageAsync("hello")).RequireMessage().GetText());
        Assert.Equal("Extended card for alice", (await acme.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest())).Description);
    }
}
