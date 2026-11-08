using A2A.Server;
using A2A.Server.Postgres;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="EventStore"/> must have.</summary>
public abstract class EventStoreContract
{
    protected abstract Task<EventStore> CreateStoreAsync(StreamingOptions options, TimeProvider? time = null);

    protected static StreamResponse Evt(string taskId, TaskState state = TaskState.Working) => StreamResponse.Of(new AgentTask
    {
        Id = taskId, ContextId = "c", Status = new AgentTaskStatus { State = state },
    });

    private static async Task<List<SequencedEvent>> ReadAllAsync(EventStore store, TaskKey key, long after = 0)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in store.ReadAfterAsync(key, after)) list.Add(e);
        return list;
    }

    [SkippableFact]
    public async Task Sequences_start_at_1_and_increase_per_task()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        var a = new TaskKey("", "alice", "a");
        var b = new TaskKey("", "alice", "b");
        var seqA = new List<long>();
        var seqB = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            seqA.Add(await store.AppendAsync(a, Evt("a")));
            seqB.Add(await store.AppendAsync(b, Evt("b")));
        }
        Assert.Equal([1L, 2, 3], seqA);
        Assert.Equal([1L, 2, 3], seqB);
        Assert.Equal(3, await store.LastSequenceAsync(a));
        Assert.Equal(0, await store.LastSequenceAsync(new TaskKey("", "alice", "none")));
        Assert.Equal([2L, 3], (await ReadAllAsync(store, a, after: 1)).Select(e => e.Sequence));
    }

    [SkippableFact]
    public async Task Events_round_trip_in_order()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        var key = new TaskKey("t", "alice", "x");
        await store.AppendAsync(key, Evt("x", TaskState.Submitted));
        await store.AppendAsync(key, Evt("x", TaskState.Working));
        await store.AppendAsync(key, Evt("x", TaskState.Completed));

        var read = await ReadAllAsync(store, key);
        Assert.Equal([1L, 2, 3], read.Select(e => e.Sequence));
        Assert.Equal([TaskState.Submitted, TaskState.Working, TaskState.Completed],
            read.Select(e => ((TaskEvent)e.Event).Task.Status.State));
        Assert.Empty(await ReadAllAsync(store, key, after: 3));
    }

    [SkippableFact]
    public async Task Mutating_a_read_event_does_not_change_the_store()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        var key = new TaskKey("", "alice", "x");
        await store.AppendAsync(key, Evt("x"));
        ((TaskEvent)(await ReadAllAsync(store, key))[0].Event).Task.Status.State = TaskState.Failed;
        Assert.Equal(TaskState.Working, ((TaskEvent)(await ReadAllAsync(store, key))[0].Event).Task.Status.State);
    }

    [SkippableFact]
    public async Task Sequences_are_scoped_by_owner_and_tenant()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        await store.AppendAsync(new TaskKey("", "alice", "t"), Evt("t"));
        Assert.Equal(1, await store.AppendAsync(new TaskKey("", "bob", "t"), Evt("t")));
        Assert.Equal(1, await store.AppendAsync(new TaskKey("other", "alice", "t"), Evt("t")));
        Assert.Single(await ReadAllAsync(store, new TaskKey("", "bob", "t")));
        Assert.Null(await store.OldestRetainedAsync(new TaskKey("", "carol", "t")));
    }

    [SkippableFact]
    public async Task Buffer_trims_to_replay_size()
    {
        var store = await CreateStoreAsync(new StreamingOptions { ReplayBufferSize = 3 });
        var key = new TaskKey("", "alice", "t");
        Assert.Null(await store.OldestRetainedAsync(key));
        for (var i = 0; i < 5; i++) await store.AppendAsync(key, Evt("t"));
        Assert.Equal(3, await store.OldestRetainedAsync(key));
        Assert.Equal(5, await store.LastSequenceAsync(key));
        Assert.Equal([3L, 4, 5], (await ReadAllAsync(store, key)).Select(e => e.Sequence));
    }

    [SkippableFact]
    public async Task Terminal_buffer_evicted_after_retention()
    {
        var clock = new FakeTimeProvider();
        var store = await CreateStoreAsync(new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(10) }, clock);
        var key = new TaskKey("", "alice", "t");
        await store.AppendAsync(key, Evt("t"));
        await store.MarkTerminalAsync(key);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, await store.OldestRetainedAsync(key));
        Assert.Equal(1, await store.LastSequenceAsync(key));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.OldestRetainedAsync(key));
        Assert.Equal(0, await store.LastSequenceAsync(key));
        Assert.Empty(await ReadAllAsync(store, key));
    }

    [SkippableFact]
    public async Task Second_mark_terminal_does_not_extend_retention()
    {
        var clock = new FakeTimeProvider();
        var store = await CreateStoreAsync(new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(10) }, clock);
        var key = new TaskKey("", "alice", "t");
        await store.AppendAsync(key, Evt("t"));
        await store.MarkTerminalAsync(key);
        clock.Advance(TimeSpan.FromMinutes(8));
        await store.MarkTerminalAsync(key);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await store.OldestRetainedAsync(key));
    }

    [SkippableFact]
    public async Task Mark_terminal_without_events_is_a_no_op()
    {
        var clock = new FakeTimeProvider();
        var store = await CreateStoreAsync(new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(10) }, clock);
        var key = new TaskKey("", "alice", "t");
        await store.MarkTerminalAsync(key);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await store.AppendAsync(key, Evt("t")));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await store.OldestRetainedAsync(key)); // the early mark set no deadline
    }

    [SkippableFact]
    public async Task Log_starts_over_after_eviction()
    {
        var clock = new FakeTimeProvider();
        var store = await CreateStoreAsync(new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(1) }, clock);
        var key = new TaskKey("", "alice", "t");
        await store.AppendAsync(key, Evt("t"));
        await store.AppendAsync(key, Evt("t"));
        await store.MarkTerminalAsync(key);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await store.AppendAsync(key, Evt("t", TaskState.Completed)));
        var read = Assert.Single(await ReadAllAsync(store, key));
        Assert.Equal(TaskState.Completed, ((TaskEvent)read.Event).Task.Status.State);
        Assert.Equal(1, await store.OldestRetainedAsync(key));

        // The new log is not terminal: it stays.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await store.LastSequenceAsync(key));
    }

    [SkippableFact]
    public async Task Concurrent_appends_get_distinct_contiguous_sequences()
    {
        var store = await CreateStoreAsync(new StreamingOptions { ReplayBufferSize = 1000 });
        var key = new TaskKey("", "alice", "t");
        var seqs = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => store.AppendAsync(key, Evt("t")).AsTask())));
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), seqs.Order());
        Assert.Equal(40, (await ReadAllAsync(store, key)).Count);
    }

    [SkippableFact]
    public async Task Delete_drops_the_log_and_restarts_sequences()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        var key = new TaskKey("t", "alice", "x");
        var other = new TaskKey("t", "alice", "y");
        await store.AppendAsync(key, Evt("x"));
        await store.AppendAsync(key, Evt("x"));
        await store.AppendAsync(other, Evt("y"));

        await store.DeleteAsync(key);
        Assert.Equal(0, await store.LastSequenceAsync(key));
        Assert.Null(await store.OldestRetainedAsync(key));
        Assert.Empty(await ReadAllAsync(store, key));
        Assert.Equal(1, await store.LastSequenceAsync(other));
        Assert.Equal(1, await store.AppendAsync(key, Evt("x")));
        await store.DeleteAsync(new TaskKey("t", "alice", "never-existed")); // nothing to delete is fine
    }

    [SkippableFact]
    public async Task Delete_with_another_owner_or_tenant_deletes_nothing()
    {
        var store = await CreateStoreAsync(new StreamingOptions());
        var key = new TaskKey("t", "alice", "x");
        await store.AppendAsync(key, Evt("x"));
        await store.DeleteAsync(key with { Owner = "bob" });
        await store.DeleteAsync(key with { Tenant = "u" });
        Assert.Equal(1, await store.LastSequenceAsync(key));
        Assert.Single(await ReadAllAsync(store, key));
    }
}

public sealed class InMemoryEventStoreTests : EventStoreContract
{
    protected override Task<EventStore> CreateStoreAsync(StreamingOptions options, TimeProvider? time = null) =>
        Task.FromResult<EventStore>(new InMemoryEventStore(options, time));
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresEventStoreTests(PostgresFixture db) : EventStoreContract
{
    protected override async Task<EventStore> CreateStoreAsync(StreamingOptions options, TimeProvider? time = null) =>
        new PostgresEventStore(db.DataSource, options, await db.NewSchemaAsync(), time);

    [SkippableFact]
    public async Task Maintenance_deletes_expired_logs_and_keeps_live_ones()
    {
        var schema = await db.NewSchemaAsync();
        var clock = new FakeTimeProvider();
        var store = new PostgresEventStore(db.DataSource, new StreamingOptions { TerminalRetention = TimeSpan.FromMinutes(1) }, schema, clock);
        var done = new TaskKey("", "alice", "done");
        var live = new TaskKey("", "alice", "live");
        await store.AppendAsync(done, Evt("done"));
        await store.AppendAsync(live, Evt("live"));
        await store.MarkTerminalAsync(done);
        clock.Advance(TimeSpan.FromMinutes(2));

        var (logs, _) = await new PostgresMaintenance(db.DataSource, schema, clock).PurgeExpiredAsync();
        Assert.Equal(1, logs);
        Assert.Equal(1, await store.LastSequenceAsync(live));

        await using var cmd = db.DataSource.CreateCommand($"SELECT count(*) FROM \"{schema.Schema}\".a2a_events");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }
}
