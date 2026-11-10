using A2A.Json;
using A2A.Server;
using A2A.Server.Postgres;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Tests;

namespace A2A.Stores.Tests;

/// <summary>
/// Behavior every <see cref="PushOutbox"/> must have, together with the <see cref="TaskStore"/> whose
/// <see cref="TaskStore.CommitAsync"/> writes its entries.
/// </summary>
public abstract class PushOutboxContract
{
    /// <summary>A fresh task store and the outbox its commits write to, on <paramref name="time"/>.</summary>
    protected async Task<(TaskStore Tasks, PushOutbox Outbox)> CreateAsync(TimeProvider time)
    {
        var (tasks, _, outbox) = await CreateWithEventsAsync(time);
        return (tasks, outbox);
    }

    /// <summary>A fresh task store with the event store and outbox its commits write to, on <paramref name="time"/>.</summary>
    protected abstract Task<(TaskStore Tasks, EventStore Events, PushOutbox Outbox)> CreateWithEventsAsync(TimeProvider time);

    /// <summary>A second outbox over the same storage (another instance). In-memory: the same object.</summary>
    protected abstract PushOutbox SecondInstance(PushOutbox outbox, TimeProvider time);

    private static readonly TaskKey Alice = new("t", "alice", "task1");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<TaskKey, TaskRecord> _records = new();

    private static PushDelivery NewDelivery(TaskKey task, string configId) => new(task, configId, 0, Guid.NewGuid().ToString("N"));

    /// <summary>Commits the next event of <paramref name="task"/> (the first creates it) with a delivery per config id.</summary>
    private async Task<List<PushDelivery>> CommitAsync(TaskStore tasks, TaskKey task, params string[] configIds)
    {
        var deliveries = configIds.Select(id => NewDelivery(task, id)).ToList();
        var result = await CommitWithAsync(tasks, task, deliveries);
        return deliveries.Select(d => d with { Sequence = result.Sequence }).ToList();
    }

    private async Task<CommitResult> CommitWithAsync(TaskStore tasks, TaskKey task, IReadOnlyList<PushDelivery> deliveries)
    {
        CommitResult result;
        if (!_records.TryGetValue(task, out var record))
        {
            var created = TaskStoreContract.NewRecord(task.Tenant, task.Owner, task.TaskId, _time.GetUtcNow());
            result = await tasks.CommitAsync(new TaskCommit(created, 0, StreamResponse.Of(created.Task.DeepCopy()), deliveries));
        }
        else
        {
            result = await tasks.CommitAsync(new TaskCommit(record, record.Version, TaskStoreContract.StatusEvent(task.TaskId, TaskState.Working), deliveries));
        }
        _records[task] = result.Record;
        return result;
    }

    [Fact]
    public async Task Commit_writes_deliveries_due_now_with_the_event_sequence()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice);
        var written = await CommitAsync(tasks, Alice, "c1");

        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(written[0], leased.Delivery);
        Assert.Equal(2, leased.Delivery.Sequence);
        Assert.Equal(0, leased.Attempt);
        Assert.Equal(_time.GetUtcNow(), leased.DueAt);
        Assert.Null(leased.LastError);
    }

    [Fact]
    public async Task Payload_is_the_committed_event_and_survives_dead_letter_and_redrive()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice);
        var d = (await CommitAsync(tasks, Alice, "c1"))[0];
        var expected = A2AJson.Serialize(TaskStoreContract.StatusEvent(Alice.TaskId, TaskState.Working));

        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(expected, Utf8(leased.Payload));
        Assert.True(await outbox.RescheduleAsync(leased, 1, _time.GetUtcNow(), "HTTP 500"));
        var again = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(expected, Utf8(again.Payload));

        Assert.True(await outbox.DeadLetterAsync(again, "HTTP 500"));
        Assert.Equal(expected, Utf8(Assert.Single(await outbox.ListDeadLettersAsync("t", 10)).Payload));
        Assert.True(await outbox.RedriveAsync(d));
        Assert.Equal(expected, Utf8(Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Payload));
    }

    private static string Utf8(ReadOnlyMemory<byte> bytes) => System.Text.Encoding.UTF8.GetString(bytes.Span);

    [Fact]
    public async Task Trace_parent_is_kept()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        const string parent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await CommitWithAsync(tasks, Alice, [NewDelivery(Alice, "c1") with { TraceParent = parent }]);

        Assert.Equal(parent, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery.TraceParent);
    }

    [Fact]
    public async Task Failed_commit_writes_no_delivery()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice);
        var stale = _records[Alice] with { Version = 7 };
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            tasks.CommitAsync(new TaskCommit(stale, 7, TaskStoreContract.StatusEvent(Alice.TaskId, TaskState.Working), [NewDelivery(Alice, "c1")])).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => // a delivery of another task
            tasks.CommitAsync(new TaskCommit(_records[Alice], 1, TaskStoreContract.StatusEvent(Alice.TaskId, TaskState.Working), [NewDelivery(Alice with { Owner = "bob" }, "c1")])).AsTask());

        Assert.Empty(await outbox.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task Leased_entry_is_hidden_until_the_lease_ends()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var d = (await CommitAsync(tasks, Alice, "c1"))[0];

        Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Empty(await outbox.LeaseDueAsync(10, Lease));
        _time.Advance(Lease - TimeSpan.FromSeconds(1));
        Assert.Empty(await outbox.LeaseDueAsync(10, Lease));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(d, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task Only_the_oldest_entry_per_task_and_config_is_leased()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var first = await CommitAsync(tasks, Alice, "a", "b");
        var second = await CommitAsync(tasks, Alice, "a", "b");
        var other = await CommitAsync(tasks, Alice with { TaskId = "task2" }, "a");

        var leased = await outbox.LeaseDueAsync(10, Lease);
        Assert.Equal(first.Concat(other).OrderBy(Id), leased.Select(e => e.Delivery).OrderBy(Id));

        // Completing "a" seq 1 releases "a" seq 2 only; "b" seq 2 still waits behind its leased seq 1.
        Assert.True(await outbox.CompleteAsync(Entry(leased, first[0])));
        Assert.Equal(second[0], Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task Lease_returns_at_most_max_entries()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice, "a", "b", "c");

        Assert.Equal(2, (await outbox.LeaseDueAsync(2, Lease)).Count);
        Assert.Single(await outbox.LeaseDueAsync(2, Lease));
        Assert.Empty(await outbox.LeaseDueAsync(2, Lease));
    }

    [Fact]
    public async Task Reschedule_sets_attempt_due_time_and_error()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var d = (await CommitAsync(tasks, Alice, "c1"))[0];
        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.NotNull(leased.LeaseToken);

        var due = _time.GetUtcNow() + TimeSpan.FromSeconds(5);
        Assert.True(await outbox.RescheduleAsync(leased, 1, due, "HTTP 500"));
        Assert.Empty(await outbox.LeaseDueAsync(10, Lease)); // not due yet; the lease was released, not extended
        _time.Advance(TimeSpan.FromSeconds(5));

        var entry = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(d, entry.Delivery);
        Assert.Equal(1, entry.Attempt);
        Assert.Equal(due, entry.DueAt);
        Assert.Equal("HTTP 500", entry.LastError);
    }

    [Fact]
    public async Task Dead_letter_moves_the_entry_and_releases_the_next()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var first = (await CommitAsync(tasks, Alice, "c1"))[0];
        var second = (await CommitAsync(tasks, Alice, "c1"))[0];
        Assert.True(await outbox.RescheduleAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), 1, _time.GetUtcNow(), "HTTP 500"));
        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await outbox.DeadLetterAsync(leased, "HTTP 404"));

        var dead = Assert.Single(await outbox.ListDeadLettersAsync("t", 10));
        Assert.Equal(first, dead.Delivery);
        Assert.Equal(2, dead.Attempt); // attempts made, the last one included
        Assert.Equal(_time.GetUtcNow(), dead.DueAt); // when it was dead-lettered
        Assert.Equal("HTTP 404", dead.LastError);
        Assert.Equal(second, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task Dead_letters_are_listed_per_tenant_oldest_first_up_to_max()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var a = (await CommitAsync(tasks, Alice, "c1"))[0];
        var b = (await CommitAsync(tasks, Alice with { TaskId = "task2", Owner = "bob" }, "c1"))[0];
        var other = (await CommitAsync(tasks, Alice with { Tenant = "u" }, "c1"))[0];
        var leased = await outbox.LeaseDueAsync(10, Lease);
        await outbox.DeadLetterAsync(Entry(leased, b), "e");
        _time.Advance(TimeSpan.FromSeconds(1));
        await outbox.DeadLetterAsync(Entry(leased, a), "e");
        await outbox.DeadLetterAsync(Entry(leased, other), "e");

        Assert.Equal([b, a], (await outbox.ListDeadLettersAsync("t", 10)).Select(e => e.Delivery));
        Assert.Equal([b], (await outbox.ListDeadLettersAsync("t", 1)).Select(e => e.Delivery));
        Assert.Equal([other], (await outbox.ListDeadLettersAsync("u", 10)).Select(e => e.Delivery));
        Assert.Empty(await outbox.ListDeadLettersAsync("nobody", 10));
    }

    [Fact]
    public async Task Redrive_returns_a_dead_letter_to_the_outbox()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var d = (await CommitAsync(tasks, Alice, "c1"))[0];
        Assert.True(await outbox.RescheduleAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), 4, _time.GetUtcNow(), "HTTP 500"));
        Assert.True(await outbox.DeadLetterAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), "HTTP 500"));
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(await outbox.RedriveAsync(d with { ConfigId = "nope" }));
        Assert.True(await outbox.RedriveAsync(d));
        Assert.False(await outbox.RedriveAsync(d));

        Assert.Empty(await outbox.ListDeadLettersAsync("t", 10));
        var entry = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(d, entry.Delivery);
        Assert.Equal(0, entry.Attempt);
        Assert.Equal(_time.GetUtcNow(), entry.DueAt);
    }

    [Fact]
    public async Task Delete_all_removes_entries_and_dead_letters_of_one_task_only()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var dead = (await CommitAsync(tasks, Alice, "a"))[0];
        await outbox.DeadLetterAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), "e");
        await CommitAsync(tasks, Alice, "b");
        var otherOwner = (await CommitAsync(tasks, Alice with { TaskId = "task2" }, "a"))[0];

        await outbox.DeleteAllAsync(Alice with { Owner = "bob" }); // another owner's key: nothing
        await outbox.DeleteAllAsync(Alice);

        Assert.Empty(await outbox.ListDeadLettersAsync("t", 10));
        Assert.Equal(otherOwner, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task Writes_for_unknown_or_unleased_entries_do_nothing()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var d = (await CommitAsync(tasks, Alice, "c1"))[0];
        var notLeased = new OutboxEntry(d, 0, _time.GetUtcNow(), null);
        Assert.False(await outbox.CompleteAsync(notLeased));
        Assert.False(await outbox.RescheduleAsync(notLeased, 1, _time.GetUtcNow() + TimeSpan.FromHours(1), "x"));
        Assert.False(await outbox.DeadLetterAsync(notLeased, "x"));

        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.False(await outbox.CompleteAsync(leased with { Delivery = d with { Sequence = 99 } }));
        Assert.False(await outbox.RescheduleAsync(leased with { Delivery = d with { Task = Alice with { Owner = "bob" } } }, 1, _time.GetUtcNow() + TimeSpan.FromHours(1), "x"));
        Assert.False(await outbox.DeadLetterAsync(leased with { Delivery = d with { ConfigId = "other" } }, "x"));
        Assert.False(await outbox.CompleteAsync(leased with { LeaseToken = Guid.NewGuid() }));

        Assert.Empty(await outbox.ListDeadLettersAsync("t", 10));
        _time.Advance(Lease);
        Assert.Equal(d, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task A_stale_holder_cannot_change_a_re_leased_entry()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var first = (await CommitAsync(tasks, Alice, "c1"))[0];
        var next = (await CommitAsync(tasks, Alice, "c1"))[0];
        var stale = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        _time.Advance(Lease); // the first holder's lease runs out mid-send
        var current = Assert.Single(await SecondInstance(outbox, _time).LeaseDueAsync(10, Lease));
        Assert.Equal(first, current.Delivery);
        Assert.NotEqual(stale.LeaseToken, current.LeaseToken);

        Assert.False(await outbox.CompleteAsync(stale));
        Assert.False(await outbox.RescheduleAsync(stale, 1, _time.GetUtcNow(), "stale"));
        Assert.False(await outbox.DeadLetterAsync(stale, "stale"));

        // seq 1 is still held by the current holder: seq 2 is not released, and the lease was not cleared.
        Assert.Empty(await outbox.LeaseDueAsync(10, Lease));
        Assert.Empty(await outbox.ListDeadLettersAsync("t", 10));
        Assert.True(await outbox.CompleteAsync(current));
        Assert.Equal(next, Assert.Single(await outbox.LeaseDueAsync(10, Lease)).Delivery);
    }

    [Fact]
    public async Task A_new_delivery_never_replaces_an_existing_entry()
    {
        var (tasks, events, outbox) = await CreateWithEventsAsync(_time);
        var first = (await CommitAsync(tasks, Alice, "c1"))[0];
        // The finished task's log expires, so the next event restarts at sequence 1: same key as the pending entry.
        await events.MarkTerminalAsync(Alice);
        _time.Advance(TimeSpan.FromDays(1));
        var second = (await CommitAsync(tasks, Alice, "c1"))[0];
        Assert.Equal(first.Sequence, second.Sequence);

        var entry = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(first.DeliveryId, entry.Delivery.DeliveryId);
        Assert.IsType<TaskEvent>(A2AJson.Deserialize<StreamResponse>(Utf8(entry.Payload))); // the first event's body, not the second's
    }

    [Fact]
    public async Task Redrive_on_a_key_collision_keeps_the_pending_entry_and_the_dead_letter()
    {
        var (tasks, events, outbox) = await CreateWithEventsAsync(_time);
        var first = (await CommitAsync(tasks, Alice, "c1"))[0];
        Assert.True(await outbox.DeadLetterAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), "HTTP 500"));
        // The finished task's log expires, so the next event restarts at sequence 1: the key of the dead letter.
        await events.MarkTerminalAsync(Alice);
        _time.Advance(TimeSpan.FromDays(1));
        var second = (await CommitAsync(tasks, Alice, "c1"))[0];
        Assert.Equal(first.Sequence, second.Sequence);
        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(second.DeliveryId, leased.Delivery.DeliveryId);

        Assert.False(await outbox.RedriveAsync(first));

        Assert.Equal(first.DeliveryId, Assert.Single(await outbox.ListDeadLettersAsync("t", 10)).Delivery.DeliveryId); // not deleted
        Assert.Empty(await outbox.LeaseDueAsync(10, Lease)); // the lease is untouched
        Assert.True(await outbox.CompleteAsync(leased)); // and so is its token
    }

    private static OutboxEntry Entry(IEnumerable<OutboxEntry> leased, PushDelivery d) => Assert.Single(leased, e => e.Delivery == d);

    [Fact]
    public async Task Two_leasing_instances_never_get_the_same_entry()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var all = new List<PushDelivery>();
        for (var i = 0; i < 20; i++) all.AddRange(await CommitAsync(tasks, Alice with { TaskId = $"task{i}" }, "a", "b"));
        for (var i = 0; i < 20; i++) await CommitAsync(tasks, Alice with { TaskId = $"task{i}" }, "a"); // queued behind seq 1
        var second = SecondInstance(outbox, _time);

        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => await (i % 2 == 0 ? outbox : second).LeaseDueAsync(7, Lease))));

        var leased = leases.SelectMany(l => l).Select(e => e.Delivery).ToList();
        Assert.Equal(leased.Count, leased.Distinct().Count());
        Assert.Equal(all.OrderBy(Id), leased.OrderBy(Id)); // every head once, nothing queued behind a head
    }

    private static readonly byte[] Snapshot = System.Text.Encoding.UTF8.GetBytes("{\"task\":{\"id\":\"task1\"}}");

    [Fact]
    public async Task Enqueue_queues_one_due_delivery_with_its_payload_and_is_idempotent_on_the_key()
    {
        var (_, outbox) = await CreateAsync(_time);
        var d = new PushDelivery(Alice, "c1", 4, "id-one");

        Assert.True(await outbox.EnqueueAsync(d, Snapshot));
        Assert.False(await outbox.EnqueueAsync(d with { DeliveryId = "id-two" }, new byte[] { 1 })); // same key: nothing changes

        var leased = Assert.Single(await outbox.LeaseDueAsync(10, Lease));
        Assert.Equal(d, leased.Delivery);
        Assert.Equal(0, leased.Attempt);
        Assert.Equal(_time.GetUtcNow(), leased.DueAt);
        Assert.Equal(Snapshot, leased.Payload.ToArray());
        Assert.True(await outbox.EnqueueAsync(d with { ConfigId = "c2" }, Snapshot)); // another config: a new entry
        Assert.True(await outbox.EnqueueAsync(d with { Sequence = 5 }, Snapshot)); // another sequence: a new entry
    }

    [Fact]
    public async Task Enqueue_does_nothing_when_a_dead_letter_has_the_key()
    {
        var (_, outbox) = await CreateAsync(_time);
        var d = new PushDelivery(Alice, "c1", 4, "id-one");
        Assert.True(await outbox.EnqueueAsync(d, Snapshot));
        Assert.True(await outbox.DeadLetterAsync(Assert.Single(await outbox.LeaseDueAsync(10, Lease)), "HTTP 500"));

        Assert.False(await outbox.EnqueueAsync(d, Snapshot));

        Assert.Empty(await outbox.LeaseDueAsync(10, Lease));
        Assert.Single(await outbox.ListDeadLettersAsync("t", 10));
    }

    [Fact]
    public async Task Concurrent_enqueues_of_one_key_queue_it_once_across_instances()
    {
        var (_, outbox) = await CreateAsync(_time);
        var second = SecondInstance(outbox, _time);
        var d = new PushDelivery(Alice, "c1", 4, "id-one");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => await (i % 2 == 0 ? outbox : second).EnqueueAsync(d, Snapshot))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await outbox.LeaseDueAsync(10, Lease));
    }

    [Fact]
    public async Task Age_counts_from_queueing_survives_retries_and_a_redrive_starts_over()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var committed = (await CommitAsync(tasks, Alice, "c1"))[0];
        var enqueued = new PushDelivery(Alice, "c2", 9, "id-two");
        Assert.True(await outbox.EnqueueAsync(enqueued, Snapshot));

        _time.Advance(TimeSpan.FromHours(2));
        var leased = await outbox.LeaseDueAsync(10, Lease);
        Assert.Equal(TimeSpan.FromHours(2), Entry(leased, committed).Age);
        Assert.Equal(TimeSpan.FromHours(2), Entry(leased, enqueued).Age);

        // A retry keeps the age; a re-driven dead letter starts again.
        Assert.True(await outbox.RescheduleAsync(Entry(leased, committed), 1, _time.GetUtcNow(), "HTTP 500"));
        Assert.True(await outbox.DeadLetterAsync(Entry(leased, enqueued), "HTTP 500"));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await outbox.RedriveAsync(enqueued));
        var again = await outbox.LeaseDueAsync(10, Lease);
        Assert.Equal(TimeSpan.FromHours(3), Entry(again, committed).Age);
        Assert.Equal(TimeSpan.Zero, Entry(again, enqueued).Age);
    }

    private static IPushOutboxStatistics Stats(PushOutbox outbox) => Assert.IsAssignableFrom<IPushOutboxStatistics>(outbox);

    /// <summary>Dead-letters every entry that is due now.</summary>
    private static async Task DeadLetterAllDueAsync(PushOutbox outbox)
    {
        foreach (var leased in await outbox.LeaseDueAsync(100, Lease)) Assert.True(await outbox.DeadLetterAsync(leased, "HTTP 404"));
    }

    [Fact]
    public async Task Statistics_of_an_empty_outbox_are_zero()
    {
        var (_, outbox) = await CreateAsync(_time);

        var stats = await Stats(outbox).GetStatisticsAsync(10);

        Assert.Equal(TimeSpan.Zero, stats.OldestDueAge);
        Assert.Equal(0, stats.DeadLetterCount);
    }

    [Fact]
    public async Task Statistics_age_is_how_long_the_oldest_due_entry_waited_and_leased_entries_count()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice, "c1");
        Assert.Equal(TimeSpan.Zero, (await Stats(outbox).GetStatisticsAsync(10)).OldestDueAge);

        _time.Advance(TimeSpan.FromMinutes(3));
        await CommitAsync(tasks, new TaskKey("t", "bob", "task2"), "c1");
        Assert.Equal(TimeSpan.FromMinutes(3), (await Stats(outbox).GetStatisticsAsync(10)).OldestDueAge);

        // A leased entry is still waiting.
        Assert.Equal(2, (await outbox.LeaseDueAsync(10, Lease)).Count);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(5), (await Stats(outbox).GetStatisticsAsync(10)).OldestDueAge);
    }

    [Fact]
    public async Task Statistics_age_ignores_a_retry_backoff_but_counts_the_entries_queued_behind_it()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice, "c1");
        await CommitAsync(tasks, Alice, "c1");
        _time.Advance(TimeSpan.FromMinutes(10));
        var first = Assert.Single(await outbox.LeaseDueAsync(10, Lease)); // only the lowest sequence is leased
        Assert.True(await outbox.RescheduleAsync(first, 1, _time.GetUtcNow() + TimeSpan.FromHours(1), "HTTP 500"));

        // The first entry is backing off, not due. The second was queued 10 minutes ago.
        Assert.Equal(TimeSpan.FromMinutes(10), (await Stats(outbox).GetStatisticsAsync(10)).OldestDueAge);

        var (tasks2, outbox2) = await CreateAsync(_time);
        await CommitAsync(tasks2, new TaskKey("t", "bob", "task9"), "c1");
        var only = Assert.Single(await outbox2.LeaseDueAsync(10, Lease));
        Assert.True(await outbox2.RescheduleAsync(only, 1, _time.GetUtcNow() + TimeSpan.FromHours(1), "HTTP 500"));
        Assert.Equal(TimeSpan.Zero, (await Stats(outbox2).GetStatisticsAsync(10)).OldestDueAge);
    }

    [Fact]
    public async Task Statistics_count_dead_letters_of_every_tenant_and_stop_at_the_limit()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice, "c1");
        await CommitAsync(tasks, new TaskKey("other", "bob", "task2"), "c1");
        await CommitAsync(tasks, new TaskKey("third", "carol", "task3"), "c1");
        await DeadLetterAllDueAsync(outbox);

        Assert.Equal(3, (await Stats(outbox).GetStatisticsAsync(10)).DeadLetterCount);
        Assert.Equal(3, (await Stats(outbox).GetStatisticsAsync(3)).DeadLetterCount);
        Assert.Equal(2, (await Stats(outbox).GetStatisticsAsync(2)).DeadLetterCount);
        Assert.Equal(TimeSpan.Zero, (await Stats(outbox).GetStatisticsAsync(2)).OldestDueAge); // dead letters are not due
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Statistics_with_a_limit_of_zero_or_less_count_no_dead_letters(int max)
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, Alice, "c1");
        await DeadLetterAllDueAsync(outbox);

        Assert.Equal(0, (await Stats(outbox).GetStatisticsAsync(max)).DeadLetterCount);
    }

    private static TaskKey OwnerTask(string owner, int n, string tenant = "t") => new(tenant, owner, $"{owner}-{n}"); // task ids are unique per tenant, so they carry the owner

    private static IEnumerable<string> Owners(IEnumerable<OutboxEntry> entries) => entries.Select(e => e.Delivery.Task.Owner);

    [Fact]
    public async Task Capped_lease_leaves_an_owner_that_is_at_the_cap_while_another_owner_still_leases()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        for (var i = 1; i <= 4; i++) await CommitAsync(tasks, OwnerTask("alice", i), "c");
        var first = await outbox.LeaseDueCappedAsync(2, Lease, 2);
        Assert.Equal(["alice", "alice"], Owners(first));
        await CommitAsync(tasks, OwnerTask("bob", 1), "c");

        var second = await outbox.LeaseDueCappedAsync(10, Lease, 2);

        Assert.Equal(["bob"], Owners(second));
    }

    [Fact]
    public async Task Capped_lease_never_gives_an_owner_more_than_the_cap_in_one_call()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        for (var i = 1; i <= 5; i++)
        {
            await CommitAsync(tasks, OwnerTask("alice", i), "c");
            await CommitAsync(tasks, OwnerTask("bob", i), "c");
        }

        var leased = (await outbox.LeaseDueCappedAsync(20, Lease, 2)).ToList();

        Assert.Equal(2, leased.Count(e => e.Delivery.Task.Owner == "alice"));
        Assert.Equal(2, leased.Count(e => e.Delivery.Task.Owner == "bob"));
        Assert.Equal(4, leased.Count);
    }

    [Fact]
    public async Task Completing_a_delivery_frees_one_slot_of_its_owner()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        for (var i = 1; i <= 4; i++) await CommitAsync(tasks, OwnerTask("alice", i), "c");
        var leased = await outbox.LeaseDueCappedAsync(10, Lease, 2);
        Assert.Equal(2, leased.Count);
        Assert.Empty(await outbox.LeaseDueCappedAsync(10, Lease, 2));

        Assert.True(await outbox.CompleteAsync(leased[0]));

        Assert.Single(await outbox.LeaseDueCappedAsync(10, Lease, 2));
        Assert.Empty(await outbox.LeaseDueCappedAsync(10, Lease, 2));
    }

    [Fact]
    public async Task An_expired_lease_no_longer_counts_toward_the_cap()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        var first = (await CommitAsync(tasks, OwnerTask("alice", 1), "c"))[0];
        _time.Advance(TimeSpan.FromSeconds(1));
        await CommitAsync(tasks, OwnerTask("alice", 2), "c");
        Assert.Equal(first, Assert.Single(await outbox.LeaseDueCappedAsync(10, Lease, 1)).Delivery);
        Assert.Empty(await outbox.LeaseDueCappedAsync(10, Lease, 1));

        _time.Advance(Lease);

        // The lease ended, so the owner has a free slot again and the oldest due entry is leased once more.
        Assert.Equal(first, Assert.Single(await outbox.LeaseDueCappedAsync(10, Lease, 1)).Delivery);
    }

    [Fact]
    public async Task The_cap_ignores_the_tenant()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        await CommitAsync(tasks, OwnerTask("alice", 1, "t1"), "c");
        await CommitAsync(tasks, OwnerTask("alice", 2, "t2"), "c");
        await CommitAsync(tasks, OwnerTask("alice", 3, "t1"), "c");
        await CommitAsync(tasks, OwnerTask("alice", 4, "t2"), "c");

        Assert.Equal(2, (await outbox.LeaseDueCappedAsync(10, Lease, 2)).Count);
        Assert.Empty(await outbox.LeaseDueCappedAsync(10, Lease, 2));
    }

    [Fact]
    public async Task A_saturated_owners_older_backlog_does_not_hide_another_owner()
    {
        var (tasks, outbox) = await CreateAsync(_time);
        for (var i = 1; i <= 20; i++) await CommitAsync(tasks, OwnerTask("alice", i), "c");
        Assert.Equal(["alice"], Owners(await outbox.LeaseDueCappedAsync(1, Lease, 1))); // alice is at the cap now
        _time.Advance(TimeSpan.FromSeconds(1));
        await CommitAsync(tasks, OwnerTask("bob", 1), "c");

        var leased = await outbox.LeaseDueCappedAsync(2, Lease, 1);

        Assert.Equal(["bob"], Owners(leased));
    }

    private static string Id(PushDelivery d) => $"{d.Task.Tenant}/{d.Task.Owner}/{d.Task.TaskId}/{d.ConfigId}/{d.Sequence:D5}";
}

public sealed class InMemoryPushOutboxTests : PushOutboxContract
{
    protected override Task<(TaskStore Tasks, EventStore Events, PushOutbox Outbox)> CreateWithEventsAsync(TimeProvider time)
    {
        var events = new InMemoryEventStore(new StreamingOptions(), time);
        var outbox = new InMemoryPushOutbox(time);
        return Task.FromResult<(TaskStore, EventStore, PushOutbox)>((new InMemoryTaskStore(events, outbox), events, outbox));
    }

    protected override PushOutbox SecondInstance(PushOutbox outbox, TimeProvider time) => outbox;
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresPushOutboxTests(PostgresFixture db) : PushOutboxContract
{
    private PostgresStoreOptions? _schema;

    protected override async Task<(TaskStore Tasks, EventStore Events, PushOutbox Outbox)> CreateWithEventsAsync(TimeProvider time)
    {
        _schema = await db.NewSchemaAsync(time);
        var streaming = new StreamingOptions();
        return (new PostgresTaskStore(db.DataSource, streaming, _schema, time), new PostgresEventStore(db.DataSource, streaming, _schema, time),
            new PostgresPushOutbox(db.DataSource, _schema, time));
    }

    protected override PushOutbox SecondInstance(PushOutbox outbox, TimeProvider time) => new PostgresPushOutbox(db.DataSource, _schema, time);
}
