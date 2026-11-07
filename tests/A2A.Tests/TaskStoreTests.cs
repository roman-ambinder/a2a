using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;

namespace A2A.Tests;

public class TaskStoreTests
{
    private static TaskRecord NewRecord(string tenant, string owner, string id, DateTimeOffset? at = null, string contextId = "ctx")
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return new TaskRecord(tenant, owner,
            new AgentTask { Id = id, ContextId = contextId, Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = now } },
            0, now, now);
    }

    internal static TaskRecord WithState(TaskRecord r, TaskState s)
    {
        var task = A2AJson.Clone(r.Task);
        task.Status.State = s;
        return r with { Task = task };
    }

    private static TaskQuery Query(string tenant, string owner, int pageSize = 50, string? token = null, string? contextId = null, TaskState? status = null) =>
        new(tenant, owner, contextId, status, null, pageSize, token);

    [Fact]
    public async Task Get_with_wrong_owner_returns_null()
    {
        var s = new InMemoryTaskStore();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        Assert.Null(await s.GetAsync(new TaskKey("t", "bob", "id1")));
        Assert.Equal(1, (await s.GetAsync(r.Key))!.Version);
    }

    [Fact]
    public async Task Get_with_wrong_tenant_returns_null()
    {
        var s = new InMemoryTaskStore();
        await s.CreateAsync(NewRecord("t", "alice", "id1"));
        Assert.Null(await s.GetAsync(new TaskKey("T", "alice", "id1")));
    }

    [Fact]
    public async Task Update_with_stale_version_throws()
    {
        var s = new InMemoryTaskStore();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        var r2 = await s.UpdateAsync(r, 1);
        Assert.Equal(2, r2.Version);
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(r, 1).AsTask());
    }

    [Fact]
    public async Task Update_by_other_owner_throws_and_leaves_task_unchanged()
    {
        var s = new InMemoryTaskStore();
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(WithState(r with { Owner = "bob" }, TaskState.Failed), 1).AsTask());
        var stored = (await s.GetAsync(r.Key))!;
        Assert.Equal(1, stored.Version);
        Assert.Equal(TaskState.Submitted, stored.Task.Status.State);
    }

    [Fact]
    public async Task Create_twice_throws_duplicate()
    {
        var s = new InMemoryTaskStore();
        await s.CreateAsync(NewRecord("t", "a", "id1"));
        await Assert.ThrowsAsync<DuplicateTaskException>(() => s.CreateAsync(NewRecord("t", "b", "id1")).AsTask());
    }

    [Fact]
    public async Task Create_requires_version_zero()
    {
        var s = new InMemoryTaskStore();
        await Assert.ThrowsAsync<ArgumentException>(() => s.CreateAsync(NewRecord("t", "a", "id1") with { Version = 3 }).AsTask());
    }

    [Fact]
    public async Task Mutating_returned_record_does_not_change_store()
    {
        var s = new InMemoryTaskStore();
        var r = await s.CreateAsync(NewRecord("t", "a", "id1"));
        r.Task.Status.State = TaskState.Failed;
        Assert.Equal(TaskState.Submitted, (await s.GetAsync(r.Key))!.Task.Status.State);
    }

    [Fact]
    public async Task Mutating_input_record_after_write_does_not_change_store()
    {
        var s = new InMemoryTaskStore();
        var input = NewRecord("t", "a", "id1");
        await s.CreateAsync(input);
        input.Task.Status.State = TaskState.Failed;
        Assert.Equal(TaskState.Submitted, (await s.GetAsync(input.Key))!.Task.Status.State);
    }

    [Fact]
    public async Task GetOrCreateContext_is_first_writer_wins()
    {
        var s = new InMemoryTaskStore();
        var a = s.GetOrCreateContextAsync(new("t", "alice", "c1", DateTimeOffset.UtcNow)).AsTask();
        var b = s.GetOrCreateContextAsync(new("t", "bob", "c1", DateTimeOffset.UtcNow)).AsTask();
        var res = await Task.WhenAll(a, b);
        Assert.Equal(res[0].Owner, res[1].Owner);
        Assert.Equal(res[0].Owner, (await s.GetContextAsync("t", "c1"))!.Owner);
        Assert.Null(await s.GetContextAsync("other", "c1"));
    }

    [Fact]
    public void Task_ids_are_22_char_base64url_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => TaskIds.New()).ToHashSet();
        Assert.Equal(1000, ids.Count);
        Assert.All(ids, i => Assert.Matches("^[A-Za-z0-9_-]{22}$", i));
    }

    [Fact]
    public async Task List_filters_by_owner_and_tenant_before_counting()
    {
        var s = new InMemoryTaskStore();
        await s.CreateAsync(NewRecord("t", "alice", "a1"));
        await s.CreateAsync(NewRecord("t", "alice", "a2"));
        await s.CreateAsync(NewRecord("t", "bob", "b1"));
        await s.CreateAsync(NewRecord("u", "alice", "u1"));

        var page = await s.ListAsync(Query("t", "alice"));
        Assert.Equal(["a1", "a2"], page.Records.Select(r => r.Task.Id).Order().ToArray());
        Assert.Equal(2, page.TotalSize);
        Assert.Equal(1, (await s.ListAsync(Query("t", "bob"))).TotalSize);
        Assert.Equal(0, (await s.ListAsync(Query("t", "carol"))).TotalSize);
    }

    [Fact]
    public async Task List_filters_by_context_and_status()
    {
        var s = new InMemoryTaskStore();
        await s.CreateAsync(NewRecord("t", "a", "x1", contextId: "c1"));
        var x2 = await s.CreateAsync(NewRecord("t", "a", "x2", contextId: "c2"));
        await s.UpdateAsync(WithState(x2, TaskState.Completed), x2.Version);

        Assert.Equal(["x1"], (await s.ListAsync(Query("t", "a", contextId: "c1"))).Records.Select(r => r.Task.Id).ToArray());
        Assert.Equal(["x2"], (await s.ListAsync(Query("t", "a", status: TaskState.Completed))).Records.Select(r => r.Task.Id).ToArray());
    }

    [Fact]
    public async Task List_uses_keyset_paging_newest_first()
    {
        var s = new InMemoryTaskStore();
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) await s.CreateAsync(NewRecord("t", "a", $"id{i}", t0.AddSeconds(i)));

        var p1 = await s.ListAsync(Query("t", "a", pageSize: 2));
        Assert.Equal(["id4", "id3"], p1.Records.Select(r => r.Task.Id).ToArray());
        Assert.Equal(5, p1.TotalSize);
        Assert.NotEmpty(p1.NextPageToken);

        var p2 = await s.ListAsync(Query("t", "a", pageSize: 2, token: p1.NextPageToken));
        Assert.Equal(["id2", "id1"], p2.Records.Select(r => r.Task.Id).ToArray());

        var p3 = await s.ListAsync(Query("t", "a", pageSize: 2, token: p2.NextPageToken));
        Assert.Equal(["id0"], p3.Records.Select(r => r.Task.Id).ToArray());
        Assert.Equal(string.Empty, p3.NextPageToken);
    }

    [Fact]
    public async Task List_with_bad_page_token_is_invalid_params()
    {
        var s = new InMemoryTaskStore();
        var ex = await Assert.ThrowsAsync<A2AException>(() => s.ListAsync(Query("t", "a", token: "garbage!")).AsTask());
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }
}
