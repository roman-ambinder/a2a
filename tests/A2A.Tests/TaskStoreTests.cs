using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>
/// A task store that forwards everything to an in-memory store. Tests derive from it and override the members they break.
/// Register it with <see cref="TestStoreExtensions.AddTaskStore"/>, so the server reads events from the store its commits write to.
/// </summary>
public abstract class DelegatingTaskStore : TaskStore
{
    protected DelegatingTaskStore() => Inner = new InMemoryTaskStore(Events);

    /// <summary>The event store the inner store commits to.</summary>
    public InMemoryEventStore Events { get; } = new(new StreamingOptions());

    protected InMemoryTaskStore Inner { get; }

    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => Inner.GetAsync(key, ct);
    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => Inner.CreateAsync(record, ct);
    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) => Inner.UpdateAsync(record, expectedVersion, ct);
    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default) => Inner.CommitAsync(commit, ct);
    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => Inner.ListAsync(query, ct);
    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) => Inner.GetContextAsync(tenant, contextId, ct);
    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) => Inner.GetOrCreateContextAsync(record, ct);
}

public static class TestStoreExtensions
{
    /// <summary>Uses <paramref name="store"/> as the task store and its <see cref="DelegatingTaskStore.Events"/> as the event store.</summary>
    public static IServiceCollection AddTaskStore(this IServiceCollection services, DelegatingTaskStore store) =>
        services.AddSingleton<TaskStore>(store).AddSingleton<EventStore>(store.Events);
}
