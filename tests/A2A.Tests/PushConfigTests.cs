using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

public class PushConfigTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async Task InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static TaskPushNotificationConfig Secret(string taskId, string? id = null) => new()
    {
        TaskId = taskId, Id = id, Url = "https://hooks.example/cb", Token = "s3cret",
        Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "abc" },
    };

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Get_and_list_redact_token_and_credentials(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));
        Assert.Equal("", created.Token);
        Assert.Equal("", created.Authentication!.Credentials);
        Assert.Equal("Bearer", created.Authentication.Scheme);

        var got = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });
        Assert.Equal("", got.Token);
        Assert.Equal("", got.Authentication!.Credentials);

        var listed = Assert.Single((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);
        Assert.Equal("", listed.Token);
        Assert.Equal("", listed.Authentication!.Credentials);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Stored_secret_is_not_plaintext(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));

        var rec = await _host.App.Services.GetRequiredService<PushConfigStore>()
            .GetAsync(new TaskKey("", "alice", task.Id), created.Id!);
        Assert.NotNull(rec);
        Assert.NotEqual("s3cret", rec!.Config.Token);
        Assert.NotEqual("abc", rec.Config.Authentication!.Credentials);
        Assert.False(string.IsNullOrEmpty(rec.Config.Token));
    }

    [Fact]
    public async Task Inline_config_on_new_task_is_stored_protected()
    {
        var client = _host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync(Message.FromUser("input"),
            new SendMessageConfiguration { TaskPushNotificationConfig = Secret("") })).RequireTask();
        var rec = Assert.Single(await _host.App.Services.GetRequiredService<PushConfigStore>().ListAsync(new TaskKey("", "alice", task.Id)));
        Assert.NotEqual("s3cret", rec.Config.Token);
        Assert.NotEqual("abc", rec.Config.Authentication!.Credentials);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Foreign_owner_cannot_read_push_config(string binding)
    {
        var alice = _host.Client(binding, "alice");
        var task = (await alice.SendMessageAsync("input")).RequireTask();
        var created = await alice.CreateTaskPushNotificationConfigAsync(Secret(task.Id));

        var ex = await Assert.ThrowsAsync<A2AException>(() => _host.Client(binding, "bob")
            .GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! }));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task Store_is_keyed_by_owner()
    {
        var store = new InMemoryPushConfigStore();
        var key = new TaskKey("", "alice", "t1");
        await store.SaveAsync(new PushConfigRecord(key, new TaskPushNotificationConfig { Id = "c1", TaskId = "t1", Url = "https://x.example/" }));
        Assert.NotNull(await store.GetAsync(key, "c1"));
        Assert.Null(await store.GetAsync(key with { Owner = "bob" }, "c1"));
        Assert.Null(await store.GetAsync(key with { Tenant = "other" }, "c1"));
        Assert.Empty(await store.ListAsync(key with { Owner = "bob" }));
        Assert.Equal(1, await store.CountAsync(key));
        Assert.False(await store.DeleteAsync(key with { Owner = "bob" }, "c1"));
        Assert.True(await store.DeleteAsync(key, "c1"));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Push_configs_survive_terminal_state(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));
        var done = (await client.SendMessageAsync("finish", task.ContextId, task.Id)).RequireTask();
        Assert.Equal(TaskState.Completed, done.Status.State);
        await _host.WaitIdleAsync();

        var listed = Assert.Single((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);
        Assert.Equal(created.Id, listed.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Create_beyond_MaxPushConfigsPerTask_fails(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        for (var i = 0; i < 10; i++) await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(Secret(task.Id)));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Contains("max 10", ex.Message);
    }

    [Fact]
    public void Ephemeral_protector_round_trips_and_is_not_plaintext()
    {
        var p = new EphemeralPushSecretProtector();
        var a = p.Protect("s3cret");
        Assert.StartsWith("e1:", a);
        Assert.DoesNotContain("s3cret", a);
        Assert.NotEqual(a, p.Protect("s3cret"));
        Assert.Equal("s3cret", p.Unprotect(a));
        Assert.Equal("", p.Unprotect(p.Protect("")));
        Assert.ThrowsAny<Exception>(() => { p.Unprotect("e1:AAAA"); });
        Assert.ThrowsAny<Exception>(() => { new EphemeralPushSecretProtector().Unprotect(a); });
    }

    [Fact]
    public void Ephemeral_protector_is_safe_under_concurrency_and_never_reuses_a_nonce()
    {
        var p = new EphemeralPushSecretProtector();
        var plain = Enumerable.Range(0, 64).Select(i => $"secret-{i}").ToArray();
        var protectedValues = new string[plain.Length];
        Parallel.For(0, plain.Length, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i => protectedValues[i] = p.Protect(plain[i]));
        Parallel.For(0, plain.Length, i => Assert.Equal(plain[i], p.Unprotect(protectedValues[i])));
        var nonces = protectedValues.Select(v => Convert.ToBase64String(Signing.Base64Url.Decode(v["e1:".Length..])[..12])).ToHashSet();
        Assert.Equal(plain.Length, nonces.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Inline_config_on_existing_task_respects_MaxPushConfigsPerTask(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        for (var i = 0; i < 10; i++) await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(
            Message.FromUser("more", task.ContextId, task.Id),
            new SendMessageConfiguration { TaskPushNotificationConfig = Secret("") }));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Equal(10, (await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_to_a_terminal_task_at_the_config_limit_gets_the_state_error(string binding)
    {
        var client = _host.Client(binding, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        for (var i = 0; i < 10; i++) await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));
        var done = (await client.SendMessageAsync("finish", task.ContextId, task.Id)).RequireTask();
        Assert.Equal(TaskState.Completed, done.Status.State);
        await _host.WaitIdleAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(
            Message.FromUser("more", task.ContextId, task.Id),
            new SendMessageConfiguration { TaskPushNotificationConfig = Secret("") }));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Fact]
    public async Task Inline_config_on_new_task_is_accepted_when_the_limit_is_zero()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxPushConfigsPerTask = 0);
        var client = host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync(Message.FromUser("input"),
            new SendMessageConfiguration { TaskPushNotificationConfig = Secret("") })).RequireTask();
        Assert.Single(await host.App.Services.GetRequiredService<PushConfigStore>().ListAsync(new TaskKey("", "alice", task.Id)));
    }

    [Fact]
    public async Task Service_CreateAsync_rejects_a_private_network_webhook_URL_on_its_own()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.AllowPrivateNetworks = false);
        var service = host.App.Services.GetRequiredService<PushConfigService>();
        var config = new TaskPushNotificationConfig { TaskId = "t1", Url = "https://10.0.0.1/hook" };
        var ex = await Assert.ThrowsAnyAsync<A2AException>(async () =>
            await service.CreateAsync(new TaskKey("", "alice", "t1"), config, CancellationToken.None));

        var client = host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var viaHandler = await Assert.ThrowsAnyAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(
            new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://10.0.0.1/hook" }));
        Assert.Equal(viaHandler.Code, ex.Code);
        Assert.Equal(viaHandler.Message, ex.Message);
    }

    /// <summary>A store whose count and lookups are stale (a concurrent saver took the last slot): only the atomic save can hold the limit.</summary>
    private sealed class StaleCountPushConfigStore : PushConfigStore
    {
        private readonly InMemoryPushConfigStore _inner = new();
        public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default) => _inner.SaveAsync(record, cancellationToken);
        public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken cancellationToken = default) => _inner.GetAsync(task, configId, cancellationToken);
        public override ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken cancellationToken = default) => _inner.ListAsync(task, cancellationToken);
        public override ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default) => ValueTask.FromResult(0);
        public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(task, configId, cancellationToken);
        public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default) => _inner.DeleteAllAsync(task, cancellationToken);
        public override ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken cancellationToken = default) => _inner.SaveIfUnderLimitAsync(record, limit, cancellationToken);
    }

    [Fact]
    public async Task Inline_config_on_existing_task_holds_the_limit_through_the_atomic_save_and_leaves_no_message()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<PushConfigStore, StaleCountPushConfigStore>());
        var client = host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        for (var i = 0; i < 10; i++) await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));
        var historyBefore = (await client.GetTaskAsync(task.Id)).History?.Count ?? 0;

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(
            Message.FromUser("more", task.ContextId, task.Id),
            new SendMessageConfiguration { TaskPushNotificationConfig = Secret("") }));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Contains("max 10", ex.Message);
        Assert.Equal(10, (await host.App.Services.GetRequiredService<PushConfigStore>().ListAsync(new TaskKey("", "alice", task.Id))).Count);
        Assert.Equal(historyBefore, (await client.GetTaskAsync(task.Id)).History?.Count ?? 0);
    }

    [Fact]
    public async Task Data_protection_protector_round_trips_and_is_used_by_the_store_path()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ADataProtectionPushSecrets());
        var protector = host.App.Services.GetRequiredService<IPushSecretProtector>();
        Assert.IsType<DataProtectionPushSecretProtector>(protector);
        Assert.Equal("s3cret", protector.Unprotect(protector.Protect("s3cret")));

        var client = host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(Secret(task.Id));
        var rec = await host.App.Services.GetRequiredService<PushConfigStore>().GetAsync(new TaskKey("", "alice", task.Id), created.Id!);
        Assert.False(rec!.Config.Token!.StartsWith("e1:", StringComparison.Ordinal));
        Assert.Equal("s3cret", protector.Unprotect(rec.Config.Token!));
    }
}
