using A2A.Json;
using A2A.Server.Postgres;
using A2A.Server.Storage;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="TaskStore"/> must have. Each store runs the same tests through a subclass.</summary>
public abstract class TaskStoreContract
{
    protected abstract Task<TaskStore> CreateStoreAsync(TimeProvider? time = null);

    internal static TaskRecord NewRecord(string tenant, string owner, string id, DateTimeOffset? at = null, string contextId = "ctx")
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return new TaskRecord(tenant, owner,
            new AgentTask { Id = id, ContextId = contextId, Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = now } },
            0, now, now);
    }

    internal static TaskRecord WithState(TaskRecord r, TaskState s)
    {
        var task = r.Task.CopyForUpdate(); // a new status: the stored task is never changed (stores own their records)
        task.Status.State = s;
        return r with { Task = task };
    }

    private static TaskQuery Query(string tenant, string owner, int pageSize = 50, string? token = null, string? contextId = null,
        TaskState? status = null, DateTimeOffset? after = null) =>
        new(tenant, owner, contextId, status, after, pageSize, token);

    private static string[] Ids(TaskPage page) => page.Records.Select(r => r.Task.Id).ToArray();

    [SkippableFact]
    public async Task Get_with_wrong_owner_returns_null()
    {
        var s = await CreateStoreAsync();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        Assert.Null(await s.GetAsync(new TaskKey("t", "bob", "id1")));
        Assert.Equal(1, (await s.GetAsync(r.Key))!.Version);
    }

    [SkippableFact]
    public async Task Get_with_wrong_tenant_returns_null()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(NewRecord("t", "alice", "id1"));
        Assert.Null(await s.GetAsync(new TaskKey("T", "alice", "id1")));
    }

    [SkippableFact]
    public async Task Get_of_missing_task_returns_null()
    {
        var s = await CreateStoreAsync();
        Assert.Null(await s.GetAsync(new TaskKey("t", "alice", "nope")));
    }

    [SkippableFact]
    public async Task Create_returns_version_1_and_round_trips_the_task()
    {
        var s = await CreateStoreAsync();
        var input = NewRecord("t", "alice", "id1");
        input.Task.Metadata = new Dictionary<string, System.Text.Json.JsonElement> { ["k"] = A2AJson.SerializeToElement("v") };
        var created = await s.CreateAsync(input);
        Assert.Equal(1, created.Version);

        var got = (await s.GetAsync(created.Key))!;
        Assert.Equal(1, got.Version);
        Assert.Equal("alice", got.Owner);
        Assert.Equal("t", got.Tenant);
        Assert.Equal(input.CreatedAt, got.CreatedAt);
        Assert.Equal(A2AJson.Serialize(created.Task), A2AJson.Serialize(got.Task));
        Assert.Equal("v", got.Task.Metadata!["k"].GetString());
    }

    [SkippableFact]
    public async Task Update_with_stale_version_throws()
    {
        var s = await CreateStoreAsync();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        var r2 = await s.UpdateAsync(r, 1);
        Assert.Equal(2, r2.Version);
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(r, 1).AsTask());
    }

    [SkippableFact]
    public async Task Update_of_missing_task_throws_concurrency()
    {
        var s = await CreateStoreAsync();
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(NewRecord("t", "a", "ghost"), 1).AsTask());
    }

    [SkippableFact]
    public async Task Update_keeps_created_time_and_stores_new_state()
    {
        var clock = new A2A.Tests.FakeTimeProvider();
        var s = await CreateStoreAsync(clock);
        var created = await s.CreateAsync(NewRecord("t", "a", "id1", at: clock.GetUtcNow().AddDays(-1)));
        var updated = await s.UpdateAsync(WithState(created with { CreatedAt = DateTimeOffset.UnixEpoch }, TaskState.Working), 1);

        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        var got = (await s.GetAsync(created.Key))!;
        Assert.Equal(TaskState.Working, got.Task.Status.State);
        Assert.Equal(2, got.Version);
        Assert.Equal(created.CreatedAt, got.CreatedAt);
    }

    [SkippableFact]
    public async Task Update_by_other_owner_throws_and_leaves_task_unchanged()
    {
        var s = await CreateStoreAsync();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(WithState(r with { Owner = "bob" }, TaskState.Failed), 1).AsTask());
        var stored = (await s.GetAsync(r.Key))!;
        Assert.Equal(1, stored.Version);
        Assert.Equal(TaskState.Submitted, stored.Task.Status.State);
    }

    [SkippableFact]
    public async Task Create_twice_throws_duplicate()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(NewRecord("t", "a", "id1"));
        await Assert.ThrowsAsync<DuplicateTaskException>(() => s.CreateAsync(NewRecord("t", "b", "id1")).AsTask());
    }

    [SkippableFact]
    public async Task Same_id_in_another_tenant_is_a_different_task()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(NewRecord("t", "a", "id1"));
        await s.CreateAsync(NewRecord("u", "a", "id1"));
        Assert.NotNull(await s.GetAsync(new TaskKey("u", "a", "id1")));
    }

    [SkippableFact]
    public async Task Create_requires_version_zero()
    {
        var s = await CreateStoreAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => s.CreateAsync(NewRecord("t", "a", "id1") with { Version = 3 }).AsTask());
    }

    [SkippableFact]
    public async Task Update_leaves_an_earlier_returned_record_unchanged()
    {
        // Stores own their records and callers never mutate them: a change is a new task passed to UpdateAsync.
        var s = await CreateStoreAsync();
        var r = await s.CreateAsync(NewRecord("t", "a", "id1"));
        await s.UpdateAsync(WithState(r, TaskState.Failed), 1);
        Assert.Equal(TaskState.Submitted, r.Task.Status.State);
        Assert.Equal(TaskState.Failed, (await s.GetAsync(r.Key))!.Task.Status.State);
    }

    [SkippableFact]
    public async Task Created_record_reads_back_with_the_same_content()
    {
        var s = await CreateStoreAsync();
        var input = NewRecord("t", "a", "id1");
        var created = await s.CreateAsync(input);
        var read = (await s.GetAsync(input.Key))!;
        Assert.Equal(A2AJson.Serialize(input.Task), A2AJson.Serialize(created.Task));
        Assert.Equal(A2AJson.Serialize(input.Task), A2AJson.Serialize(read.Task));
    }

    [SkippableFact]
    public async Task GetOrCreateContext_is_first_writer_wins()
    {
        var s = await CreateStoreAsync();
        var a = s.GetOrCreateContextAsync(new("t", "alice", "c1", DateTimeOffset.UtcNow)).AsTask();
        var b = s.GetOrCreateContextAsync(new("t", "bob", "c1", DateTimeOffset.UtcNow)).AsTask();
        var res = await Task.WhenAll(a, b);
        Assert.Equal(res[0].Owner, res[1].Owner);
        Assert.Equal(res[0].Owner, (await s.GetContextAsync("t", "c1"))!.Owner);
        Assert.Null(await s.GetContextAsync("other", "c1"));
    }

    [SkippableFact]
    public async Task List_filters_by_owner_and_tenant_before_counting()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(NewRecord("t", "alice", "a1"));
        await s.CreateAsync(NewRecord("t", "alice", "a2"));
        await s.CreateAsync(NewRecord("t", "bob", "b1"));
        await s.CreateAsync(NewRecord("u", "alice", "u1"));

        var page = await s.ListAsync(Query("t", "alice"));
        Assert.Equal(["a1", "a2"], Ids(page).Order().ToArray());
        Assert.Equal(2, page.TotalSize);
        Assert.Equal(1, (await s.ListAsync(Query("t", "bob"))).TotalSize);
        Assert.Equal(0, (await s.ListAsync(Query("t", "carol"))).TotalSize);
    }

    [SkippableFact]
    public async Task List_filters_by_context_and_status()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(NewRecord("t", "a", "x1", contextId: "c1"));
        var x2 = await s.CreateAsync(NewRecord("t", "a", "x2", contextId: "c2"));
        await s.UpdateAsync(WithState(x2, TaskState.Completed), x2.Version);

        Assert.Equal(["x1"], Ids(await s.ListAsync(Query("t", "a", contextId: "c1"))));
        Assert.Equal(["x2"], Ids(await s.ListAsync(Query("t", "a", status: TaskState.Completed))));
        Assert.Empty(Ids(await s.ListAsync(Query("t", "a", contextId: "c1", status: TaskState.Completed))));
    }

    [SkippableFact]
    public async Task List_filters_by_status_timestamp_inclusive()
    {
        var s = await CreateStoreAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++) await s.CreateAsync(NewRecord("t", "a", $"id{i}", t0.AddMinutes(i)));

        var page = await s.ListAsync(Query("t", "a", after: t0.AddMinutes(2)));
        Assert.Equal(["id3", "id2"], Ids(page));
        Assert.Equal(2, page.TotalSize);
    }

    [SkippableFact]
    public async Task List_uses_keyset_paging_newest_first()
    {
        var s = await CreateStoreAsync();
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) await s.CreateAsync(NewRecord("t", "a", $"id{i}", t0.AddSeconds(i)));

        var p1 = await s.ListAsync(Query("t", "a", pageSize: 2));
        Assert.Equal(["id4", "id3"], Ids(p1));
        Assert.Equal(5, p1.TotalSize);
        Assert.NotEmpty(p1.NextPageToken);

        var p2 = await s.ListAsync(Query("t", "a", pageSize: 2, token: p1.NextPageToken));
        Assert.Equal(["id2", "id1"], Ids(p2));
        Assert.Equal(5, p2.TotalSize);

        var p3 = await s.ListAsync(Query("t", "a", pageSize: 2, token: p2.NextPageToken));
        Assert.Equal(["id0"], Ids(p3));
        Assert.Equal(string.Empty, p3.NextPageToken);
    }

    [SkippableFact]
    public async Task List_breaks_timestamp_ties_by_ordinal_id()
    {
        var s = await CreateStoreAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        foreach (var id in new[] { "b", "B", "a", "_", "A" }) await s.CreateAsync(NewRecord("t", "a", id, t0));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await s.ListAsync(Query("t", "a", pageSize: 2, token: token));
            all.AddRange(Ids(page));
            token = page.NextPageToken;
        } while (token != string.Empty);

        Assert.Equal(new[] { "b", "B", "a", "_", "A" }.Order(StringComparer.Ordinal), all);
    }

    [SkippableFact]
    public async Task List_sorts_a_task_without_status_timestamp_by_its_creation_time()
    {
        var s = await CreateStoreAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        await s.CreateAsync(NewRecord("t", "a", "old", t0));
        var none = NewRecord("t", "a", "mid", t0.AddMinutes(1));
        none.Task.Status.Timestamp = null;
        var created = await s.CreateAsync(none);
        await s.CreateAsync(NewRecord("t", "a", "new", t0.AddMinutes(2)));

        var p1 = await s.ListAsync(Query("t", "a", pageSize: 2));
        Assert.Equal(["new", "mid"], Ids(p1));
        Assert.Equal(["old"], Ids(await s.ListAsync(Query("t", "a", pageSize: 2, token: p1.NextPageToken))));

        // Still sorted by creation time after an update that leaves the timestamp unset.
        await s.UpdateAsync(WithState(created, TaskState.Working), created.Version);
        Assert.Equal(["new", "mid", "old"], Ids(await s.ListAsync(Query("t", "a"))));
    }

    [SkippableFact]
    public async Task List_with_bad_page_token_is_invalid_params()
    {
        var s = await CreateStoreAsync();
        var ex = await Assert.ThrowsAsync<A2AException>(() => s.ListAsync(Query("t", "a", token: "garbage!")).AsTask());
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [SkippableFact]
    public async Task Parallel_updates_with_same_version_have_exactly_one_winner()
    {
        for (var round = 0; round < 10; round++)
        {
            var s = await CreateStoreAsync();
            var r = await s.CreateAsync(NewRecord("t", "a", "id1"));
            using var ready = new CountdownEvent(10);
            using var go = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                ready.Signal();
                go.Wait();
                try { await s.UpdateAsync(WithState(r, TaskState.Working), 1); return true; }
                catch (ConcurrencyException) { return false; }
            })).ToArray();
            ready.Wait();
            go.Set();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(1, results.Count(ok => ok));
            Assert.Equal(9, results.Count(ok => !ok));
            Assert.Equal(2, (await s.GetAsync(r.Key))!.Version);
        }
    }
}

public sealed class InMemoryTaskStoreTests : TaskStoreContract
{
    protected override Task<TaskStore> CreateStoreAsync(TimeProvider? time = null) => Task.FromResult<TaskStore>(new InMemoryTaskStore());
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresTaskStoreTests(PostgresFixture db) : TaskStoreContract
{
    protected override async Task<TaskStore> CreateStoreAsync(TimeProvider? time = null) =>
        new PostgresTaskStore(db.DataSource, await db.NewSchemaAsync(), time);
}
