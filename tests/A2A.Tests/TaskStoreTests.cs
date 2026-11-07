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
        var task = A2AJson.Clone(r.Task);
        task.Status.State = s;
        return r with { Task = task };
    }

    [Fact]
    public void Task_ids_are_22_char_base64url_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => TaskIds.New()).ToHashSet();
        Assert.Equal(1000, ids.Count);
        Assert.All(ids, i => Assert.Matches("^[A-Za-z0-9_-]{22}$", i));
    }
}
