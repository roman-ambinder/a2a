using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;

namespace A2A.Tests;

/// <summary>
/// Helpers for tests that use a task store. The store behavior itself is tested once for every store (in-memory, PostgreSQL)
/// by the contract suite in tests/A2A.Stores.Tests.
/// </summary>
public class TaskStoreTests
{
    internal static TaskRecord NewRecord(string tenant, string owner, string id, DateTimeOffset? at = null, string contextId = "ctx")
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return new TaskRecord(tenant, owner,
            new AgentTask { Id = id, ContextId = contextId, Status = new AgentTaskStatus { State = TaskState.Submitted, Timestamp = now } },
            0, now, now);
    }

    internal static TaskRecord WithState(TaskRecord r, TaskState s)
    {
        var task = r.Task.CopyForUpdate(); // a new status: the stored one is never changed
        task.Status.State = s;
        return r with { Task = task };
    }

    // In-memory specific ownership checks (stores own the records they return; see TaskStoreOwnershipTests).
    [Fact]
    public async Task Update_leaves_an_earlier_returned_record_unchanged()
    {
        var s = new InMemoryTaskStore();
        var r = await s.CreateAsync(NewRecord("t", "a", "id1"));
        await s.UpdateAsync(WithState(r, TaskState.Failed), 1);
        Assert.Equal(TaskState.Submitted, r.Task.Status.State);
        Assert.Equal(TaskState.Failed, (await s.GetAsync(r.Key))!.Task.Status.State);
    }

    [Fact]
    public async Task Store_owns_and_returns_the_instance_it_was_given()
    {
        var s = new InMemoryTaskStore();
        var input = NewRecord("t", "a", "id1");
        var created = await s.CreateAsync(input);
        Assert.Same(input.Task, created.Task);
        Assert.Same(input.Task, (await s.GetAsync(input.Key))!.Task);
    }

    [Fact]
    public void Task_ids_are_22_char_base64url_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => TaskIds.New()).ToHashSet();
        Assert.Equal(1000, ids.Count);
        Assert.All(ids, i => Assert.Matches("^[A-Za-z0-9_-]{22}$", i));
    }
}
