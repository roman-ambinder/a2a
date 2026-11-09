using A2A.Server.Postgres;
using A2A.Server.Storage;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="PushConfigStore"/> must have.</summary>
public abstract class PushConfigStoreContract
{
    protected abstract Task<PushConfigStore> CreateStoreAsync();

    protected static readonly TaskKey Alice = new("t", "alice", "task1");

    protected static PushConfigRecord Config(TaskKey key, string id, string url = "https://hooks.example/cb") => new(key, new TaskPushNotificationConfig
    {
        TaskId = key.TaskId, Id = id, Url = url, Token = "protected:" + id,
        Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "protected-creds" },
    });

    [Fact]
    public async Task Save_then_get_round_trips()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        var got = await s.GetAsync(Alice, "c1");
        Assert.NotNull(got);
        Assert.Equal(Alice, got!.TaskKey);
        Assert.Equal("https://hooks.example/cb", got.Config.Url);
        Assert.Equal("protected:c1", got.Config.Token);
        Assert.Equal("protected-creds", got.Config.Authentication!.Credentials);
    }

    [Fact]
    public async Task Save_with_same_id_replaces()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        await s.SaveAsync(Config(Alice, "c1", "https://hooks.example/v2"));
        Assert.Equal(1, await s.CountAsync(Alice));
        Assert.Equal("https://hooks.example/v2", (await s.GetAsync(Alice, "c1"))!.Config.Url);
    }

    [Fact]
    public async Task Configs_of_another_owner_task_or_tenant_look_missing()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        foreach (var other in new[] { Alice with { Owner = "bob" }, Alice with { TaskId = "task2" }, Alice with { Tenant = "u" } })
        {
            Assert.Null(await s.GetAsync(other, "c1"));
            Assert.Empty(await s.ListAsync(other));
            Assert.Equal(0, await s.CountAsync(other));
            Assert.False(await s.DeleteAsync(other, "c1"));
        }
        Assert.NotNull(await s.GetAsync(Alice, "c1"));
    }

    [Fact]
    public async Task List_is_ordered_by_id_ordinal_and_count_matches()
    {
        var s = await CreateStoreAsync();
        foreach (var id in new[] { "b", "B", "a", "10", "9" }) await s.SaveAsync(Config(Alice, id));
        var list = await s.ListAsync(Alice);
        Assert.Equal(new[] { "b", "B", "a", "10", "9" }.Order(StringComparer.Ordinal), list.Select(r => r.Config.Id));
        Assert.Equal(5, await s.CountAsync(Alice));
    }

    [Fact]
    public async Task Delete_removes_once()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        Assert.True(await s.DeleteAsync(Alice, "c1"));
        Assert.False(await s.DeleteAsync(Alice, "c1"));
        Assert.Null(await s.GetAsync(Alice, "c1"));
    }

    [Fact]
    public async Task Saved_config_reads_back_with_the_same_content()
    {
        // Stores own the configs they are given and return; callers never mutate them (a change is a new SaveAsync).
        var s = await CreateStoreAsync();
        var input = Config(Alice, "c1");
        await s.SaveAsync(input);
        var got = (await s.GetAsync(Alice, "c1"))!;
        Assert.Equal(A2A.Json.A2AJson.Serialize(input.Config), A2A.Json.A2AJson.Serialize(got.Config));

        var replaced = Config(Alice, "c1");
        replaced.Config.Url = "https://changed.example";
        await s.SaveAsync(replaced);
        Assert.Equal("https://changed.example", (await s.GetAsync(Alice, "c1"))!.Config.Url);
    }

    [Fact]
    public async Task DeleteAll_removes_every_config_of_the_task_only()
    {
        var s = await CreateStoreAsync();
        var other = Alice with { TaskId = "task2" };
        foreach (var id in new[] { "c1", "c2", "c3" }) await s.SaveAsync(Config(Alice, id));
        await s.SaveAsync(Config(other, "c1"));

        await s.DeleteAllAsync(Alice);
        Assert.Equal(0, await s.CountAsync(Alice));
        Assert.Equal(1, await s.CountAsync(other));
        await s.DeleteAllAsync(Alice); // nothing left is fine
    }

    [Fact]
    public async Task DeleteAll_with_another_owner_or_tenant_deletes_nothing()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        await s.DeleteAllAsync(Alice with { Owner = "bob" });
        await s.DeleteAllAsync(Alice with { Tenant = "u" });
        Assert.Equal(1, await s.CountAsync(Alice));
    }

    [Fact]
    public async Task SaveIfUnderLimit_adds_until_the_limit_then_refuses_new_ids()
    {
        var s = await CreateStoreAsync();
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice, "c1"), 2));
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice, "c2"), 2));
        Assert.False(await s.SaveIfUnderLimitAsync(Config(Alice, "c3"), 2));
        Assert.Null(await s.GetAsync(Alice, "c3"));
        Assert.Equal(2, await s.CountAsync(Alice));
    }

    [Fact]
    public async Task SaveIfUnderLimit_replaces_an_existing_id_even_at_the_limit()
    {
        var s = await CreateStoreAsync();
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice, "c1"), 1));
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice, "c1", "https://hooks.example/v2"), 1));
        Assert.Equal("https://hooks.example/v2", (await s.GetAsync(Alice, "c1"))!.Config.Url);
        Assert.Equal(1, await s.CountAsync(Alice));
    }

    [Fact]
    public async Task SaveIfUnderLimit_counts_per_task_and_owner()
    {
        var s = await CreateStoreAsync();
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice, "c1"), 1));
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice with { TaskId = "task2" }, "c1"), 1));
        Assert.True(await s.SaveIfUnderLimitAsync(Config(Alice with { Owner = "bob" }, "c1"), 1));
        Assert.False(await s.SaveIfUnderLimitAsync(Config(Alice, "c2"), 1));
    }

    [Fact]
    public async Task SaveIfUnderLimit_is_atomic_under_parallel_savers()
    {
        var s = await CreateStoreAsync();
        var go = new TaskCompletionSource();
        var savers = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await go.Task;
            return await s.SaveIfUnderLimitAsync(Config(Alice, $"c{i:D2}"), 10);
        })).ToArray();
        go.SetResult();
        var results = await Task.WhenAll(savers);

        Assert.Equal(10, results.Count(r => r));
        Assert.Equal(10, await s.CountAsync(Alice));
    }
}

public sealed class InMemoryPushConfigStoreTests : PushConfigStoreContract
{
    protected override Task<PushConfigStore> CreateStoreAsync() => Task.FromResult<PushConfigStore>(new InMemoryPushConfigStore());
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresPushConfigStoreTests(PostgresFixture db) : PushConfigStoreContract
{
    protected override async Task<PushConfigStore> CreateStoreAsync() => new PostgresPushConfigStore(db.DataSource, await db.NewSchemaAsync());

    /// <summary>The 20-saver race again, with the task's row present (the production case; the contract test has no task row).</summary>
    [Fact]
    public async Task SaveIfUnderLimit_is_atomic_under_parallel_savers_when_the_task_row_exists()
    {
        var schema = await db.NewSchemaAsync();
        var s = new PostgresPushConfigStore(db.DataSource, schema);
        var now = DateTimeOffset.UtcNow;
        await new PostgresTaskStore(db.DataSource, new A2A.Server.StreamingOptions(), schema).CreateAsync(new TaskRecord(
            Alice.Tenant, Alice.Owner,
            new AgentTask { Id = Alice.TaskId, ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Working, Timestamp = now } },
            0, now, now));

        var go = new TaskCompletionSource();
        var savers = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await go.Task;
            return await s.SaveIfUnderLimitAsync(Config(Alice, $"c{i:D2}"), 10);
        })).ToArray();
        go.SetResult();

        Assert.Equal(10, (await Task.WhenAll(savers)).Count(r => r));
        Assert.Equal(10, await s.CountAsync(Alice));
    }
}
