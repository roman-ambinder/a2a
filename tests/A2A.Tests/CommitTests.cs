using A2A.Client;
using System.Security.Claims;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using static A2A.Tests.TaskStoreTests;

namespace A2A.Tests;

/// <summary>Counts commits and plain updates, so a test can see which path the handler took.</summary>
public sealed class CountingTaskStore : DelegatingTaskStore
{
    private int _commits;
    private int _updates;

    public int Commits => Volatile.Read(ref _commits);
    public int Updates => Volatile.Read(ref _updates);

    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _updates);
        return base.UpdateAsync(record, expectedVersion, ct);
    }

    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _commits);
        return base.CommitAsync(commit, ct);
    }
}

/// <summary>Its Nth commit of an existing task fails with a non-conflict error, like a database that went away mid-commit.</summary>
public sealed class FailingCommitStore(int failOn) : DelegatingTaskStore
{
    private int _commits;

    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default) =>
        !commit.IsCreate && Interlocked.Increment(ref _commits) == failOn
            ? throw new InvalidOperationException("the database went away")
            : base.CommitAsync(commit, ct);
}

public class CommitTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static readonly TaskKey Key = new("t", "alice", "id1");

    private static (InMemoryTaskStore Tasks, InMemoryEventStore Events) NewStores()
    {
        var events = new InMemoryEventStore(new StreamingOptions());
        return (new InMemoryTaskStore(events), events);
    }

    private static StreamResponse Status(TaskState state) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent { TaskId = Key.TaskId, ContextId = "ctx", Status = new AgentTaskStatus { State = state } });

    private static TaskCommit Create(TaskRecord record) => new(record, 0, StreamResponse.Of(record.Task), []);

    private static async Task<List<SequencedEvent>> ReadAllAsync(EventStore events, TaskKey key)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in events.ReadAfterAsync(key, 0)) list.Add(e);
        return list;
    }

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    [Fact]
    public async Task Commit_writes_task_and_event_together()
    {
        var (tasks, events) = NewStores();
        var created = await tasks.CommitAsync(Create(NewRecord(Key.Tenant, Key.Owner, Key.TaskId)));

        var result = await tasks.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Working), created.Record.Version, Status(TaskState.Working), []));

        Assert.Equal(2, result.Record.Version);
        Assert.Equal(2, result.Sequence);
        var stored = (await tasks.GetAsync(Key))!;
        Assert.Equal(2, stored.Version);
        Assert.Equal(TaskState.Working, stored.Task.Status.State);
        var log = await ReadAllAsync(events, Key);
        Assert.Equal([1L, 2L], log.Select(e => e.Sequence));
        Assert.IsType<TaskEvent>(log[0].Event);
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(log[1].Event).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Stale_commit_writes_nothing()
    {
        var (tasks, events) = NewStores();
        var created = await tasks.CommitAsync(Create(NewRecord(Key.Tenant, Key.Owner, Key.TaskId)));
        await tasks.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Working), 1, Status(TaskState.Working), []));

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            tasks.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Failed), 1, Status(TaskState.Failed), [])).AsTask());
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            tasks.CommitAsync(new TaskCommit(WithState(created.Record with { Owner = "bob" }, TaskState.Failed), 2, Status(TaskState.Failed), [])).AsTask());

        var stored = (await tasks.GetAsync(Key))!;
        Assert.Equal(2, stored.Version);
        Assert.Equal(TaskState.Working, stored.Task.Status.State);
        Assert.Equal(2, await events.LastSequenceAsync(Key));
        Assert.Equal(2, (await ReadAllAsync(events, Key)).Count);
        Assert.Equal(0, await events.LastSequenceAsync(Key with { Owner = "bob" }));
    }

    [Fact]
    public async Task Create_commit_assigns_sequence_1()
    {
        var (tasks, events) = NewStores();
        var input = NewRecord(Key.Tenant, Key.Owner, Key.TaskId);

        var created = await tasks.CommitAsync(Create(input));

        Assert.Equal(1, created.Sequence);
        Assert.Equal(1, created.Record.Version);
        Assert.Equal(1, (await tasks.GetAsync(Key))!.Version);
        await Assert.ThrowsAsync<DuplicateTaskException>(() => tasks.CommitAsync(Create(input)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.CommitAsync(Create(input with { Version = 2 })).AsTask());
        Assert.Equal([1L], (await ReadAllAsync(events, Key)).Select(e => e.Sequence));
    }

    /// <summary>
    /// The store fails in the middle of a commit: the request fails with a retryable Internal error, and the task and its
    /// event log still agree (no event without its task change, no task change without its event).
    /// </summary>
    [Fact]
    public async Task Failing_store_writes_nothing()
    {
        var store = new FailingCommitStore(failOn: 2); // WORKING commits, the first artifact fails
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("task") }, Call()));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        await host.WaitIdleAsync();

        var page = await store.ListAsync(new TaskQuery("", "alice", null, null, null, 10, null));
        var task = Assert.Single(page.Records);
        var log = await ReadAllAsync(store.Events, task.Key);
        Assert.Equal(Enumerable.Range(1, log.Count).Select(i => (long)i), log.Select(e => e.Sequence));
        Assert.Equal(task.Version, log[^1].Sequence); // one version per logged event
        Assert.Null(task.Task.Artifacts);
        Assert.DoesNotContain(log, e => e.Event is ArtifactUpdateEvent);
        Assert.Equal(task.Task.Status.State, Assert.IsType<StatusUpdateEvent>(log[^1].Event).StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Handler_uses_commit(string binding)
    {
        var store = new CountingTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddTaskStore(store));

        var task = (await host.Client(binding).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(5, store.Commits); // create, WORKING, two artifact chunks, COMPLETED
        Assert.Equal(0, store.Updates);
        Assert.Equal(5, await store.Events.LastSequenceAsync(new TaskKey("", "alice", task.Id)));
    }
}
