using A2A.Client.Outbox;
using A2A.Client.Postgres;
using A2A.Tests;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="ClientOutboxStore"/> must have.</summary>
public abstract class ClientOutboxContract
{
    /// <summary>A fresh, empty store on <paramref name="time"/>.</summary>
    protected abstract Task<ClientOutboxStore> CreateAsync(TimeProvider time);

    /// <summary>A second store object over the same storage (a restart, or another instance). In-memory: the same object.</summary>
    protected abstract ClientOutboxStore SecondInstance(ClientOutboxStore store, TimeProvider time);

    protected readonly FakeTimeProvider Time = new();
    protected static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    protected static ClientOutboxMessage Msg(string id, string peer = "peer", string? correlation = null) =>
        ClientOutboxMessage.Create(peer, new SendMessageRequest { Message = Message.FromUser("hello " + id) }, correlation, id);

    /// <summary>Enqueues, leases and returns the entry.</summary>
    private static async Task<ClientOutboxEntry> LeaseOneAsync(ClientOutboxStore store) => Assert.Single(await store.LeaseDueAsync(10, Lease));

    private static ClientOutboxOutcome Done(string taskId = "t1") =>
        ClientOutboxOutcome.Finished(new TaskResponse(ScriptedA2AClient.Make(taskId, TaskState.Completed)));

    [Fact]
    public async Task Enqueue_adds_a_queued_entry_due_now()
    {
        var store = await CreateAsync(Time);
        Assert.True(await store.EnqueueAsync(Msg("a", "billing", "order-1")));

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Queued, e.Status);
        Assert.Equal("billing", e.Peer);
        Assert.Equal("order-1", e.Correlation);
        Assert.Equal(0, e.Attempts);
        Assert.False(e.InDoubt);
        Assert.Equal(Time.GetUtcNow(), e.CreatedAt);
        Assert.Equal(Time.GetUtcNow(), e.DueAt);
        Assert.Null(e.CollectedAt);
        Assert.Null(e.LeaseToken);
        Assert.Null(e.GetResult());
        Assert.Equal("hello a", e.GetRequest().Message.GetText());
        Assert.True(e.GetRequest().Configuration!.ReturnImmediately);
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task Enqueue_of_an_existing_id_returns_false_and_keeps_the_old_entry()
    {
        var store = await CreateAsync(Time);
        Assert.True(await store.EnqueueAsync(Msg("a", "first")));

        Assert.False(await store.EnqueueAsync(Msg("a", "second")));

        Assert.Equal("first", (await store.GetAsync("a"))!.Peer);
    }

    [Fact]
    public async Task Lease_gives_due_entries_oldest_first_and_counts_the_attempt()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("b"));
        Time.Advance(TimeSpan.FromSeconds(1));
        await store.EnqueueAsync(Msg("a"));

        var leased = await store.LeaseDueAsync(10, Lease);

        Assert.Equal(["b", "a"], leased.Select(e => e.Id));
        Assert.All(leased, e => { Assert.Equal(1, e.Attempts); Assert.NotNull(e.LeaseToken); });
    }

    [Fact]
    public async Task Lease_respects_max_and_hides_leased_entries_until_the_lease_runs_out()
    {
        var store = await CreateAsync(Time);
        foreach (var id in new[] { "a", "b", "c" }) await store.EnqueueAsync(Msg(id));

        Assert.Equal(2, (await store.LeaseDueAsync(2, Lease)).Count);
        Assert.Equal(["c"], (await store.LeaseDueAsync(10, Lease)).Select(e => e.Id));
        Assert.Empty(await store.LeaseDueAsync(10, Lease));

        Time.Advance(Lease);
        Assert.Equal(3, (await store.LeaseDueAsync(10, Lease)).Count);
    }

    [Fact]
    public async Task Lease_returns_the_doubt_from_before_and_sets_it_for_queued_entries()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));

        var first = await LeaseOneAsync(store);
        Assert.False(first.InDoubt); // nothing was in doubt before this lease
        Assert.True((await store.GetAsync("a"))!.InDoubt); // but a send is under way now

        Time.Advance(Lease); // the holder crashed
        var second = await LeaseOneAsync(store);
        Assert.True(second.InDoubt);
        Assert.Equal(2, second.Attempts);
    }

    [Fact]
    public async Task Lease_skips_entries_that_are_not_due()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        Assert.True(await store.RescheduleAsync(await LeaseOneAsync(store), TimeSpan.FromSeconds(30), "HTTP 503", false));

        Assert.Empty(await store.LeaseDueAsync(10, Lease));
        Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(await store.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task Reschedule_sets_due_error_and_doubt_and_clears_the_lease()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var leased = await LeaseOneAsync(store);

        Assert.True(await store.RescheduleAsync(leased, TimeSpan.FromSeconds(10), "boom", true));

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(Time.GetUtcNow() + TimeSpan.FromSeconds(10), e.DueAt);
        Assert.Equal("boom", e.LastError);
        Assert.True(e.InDoubt);
        Assert.Equal(1, e.Attempts);

        Time.Advance(TimeSpan.FromSeconds(10)); // due again, and the lease is gone without waiting for it
        Assert.Single(await store.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task MarkTracking_records_the_peers_ids_and_resets_attempts_and_doubt()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var leased = await LeaseOneAsync(store);

        Assert.True(await store.MarkTrackingAsync(leased, "task-9", "ctx-9", TaskState.Working, TimeSpan.FromSeconds(5)));

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Tracking, e.Status);
        Assert.Equal("task-9", e.RemoteTaskId);
        Assert.Equal("ctx-9", e.RemoteContextId);
        Assert.Equal(TaskState.Working, e.RemoteState);
        Assert.Equal(0, e.Attempts);
        Assert.False(e.InDoubt);
        Assert.Equal(Time.GetUtcNow() + TimeSpan.FromSeconds(5), e.DueAt);
        Assert.Empty(await store.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task A_tracking_entry_is_leased_again_without_doubt()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        Assert.True(await store.MarkTrackingAsync(await LeaseOneAsync(store), "t", null, TaskState.Working, TimeSpan.Zero));

        var again = await LeaseOneAsync(store);

        Assert.Equal(ClientOutboxStatus.Tracking, again.Status);
        Assert.Equal(1, again.Attempts);
        Assert.False(again.InDoubt);
        Assert.False((await store.GetAsync("a"))!.InDoubt);
    }

    [Fact]
    public async Task Finish_stores_the_result_and_makes_it_harvestable()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a", correlation: "c1"));
        Assert.True(await store.FinishAsync(await LeaseOneAsync(store), Done("t7")));

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Finished, e.Status);
        Assert.Equal("t7", e.RemoteTaskId);
        Assert.Equal(TaskState.Completed, e.RemoteState);
        Assert.Equal("t7", Assert.IsType<TaskResponse>(e.GetResult()).Task.Id);
        Assert.Empty(await store.LeaseDueAsync(10, Lease)); // finished entries are not leased for work
    }

    [Fact]
    public async Task Finish_as_failed_keeps_the_remote_ids_the_entry_already_has()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        Assert.True(await store.MarkTrackingAsync(await LeaseOneAsync(store), "t1", "c1", TaskState.Working, TimeSpan.Zero));

        Assert.True(await store.FinishAsync(await LeaseOneAsync(store), ClientOutboxOutcome.Failed("gave up")));

        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Failed, e.Status);
        Assert.Equal("gave up", e.LastError);
        Assert.Equal("t1", e.RemoteTaskId);
        Assert.Equal("c1", e.RemoteContextId);
        Assert.Equal(TaskState.Working, e.RemoteState);
        Assert.Null(e.GetResult());
    }

    [Fact]
    public async Task Finish_writes_the_doubt_of_the_outcome()
    {
        var store = await CreateAsync(Time);
        foreach (var id in new[] { "a", "b", "c" }) await store.EnqueueAsync(Msg(id));
        var leased = await store.LeaseDueAsync(10, Lease); // leasing sets the doubt on all three
        Assert.True(await store.FinishAsync(leased[0], ClientOutboxOutcome.Failed("refused", inDoubt: false)));
        Assert.True(await store.FinishAsync(leased[1], ClientOutboxOutcome.Failed("unknown", inDoubt: true)));
        Assert.True(await store.FinishAsync(leased[2], Done()));

        Assert.False((await store.GetAsync(leased[0].Id))!.InDoubt);
        Assert.True((await store.GetAsync(leased[1].Id))!.InDoubt);
        Assert.False((await store.GetAsync(leased[2].Id))!.InDoubt);
    }

    [Fact]
    public async Task A_stale_lease_holder_writes_nothing()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var stale = await LeaseOneAsync(store);
        Time.Advance(Lease);
        var current = await LeaseOneAsync(store); // the lease ran out and someone else took the entry

        Assert.False(await store.RescheduleAsync(stale, TimeSpan.Zero, "x", false));
        Assert.False(await store.MarkTrackingAsync(stale, "t", null, TaskState.Working, TimeSpan.Zero));
        Assert.False(await store.FinishAsync(stale, Done()));
        Assert.False(await store.AcknowledgeAsync(stale));
        var e = (await store.GetAsync("a"))!;
        Assert.Equal(ClientOutboxStatus.Queued, e.Status);
        Assert.Null(e.LastError);

        Assert.True(await store.FinishAsync(current, Done())); // the current holder still can
    }

    [Fact]
    public async Task An_entry_without_a_lease_cannot_be_written()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var unleased = (await store.GetAsync("a"))!;

        Assert.False(await store.RescheduleAsync(unleased, TimeSpan.Zero, "x", false));
        Assert.False(await store.FinishAsync(unleased, Done()));
    }

    [Fact]
    public async Task Writes_after_the_entry_ended_change_nothing()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        var leased = await LeaseOneAsync(store);
        Assert.True(await store.FinishAsync(leased, Done()));

        Assert.False(await store.FinishAsync(leased, ClientOutboxOutcome.Failed("late")));
        Assert.Equal(ClientOutboxStatus.Finished, (await store.GetAsync("a"))!.Status);
    }

    [Fact]
    public async Task Results_are_leased_oldest_first_by_correlation_and_hidden_until_the_lease_runs_out()
    {
        var store = await CreateAsync(Time);
        foreach (var (id, c) in new[] { ("a", "x"), ("b", "y"), ("c", "x") })
        {
            await store.EnqueueAsync(Msg(id, correlation: c));
            Time.Advance(TimeSpan.FromSeconds(1));
        }
        await store.EnqueueAsync(Msg("open")); // still queued: never a result
        foreach (var e in await store.LeaseDueAsync(10, Lease))
            if (e.Id != "open") Assert.True(await store.FinishAsync(e, Done(e.Id)));
        Time.Advance(Lease); // the work leases are over anyway

        var forX = await store.LeaseResultsAsync("x", 10, Lease);
        Assert.Equal(["a", "c"], forX.Select(e => e.Id));
        Assert.All(forX, e => Assert.NotNull(e.LeaseToken));
        Assert.Empty(await store.LeaseResultsAsync("x", 10, Lease));
        Assert.Equal(["b"], (await store.LeaseResultsAsync(null, 10, Lease)).Select(e => e.Id));

        Time.Advance(Lease);
        Assert.Equal(["a", "b", "c"], (await store.LeaseResultsAsync(null, 10, Lease)).Select(e => e.Id));
    }

    [Fact]
    public async Task Failed_entries_are_harvested_too_and_max_is_respected()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await store.EnqueueAsync(Msg("b"));
        foreach (var e in await store.LeaseDueAsync(10, Lease)) await store.FinishAsync(e, ClientOutboxOutcome.Failed("no " + e.Id));

        var one = Assert.Single(await store.LeaseResultsAsync(null, 1, Lease));
        Assert.Equal(ClientOutboxStatus.Failed, one.Status);
        Assert.Equal("no " + one.Id, one.LastError);
    }

    [Fact]
    public async Task Acknowledge_collects_the_result_once_and_an_unacknowledged_result_comes_back()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await store.FinishAsync(await LeaseOneAsync(store), Done());

        var first = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease)); // the handler crashes before it acknowledges
        Time.Advance(Lease);
        var second = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease));
        Assert.False(await store.AcknowledgeAsync(first)); // the old holder is stale

        Assert.True(await store.AcknowledgeAsync(second));

        Assert.False(await store.AcknowledgeAsync(second));
        var e = (await store.GetAsync("a"))!;
        Assert.Equal(Time.GetUtcNow(), e.CollectedAt);
        Assert.Empty(await store.LeaseResultsAsync(null, 10, Lease));
        Time.Advance(Lease);
        Assert.Empty(await store.LeaseResultsAsync(null, 10, Lease));
    }

    [Fact]
    public async Task Results_survive_a_restart()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await store.FinishAsync(await LeaseOneAsync(store), Done("t5"));

        var restarted = SecondInstance(store, Time);

        var result = Assert.Single(await restarted.LeaseResultsAsync(null, 10, Lease));
        Assert.Equal("t5", Assert.IsType<TaskResponse>(result.GetResult()).Task.Id);
    }

    [Fact]
    public async Task Hasten_makes_the_tracking_entry_of_that_peers_task_due_now()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a", "peer-a"));
        await store.EnqueueAsync(Msg("b", "peer-b"));
        foreach (var e in await store.LeaseDueAsync(10, Lease))
            Assert.True(await store.MarkTrackingAsync(e, "same-task-id", null, TaskState.Working, TimeSpan.FromHours(1))); // task ids can collide across peers

        Assert.False(await store.HastenAsync("peer-c", "same-task-id"));
        Assert.False(await store.HastenAsync("peer-a", "other"));
        Assert.True(await store.HastenAsync("peer-a", "same-task-id"));

        Assert.Equal("a", Assert.Single(await store.LeaseDueAsync(10, Lease)).Id);
    }

    [Fact]
    public async Task Hasten_ignores_entries_that_are_not_tracking()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await store.FinishAsync(await LeaseOneAsync(store), Done("t1"));

        Assert.False(await store.HastenAsync("peer", "t1"));
    }

    [Fact]
    public async Task Purge_deletes_old_collected_entries_only_and_frees_their_ids()
    {
        var store = await CreateAsync(Time);
        foreach (var id in new[] { "old", "new", "open" }) await store.EnqueueAsync(Msg(id));
        foreach (var e in await store.LeaseDueAsync(10, Lease))
            if (e.Id != "open") await store.FinishAsync(e, Done(e.Id));
        var results = await store.LeaseResultsAsync(null, 10, Lease);
        Assert.True(await store.AcknowledgeAsync(results.Single(e => e.Id == "old")));
        Time.Advance(TimeSpan.FromDays(2));
        Assert.True(await store.AcknowledgeAsync(results.Single(e => e.Id == "new")));

        Assert.False(await store.EnqueueAsync(Msg("old"))); // still deduplicated
        Assert.Equal(1, await store.PurgeCollectedAsync(TimeSpan.FromDays(1), 10));

        Assert.Null(await store.GetAsync("old"));
        Assert.NotNull(await store.GetAsync("new"));
        Assert.NotNull(await store.GetAsync("open"));
        Assert.True(await store.EnqueueAsync(Msg("old")));
        Assert.Equal(0, await store.PurgeCollectedAsync(TimeSpan.FromDays(1), 10));
    }

    [Fact]
    public async Task Purge_respects_max()
    {
        var store = await CreateAsync(Time);
        foreach (var id in new[] { "a", "b", "c" }) await store.EnqueueAsync(Msg(id));
        foreach (var e in await store.LeaseDueAsync(10, Lease)) await store.FinishAsync(e, Done(e.Id));
        foreach (var e in await store.LeaseResultsAsync(null, 10, Lease)) await store.AcknowledgeAsync(e);
        Time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(2, await store.PurgeCollectedAsync(TimeSpan.Zero, 2));
        Assert.Equal(1, await store.PurgeCollectedAsync(TimeSpan.Zero, 2));
    }

    [Fact]
    public async Task Age_is_counted_from_enqueue()
    {
        var store = await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        Time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromHours(3), (await store.GetAsync("a"))!.Age);
        Assert.Equal(TimeSpan.FromHours(3), (await LeaseOneAsync(store)).Age);
    }

    [Fact]
    public async Task Two_store_objects_over_one_storage_never_lease_the_same_entry()
    {
        var a = await CreateAsync(Time);
        var b = SecondInstance(a, Time);
        for (var i = 0; i < 30; i++) await a.EnqueueAsync(Msg("m" + i));

        var taken = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? a : b).LeaseDueAsync(7, Lease).AsTask()));

        var ids = taken.SelectMany(t => t).Select(e => e.Id).ToList();
        Assert.Equal(30, ids.Count);
        Assert.Equal(30, ids.Distinct().Count());
    }
}

public sealed class InMemoryClientOutboxStoreTests : ClientOutboxContract
{
    protected override Task<ClientOutboxStore> CreateAsync(TimeProvider time) => Task.FromResult<ClientOutboxStore>(new InMemoryClientOutboxStore(time));

    protected override ClientOutboxStore SecondInstance(ClientOutboxStore store, TimeProvider time) => store;
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresClientOutboxStoreTests(PostgresFixture db) : ClientOutboxContract
{
    private PostgresClientOutboxOptions? _options;

    protected override async Task<ClientOutboxStore> CreateAsync(TimeProvider time)
    {
        _options = await db.NewClientOutboxSchemaAsync(time);
        return new PostgresClientOutboxStore(db.DataSource, _options, time);
    }

    protected override ClientOutboxStore SecondInstance(ClientOutboxStore store, TimeProvider time) => new PostgresClientOutboxStore(db.DataSource, _options, time);

    [Fact]
    public async Task Enqueue_in_the_apps_transaction_is_visible_after_commit()
    {
        var store = (PostgresClientOutboxStore)await CreateAsync(Time);
        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.EnqueueAsync(Msg("a"), conn, tx));
            Assert.Null(await store.GetAsync("a")); // not visible to others before the commit
            Assert.False(await store.EnqueueAsync(Msg("a"), conn, tx));
            await tx.CommitAsync();
        }

        Assert.NotNull(await store.GetAsync("a"));
    }

    [Fact]
    public async Task Enqueue_in_a_rolled_back_transaction_leaves_nothing()
    {
        var store = (PostgresClientOutboxStore)await CreateAsync(Time);
        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.EnqueueAsync(Msg("a"), conn, tx));
            await tx.RollbackAsync();
        }

        Assert.Null(await store.GetAsync("a"));
        Assert.Empty(await store.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task Acknowledge_in_the_apps_transaction_counts_only_after_commit()
    {
        var store = (PostgresClientOutboxStore)await CreateAsync(Time);
        await store.EnqueueAsync(Msg("a"));
        await store.FinishAsync(Assert.Single(await store.LeaseDueAsync(10, Lease)), ClientOutboxOutcome.Finished(new TaskResponse(ScriptedA2AClient.Make("t", TaskState.Completed))));
        var leased = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease));

        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.AcknowledgeAsync(leased, conn, tx));
            await tx.RollbackAsync(); // the app's own write failed
        }
        Assert.Null((await store.GetAsync("a"))!.CollectedAt);
        Time.Advance(Lease);
        var again = Assert.Single(await store.LeaseResultsAsync(null, 10, Lease)); // returned again

        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await store.AcknowledgeAsync(again, conn, tx));
            await tx.CommitAsync();
        }
        Assert.NotNull((await store.GetAsync("a"))!.CollectedAt);
    }

    [Fact]
    public async Task The_database_clock_is_used_by_default()
    {
        db.SkipIfUnavailable();
        var options = new PostgresClientOutboxOptions { Schema = "t_" + Guid.NewGuid().ToString("N") };
        await PostgresClientOutboxSchema.EnsureSchemaAsync(db.DataSource, options.Schema);
        var store = new PostgresClientOutboxStore(db.DataSource, options, new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        await store.EnqueueAsync(Msg("a"));

        Assert.True((await store.GetAsync("a"))!.CreatedAt > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)); // the fake clock is ignored
    }

    [Fact]
    public async Task Schema_script_is_idempotent_and_checks_the_schema_name()
    {
        db.SkipIfUnavailable();
        var schema = "t_" + Guid.NewGuid().ToString("N");
        await PostgresClientOutboxSchema.EnsureSchemaAsync(db.DataSource, schema);
        await PostgresClientOutboxSchema.EnsureSchemaAsync(db.DataSource, schema);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostgresClientOutboxSchema.EnsureSchemaAsync(db.DataSource, schema)));

        Assert.Throws<ArgumentException>(() => PostgresClientOutboxSchema.ScriptFor("a; DROP TABLE x"));
        Assert.Contains("\"public\".a2a_client_outbox", PostgresClientOutboxSchema.Script, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PostgresClientOutboxOptions { Schema = "" });
    }
}
