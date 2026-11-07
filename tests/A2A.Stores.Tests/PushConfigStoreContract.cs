using A2A.Server.Postgres;
using A2A.Server.Storage;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="PushConfigStore"/> must have.</summary>
public abstract class PushConfigStoreContract
{
    protected abstract Task<PushConfigStore> CreateStoreAsync();

    private static readonly TaskKey Alice = new("t", "alice", "task1");

    private static PushConfigRecord Config(TaskKey key, string id, string url = "https://hooks.example/cb") => new(key, new TaskPushNotificationConfig
    {
        TaskId = key.TaskId, Id = id, Url = url, Token = "protected:" + id,
        Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "protected-creds" },
    });

    [SkippableFact]
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

    [SkippableFact]
    public async Task Save_with_same_id_replaces()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        await s.SaveAsync(Config(Alice, "c1", "https://hooks.example/v2"));
        Assert.Equal(1, await s.CountAsync(Alice));
        Assert.Equal("https://hooks.example/v2", (await s.GetAsync(Alice, "c1"))!.Config.Url);
    }

    [SkippableFact]
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

    [SkippableFact]
    public async Task List_is_ordered_by_id_ordinal_and_count_matches()
    {
        var s = await CreateStoreAsync();
        foreach (var id in new[] { "b", "B", "a", "10", "9" }) await s.SaveAsync(Config(Alice, id));
        var list = await s.ListAsync(Alice);
        Assert.Equal(new[] { "b", "B", "a", "10", "9" }.Order(StringComparer.Ordinal), list.Select(r => r.Config.Id));
        Assert.Equal(5, await s.CountAsync(Alice));
    }

    [SkippableFact]
    public async Task Delete_removes_once()
    {
        var s = await CreateStoreAsync();
        await s.SaveAsync(Config(Alice, "c1"));
        Assert.True(await s.DeleteAsync(Alice, "c1"));
        Assert.False(await s.DeleteAsync(Alice, "c1"));
        Assert.Null(await s.GetAsync(Alice, "c1"));
    }

    [SkippableFact]
    public async Task Returned_records_are_copies()
    {
        var s = await CreateStoreAsync();
        var input = Config(Alice, "c1");
        await s.SaveAsync(input);
        input.Config.Url = "https://changed.example";
        var got = (await s.GetAsync(Alice, "c1"))!;
        got.Config.Url = "https://changed-again.example";
        Assert.Equal("https://hooks.example/cb", (await s.GetAsync(Alice, "c1"))!.Config.Url);
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
}
