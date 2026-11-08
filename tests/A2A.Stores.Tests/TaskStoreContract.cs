using A2A.Json;
using A2A.Server;
using A2A.Server.Postgres;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="TaskStore"/> must have. Each store runs the same tests through a subclass.</summary>
public abstract class TaskStoreContract
{
    /// <summary>A fresh task store and the event store its commits write to.</summary>
    protected abstract Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null);

    protected async Task<TaskStore> CreateStoreAsync(TimeProvider? time = null) => (await CreateStoresAsync(time)).Tasks;

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
    public async Task Status_message_metadata_survives_a_round_trip()
    {
        var s = await CreateStoreAsync();
        var record = NewRecord("t", "alice", "approval");
        var task = record.Task.DeepCopy();
        using var hash = System.Text.Json.JsonDocument.Parse("\"abc123\"");
        task.Status.State = TaskState.InputRequired;
        task.Status.Message = new Message
        {
            MessageId = "m1", Role = Role.Agent, Parts = [Part.FromText("ok?")],
            Metadata = new() { [A2AProtocol.ApprovalHashKey] = hash.RootElement.Clone() },
        };
        task.Metadata = new() { [A2AProtocol.ApprovalConsumedKey] = hash.RootElement.Clone() };
        var created = await s.CreateAsync(record with { Task = task });

        var loaded = (await s.GetAsync(created.Key))!.Task;

        Assert.Equal("abc123", loaded.Status.Message!.Metadata![A2AProtocol.ApprovalHashKey].GetString());
        Assert.Equal("abc123", loaded.Metadata![A2AProtocol.ApprovalConsumedKey].GetString());
    }

    [SkippableFact]
    public async Task Stamped_history_messages_and_merged_task_metadata_survive_a_round_trip()
    {
        var s = await CreateStoreAsync();
        var record = NewRecord("t", "alice", "stamped");
        var task = record.Task.DeepCopy();
        const string at = "2026-11-08T10:11:12.345Z";
        task.History =
        [
            new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("hi")],
                Metadata = new() { [A2AProtocol.MessageRecordedAtKey] = System.Text.Json.JsonSerializer.SerializeToElement(at) } },
            new Message { MessageId = "old", Role = Role.Agent, Parts = [Part.FromText("before the stamp existed")] }, // no key
        ];
        task.Metadata = new() { ["progress"] = System.Text.Json.JsonSerializer.SerializeToElement(100), ["cleared"] = System.Text.Json.JsonSerializer.SerializeToElement<string?>(null) };
        var created = await s.CreateAsync(record with { Task = task });

        var loaded = (await s.GetAsync(created.Key))!.Task;

        Assert.Equal(DateTimeOffset.Parse(at), loaded.History![0].GetRecordedAt());
        Assert.Null(loaded.History[1].GetRecordedAt());
        Assert.Null(loaded.History[1].Metadata);
        Assert.Equal(100, loaded.Metadata!["progress"].GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, loaded.Metadata["cleared"].ValueKind);
    }

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

    internal static StreamResponse StatusEvent(string taskId, TaskState state, long? v = null) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent
        {
            TaskId = taskId,
            ContextId = "ctx",
            Status = new AgentTaskStatus { State = state },
            Metadata = v is { } n ? new() { ["v"] = A2AJson.SerializeToElement(n.ToString(System.Globalization.CultureInfo.InvariantCulture)) } : null,
        });

    internal static TaskCommit CreateCommit(TaskRecord record) => new(record, 0, StreamResponse.Of(record.Task), []);

    internal static async Task<List<SequencedEvent>> ReadAllAsync(EventStore events, TaskKey key)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in events.ReadAfterAsync(key, 0)) list.Add(e);
        return list;
    }

    [SkippableFact]
    public async Task Commit_stores_task_and_event_with_the_next_sequence()
    {
        var (s, events) = await CreateStoresAsync();
        var created = await s.CommitAsync(CreateCommit(NewRecord("t", "alice", "id1")));
        Assert.Equal(1, created.Sequence);
        Assert.Equal(1, created.Record.Version);

        var next = await s.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Working), 1, StatusEvent("id1", TaskState.Working), []));

        Assert.Equal(2, next.Sequence);
        Assert.Equal(2, next.Record.Version);
        Assert.Equal(created.Record.CreatedAt, next.Record.CreatedAt);
        var stored = (await s.GetAsync(created.Record.Key))!;
        Assert.Equal(2, stored.Version);
        Assert.Equal(TaskState.Working, stored.Task.Status.State);
        var log = await ReadAllAsync(events, created.Record.Key);
        Assert.Equal([1L, 2L], log.Select(e => e.Sequence));
        Assert.Equal("id1", Assert.IsType<TaskEvent>(log[0].Event).Task.Id);
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(log[1].Event).StatusUpdate.Status.State);
    }

    [SkippableFact]
    public async Task Stale_foreign_or_duplicate_commit_writes_nothing()
    {
        var (s, events) = await CreateStoresAsync();
        var input = NewRecord("t", "alice", "id1");
        var created = await s.CommitAsync(CreateCommit(input));
        await s.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Working), 1, StatusEvent("id1", TaskState.Working), []));

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            s.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Failed), 1, StatusEvent("id1", TaskState.Failed), [])).AsTask());
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            s.CommitAsync(new TaskCommit(WithState(created.Record with { Owner = "bob" }, TaskState.Failed), 2, StatusEvent("id1", TaskState.Failed), [])).AsTask());
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            s.CommitAsync(new TaskCommit(NewRecord("t", "alice", "ghost"), 1, StatusEvent("ghost", TaskState.Failed), [])).AsTask());
        await Assert.ThrowsAsync<DuplicateTaskException>(() => s.CommitAsync(CreateCommit(input)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => s.CommitAsync(CreateCommit(input with { Version = 2 })).AsTask());

        var stored = (await s.GetAsync(created.Record.Key))!;
        Assert.Equal(2, stored.Version);
        Assert.Equal(TaskState.Working, stored.Task.Status.State);
        Assert.Equal([1L, 2L], (await ReadAllAsync(events, created.Record.Key)).Select(e => e.Sequence));
        Assert.Equal(0, await events.LastSequenceAsync(new TaskKey("t", "bob", "id1")));
        Assert.Equal(0, await events.LastSequenceAsync(new TaskKey("t", "alice", "ghost")));
    }

    /// <summary>
    /// Writers on several threads commit to one task, each reloading and retrying after a conflict. Every event carries
    /// the version its commit produced, so the log must list versions 1, 2, 3, ... in sequence order, with no gaps.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_commits_log_order_equals_version_order()
    {
        const int writers = 20;
        var (s, events) = await CreateStoresAsync();
        var key = (await s.CommitAsync(CreateCommit(NewRecord("t", "a", "id1")))).Record.Key;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            for (var attempt = 0; ; attempt++)
            {
                var current = (await s.GetAsync(key))!;
                var produced = current.Version + 1;
                try
                {
                    await s.CommitAsync(new TaskCommit(WithState(current, TaskState.Working), current.Version, StatusEvent("id1", TaskState.Working, produced), []));
                    return;
                }
                catch (ConcurrencyException) when (attempt < 1000)
                {
                    // Reload and retry, like the request handler.
                }
            }
        })).ToArray();
        go.SetResult();
        await Task.WhenAll(tasks);

        var log = await ReadAllAsync(events, key);
        Assert.Equal(Enumerable.Range(1, writers + 1).Select(i => (long)i), log.Select(e => e.Sequence));
        foreach (var e in log.Skip(1))
            Assert.Equal(e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Assert.IsType<StatusUpdateEvent>(e.Event).StatusUpdate.Metadata!["v"].GetString());
        Assert.Equal(writers + 1, (await s.GetAsync(key))!.Version);
    }

    // ---- retention members

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task Delete_removes_only_the_task_and_only_for_its_owner()
    {
        var s = await CreateStoreAsync();
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "ctx", T0));
        var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
        Assert.False(await s.DeleteAsync(r.Key with { Owner = "bob" }));
        Assert.False(await s.DeleteAsync(r.Key with { Tenant = "u" }));
        Assert.NotNull(await s.GetAsync(r.Key));

        Assert.True(await s.DeleteAsync(r.Key));
        Assert.Null(await s.GetAsync(r.Key));
        Assert.False(await s.DeleteAsync(r.Key));
        Assert.NotNull(await s.GetContextAsync("t", "ctx")); // the context is deleted by DeleteContextAsync, not with the task
    }

    [SkippableFact]
    public async Task Delete_does_not_touch_the_event_log()
    {
        var (s, events) = await CreateStoresAsync();
        var r = NewRecord("t", "alice", "id1");
        await s.CommitAsync(new TaskCommit(r, 0, StreamResponse.Of(r.Task), []));
        Assert.True(await s.DeleteAsync(r.Key));
        Assert.Equal(1, await events.LastSequenceAsync(r.Key));
    }

    [SkippableFact]
    public async Task ListExpired_returns_terminal_tasks_older_than_the_cutoff_oldest_first_capped()
    {
        var s = await CreateStoreAsync();
        async Task Add(string id, TaskState state, int days, string tenant = "t", string owner = "alice") =>
            await s.CreateAsync(WithState(NewRecord(tenant, owner, id, T0.AddDays(days)), state));
        await Add("done-3", TaskState.Completed, 3);
        await Add("done-1", TaskState.Completed, 1);
        await Add("failed-2", TaskState.Failed, 2, tenant: "u", owner: "bob"); // other tenant and owner: still listed
        await Add("canceled-4", TaskState.Canceled, 4);
        await Add("rejected-5", TaskState.Rejected, 5);
        await Add("done-new", TaskState.Completed, 30); // too new
        await Add("working", TaskState.Working, 0);
        await Add("input", TaskState.InputRequired, 0);
        await Add("auth", TaskState.AuthRequired, 0);
        await Add("submitted", TaskState.Submitted, 0);

        var all = await s.ListExpiredAsync(T0.AddDays(10), 100);
        Assert.Equal(
            [new TaskKey("t", "alice", "done-1"), new TaskKey("u", "bob", "failed-2"), new TaskKey("t", "alice", "done-3"),
             new TaskKey("t", "alice", "canceled-4"), new TaskKey("t", "alice", "rejected-5")],
            all);
        Assert.Equal(all.Take(2), await s.ListExpiredAsync(T0.AddDays(10), 2));
        Assert.Empty(await s.ListExpiredAsync(T0.AddDays(1), 100)); // strictly earlier than the cutoff
    }

    [SkippableFact]
    public async Task ListInterruptedBefore_returns_only_waiting_tasks_older_than_the_cutoff()
    {
        var s = await CreateStoreAsync();
        await s.CreateAsync(WithState(NewRecord("t", "alice", "input-old", T0.AddDays(2)), TaskState.InputRequired));
        await s.CreateAsync(WithState(NewRecord("u", "bob", "auth-old", T0.AddDays(1)), TaskState.AuthRequired));
        await s.CreateAsync(WithState(NewRecord("t", "alice", "input-new", T0.AddDays(30)), TaskState.InputRequired));
        await s.CreateAsync(WithState(NewRecord("t", "alice", "done-old", T0), TaskState.Completed));
        await s.CreateAsync(WithState(NewRecord("t", "alice", "working-old", T0), TaskState.Working));

        var list = await s.ListInterruptedBeforeAsync(T0.AddDays(10), 100);
        Assert.Equal([new TaskKey("u", "bob", "auth-old"), new TaskKey("t", "alice", "input-old")], list);
        Assert.Equal([new TaskKey("u", "bob", "auth-old")], await s.ListInterruptedBeforeAsync(T0.AddDays(10), 1));
    }

    [SkippableFact]
    public async Task Orphan_contexts_have_no_tasks_and_are_older_than_the_cutoff()
    {
        var s = await CreateStoreAsync();
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "orphan-old", T0.AddDays(2)));
        await s.GetOrCreateContextAsync(new ContextRecord("u", "bob", "orphan-older", T0.AddDays(1)));
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "orphan-new", T0.AddDays(30)));
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "used", T0));
        await s.CreateAsync(NewRecord("t", "alice", "id1", T0, contextId: "used"));

        var list = await s.ListOrphanContextsAsync(T0.AddDays(10), 100);
        Assert.Equal(["orphan-older", "orphan-old"], list.Select(c => c.ContextId));
        Assert.Equal(new ContextRecord("u", "bob", "orphan-older", T0.AddDays(1)), list[0]);
        Assert.Single(await s.ListOrphanContextsAsync(T0.AddDays(10), 1));

        // Once its last task is gone the context is an orphan too.
        await s.DeleteAsync(new TaskKey("t", "alice", "id1"));
        Assert.Contains("used", (await s.ListOrphanContextsAsync(T0.AddDays(10), 100)).Select(c => c.ContextId));
    }

    [SkippableFact]
    public async Task DeleteContext_keeps_a_context_that_a_task_still_uses()
    {
        var s = await CreateStoreAsync();
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "used", T0));
        await s.CreateAsync(NewRecord("t", "alice", "id1", T0, contextId: "used"));
        Assert.False(await s.DeleteContextAsync("t", "used"));
        Assert.NotNull(await s.GetContextAsync("t", "used"));

        await s.DeleteAsync(new TaskKey("t", "alice", "id1"));
        Assert.True(await s.DeleteContextAsync("t", "used"));
        Assert.Null(await s.GetContextAsync("t", "used"));
    }

    [SkippableFact]
    public async Task DeleteContext_removes_it_once_in_its_tenant_only()
    {
        var s = await CreateStoreAsync();
        await s.GetOrCreateContextAsync(new ContextRecord("t", "alice", "c1", T0));
        Assert.False(await s.DeleteContextAsync("u", "c1"));
        Assert.NotNull(await s.GetContextAsync("t", "c1"));
        Assert.True(await s.DeleteContextAsync("t", "c1"));
        Assert.Null(await s.GetContextAsync("t", "c1"));
        Assert.False(await s.DeleteContextAsync("t", "c1"));
    }
}

public sealed class InMemoryTaskStoreTests : TaskStoreContract
{
    protected override Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null)
    {
        var events = new InMemoryEventStore(new StreamingOptions(), time);
        return Task.FromResult<(TaskStore, EventStore)>((new InMemoryTaskStore(events), events));
    }
}

[Collection(ContainersCollection.Name)]
public sealed class PostgresTaskStoreTests(PostgresFixture db) : TaskStoreContract
{
    protected override async Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null)
    {
        var schema = await db.NewSchemaAsync(time);
        return Create(schema, time);
    }

    private (TaskStore Tasks, EventStore Events) Create(PostgresStoreOptions schema, TimeProvider? time = null)
    {
        var streaming = new StreamingOptions();
        return (new PostgresTaskStore(db.DataSource, streaming, schema, time), new PostgresEventStore(db.DataSource, streaming, schema, time));
    }

    /// <summary>
    /// The event insert fails after the task row was updated in the same transaction (a row already holds the next
    /// sequence): the whole commit rolls back, so the task keeps its version and the log its last sequence.
    /// </summary>
    [SkippableFact]
    public async Task Commit_is_atomic()
    {
        var schema = await db.NewSchemaAsync();
        var (s, events) = Create(schema);
        var created = await s.CommitAsync(CreateCommit(NewRecord("t", "a", "id1")));
        await using (var plant = db.DataSource.CreateCommand(
            $"INSERT INTO \"{schema.Schema}\".a2a_events (tenant, owner, task_id, seq, event) VALUES ('t', 'a', 'id1', 2, '{{}}')"))
            await plant.ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            s.CommitAsync(new TaskCommit(WithState(created.Record, TaskState.Working), 1, StatusEvent("id1", TaskState.Working), [])).AsTask());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        var stored = (await s.GetAsync(created.Record.Key))!;
        Assert.Equal(1, stored.Version);
        Assert.Equal(TaskState.Submitted, stored.Task.Status.State);
        Assert.Equal(1, await events.LastSequenceAsync(created.Record.Key));
    }
}
