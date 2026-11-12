using System.Reflection;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace A2A.Stores.Tests;

/// <summary>The whole task store contract, with the artifact parts in rows (PostgresStoreOptions.ArtifactRows).</summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresArtifactRowsTaskStoreTests(PostgresFixture db) : TaskStoreContract
{
    protected override async Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null)
    {
        var schema = await db.NewSchemaAsync(time);
        schema.ArtifactRows = true;
        var streaming = new StreamingOptions();
        return (new PostgresTaskStore(db.DataSource, streaming, schema, time), new PostgresEventStore(db.DataSource, streaming, schema, time));
    }
}

/// <summary>
/// PostgresStoreOptions.ArtifactRows (ADR-003, docs/superpowers/specs/2026-11-12-postgres-artifact-rows-design.md): an
/// append writes one row with its new parts, and a task reads back exactly as it was committed.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresArtifactRowsTests(PostgresFixture db)
{
    private static readonly TaskKey Key = new("t", "alice", "id1");

    private async Task<(PostgresTaskStore Store, PostgresStoreOptions Schema)> CreateAsync(Action<PostgresStoreOptions>? configure = null)
    {
        var schema = await db.NewSchemaAsync();
        schema.ArtifactRows = true;
        configure?.Invoke(schema);
        return (new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema), schema);
    }

    private static PostgresTaskStore Reopen(NpgsqlDataSourceHolder holder, PostgresStoreOptions schema, bool rows) =>
        new(holder.DataSource, new StreamingOptions(), new PostgresStoreOptions
        {
            Schema = schema.Schema, UseDatabaseClock = schema.UseDatabaseClock, ArtifactRows = rows,
            ArtifactRowsCompactAt = schema.ArtifactRowsCompactAt,
        });

    private sealed record NpgsqlDataSourceHolder(Npgsql.NpgsqlDataSource DataSource);

    private static string Canonical(AgentTask task) =>
        System.Text.Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(System.Text.Json.Nodes.JsonNode.Parse(A2AJson.Serialize(task))!));

    /// <summary>Commits <paramref name="evt"/> with <paramref name="next"/> and checks the read-back task equals it.</summary>
    private static async Task<TaskRecord> CommitAsync(TaskStore store, TaskRecord current, AgentTask next, StreamResponse evt)
    {
        var committed = await store.CommitAsync(new TaskCommit(current with { Task = next }, current.Version, evt, []));
        var read = await store.GetAsync(Key);
        Assert.Equal(Canonical(next), Canonical(read!.Task));
        Assert.Equal(Canonical(next), Canonical(committed.Record.Task));
        return committed.Record;
    }

    private static (AgentTask Next, StreamResponse Event) Append(TaskRecord current, string artifactId, params string[] texts)
    {
        var parts = texts.Select(t => (Part)Part.FromText(t)).ToList();
        var next = current.Task.CopyForUpdate();
        var index = next.Artifacts?.FindIndex(a => a.ArtifactId == artifactId) ?? -1;
        if (index < 0) (next.Artifacts ??= []).Add(new Artifact { ArtifactId = artifactId, Name = "out", Parts = parts });
        else next.Artifacts![index] = next.Artifacts[index].CopyWithParts(parts);
        var evt = StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = Key.TaskId, ContextId = "ctx", Append = index >= 0,
            Artifact = new Artifact { ArtifactId = artifactId, Parts = parts },
        });
        return (next, evt);
    }

    private async Task<long> RowsAsync(PostgresStoreOptions schema, string? artifactId = null)
    {
        await using var cmd = db.DataSource.CreateCommand(artifactId is null
            ? $"SELECT count(*) FROM \"{schema.Schema}\".a2a_artifact_parts WHERE task_id = 'id1'"
            : $"SELECT count(*) FROM \"{schema.Schema}\".a2a_artifact_parts WHERE task_id = 'id1' AND artifact_id = '{artifactId}'");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<short> LayoutAsync(PostgresStoreOptions schema)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT parts_layout FROM \"{schema.Schema}\".a2a_tasks WHERE task_id = 'id1'");
        return (short)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<TaskRecord> CreateTaskAsync(TaskStore store) =>
        (await store.CommitAsync(TaskStoreContract.CreateCommit(TaskStoreContract.NewRecord("t", "alice", "id1")))).Record;

    [Fact]
    public async Task Each_append_adds_one_row_and_the_task_reads_back_as_committed()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);

        var (next, evt) = Append(record, "a", "p0");
        record = await CommitAsync(store, record, next, evt);
        for (var i = 1; i <= 5; i++)
        {
            (next, evt) = Append(record, "a", $"p{i}", $"q{i}");
            record = await CommitAsync(store, record, next, evt);
        }

        Assert.Equal(6, await RowsAsync(schema, "a"));
        Assert.Equal(11, (await store.GetAsync(Key))!.Task.Artifacts![0].Parts.Count);
        Assert.Equal(PostgresArtifactRows.RowsLayout, await LayoutAsync(schema));

        // A status event changes no rows; a second artifact gets its own.
        var working = TaskStoreContract.WithState(record, TaskState.Working);
        record = await CommitAsync(store, record, working.Task, TaskStoreContract.StatusEvent("id1", TaskState.Working));
        (next, evt) = Append(record, "b", "b0");
        record = await CommitAsync(store, record, next, evt);
        Assert.Equal(6, await RowsAsync(schema, "a"));
        Assert.Equal(1, await RowsAsync(schema, "b"));
    }

    [Fact]
    public async Task A_replace_and_a_batch_keep_the_rows_right()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0");
        record = await CommitAsync(store, record, next, evt);

        // Replace (append = false) with new parts: the artifact's rows are rewritten as one.
        var replaced = record.Task.CopyForUpdate();
        replaced.Artifacts![0] = new Artifact { ArtifactId = "a", Name = "again", Parts = [Part.FromText("r0"), Part.FromText("r1")] };
        var replaceEvent = StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "id1", ContextId = "ctx", Artifact = new Artifact { ArtifactId = "a", Name = "again", Parts = [Part.FromText("r0"), Part.FromText("r1")] },
        });
        record = await CommitAsync(store, record, replaced, replaceEvent);
        Assert.Equal(1, await RowsAsync(schema, "a"));

        // A batch of three appends: one row per event's parts, all after the replace.
        var events = new List<BatchedEvent>();
        var batched = record;
        foreach (var t in new[] { "x", "y", "z" })
        {
            var (n, e) = Append(batched, "a", t);
            batched = batched with { Task = n };
            events.Add(new BatchedEvent(EventEncoder.Encode(0, e), []));
        }

        var result = await store.CommitBatchAsync(new TaskBatchCommit(batched, record.Version, events));
        Assert.Equal(Canonical(batched.Task), Canonical((await store.GetAsync(Key))!.Task));
        Assert.Equal(Canonical(batched.Task), Canonical(result.Record.Task));
        Assert.Equal(2, await RowsAsync(schema, "a"));
    }

    [Fact]
    public async Task An_artifact_is_compacted_into_one_row_once_it_has_enough_rows()
    {
        var (store, schema) = await CreateAsync(o => o.ArtifactRowsCompactAt = 3);
        var record = await CreateTaskAsync(store);
        for (var i = 0; i < 7; i++)
        {
            var (next, evt) = Append(record, "a", $"p{i}");
            record = await CommitAsync(store, record, next, evt);
            Assert.InRange(await RowsAsync(schema, "a"), 1, 3);
        }

        Assert.Equal(7, (await store.GetAsync(Key))!.Task.Artifacts![0].Parts.Count);
    }

    [Fact]
    public async Task Turning_the_option_off_and_on_again_keeps_the_task_right()
    {
        var (store, schema) = await CreateAsync();
        var holder = new NpgsqlDataSourceHolder(db.DataSource);
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0");
        record = await CommitAsync(store, record, next, evt);
        (next, evt) = Append(record, "a", "p1");
        record = await CommitAsync(store, record, next, evt);

        // Off: the task is written with its parts inline, and the old rows are ignored.
        var off = Reopen(holder, schema, rows: false);
        (next, evt) = Append(record, "a", "p2");
        record = await CommitAsync(off, record, next, evt);
        Assert.Equal(PostgresArtifactRows.InlineLayout, await LayoutAsync(schema));
        Assert.Equal(3, (await store.GetAsync(Key))!.Task.Artifacts![0].Parts.Count); // either store reads it right

        // On again: the inline task is moved to rows on its next commit.
        (next, evt) = Append(record, "a", "p3");
        record = await CommitAsync(store, record, next, evt);
        Assert.Equal(PostgresArtifactRows.RowsLayout, await LayoutAsync(schema));
        Assert.Equal(["p0", "p1", "p2", "p3"], (await off.GetAsync(Key))!.Task.Artifacts![0].Parts.OfType<TextPart>().Select(p => p.Text));
    }

    [Fact]
    public async Task InlineArtifactPartsAsync_moves_every_task_back_to_inline_parts()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0", "p1");
        record = await CommitAsync(store, record, next, evt);
        (next, evt) = Append(record, "a", "p2");
        record = await CommitAsync(store, record, next, evt);

        Assert.Equal(1, await store.InlineArtifactPartsAsync());
        Assert.Equal(0, await store.InlineArtifactPartsAsync());

        Assert.Equal(PostgresArtifactRows.InlineLayout, await LayoutAsync(schema));
        Assert.Equal(0, await RowsAsync(schema));
        var read = (await store.GetAsync(Key))!;
        Assert.Equal(Canonical(record.Task), Canonical(read.Task));
        // The content did not change, nor did the update time, but the version did: a commit based on an older read must
        // fail its compare-and-set.
        Assert.Equal(record.Version + 1, read.Version);
        Assert.Equal(record.UpdatedAt, read.UpdatedAt);
    }

    /// <summary>A task with artifact "a" in two rows (p0, then p1) and artifact "b" in one (b0).</summary>
    private static async Task<TaskRecord> TwoArtifactsAsync(TaskStore store)
    {
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0");
        record = await CommitAsync(store, record, next, evt);
        (next, evt) = Append(record, "a", "p1");
        record = await CommitAsync(store, record, next, evt);
        (next, evt) = Append(record, "b", "b0");
        return await CommitAsync(store, record, next, evt);
    }

    /// <summary>
    /// Race found in review: a read of a task in rows, then InlineArtifactPartsAsync, then a commit of what that read
    /// returned. Before the fix, the conversion kept the version, and a read torn by it (the task row in rows, then no
    /// rows) returned every artifact with no parts; the commit's compare-and-set matched and stored them inline for good.
    /// Now the conversion moves the version, so such a commit fails and the engine reads the task again.
    /// </summary>
    [Fact]
    public async Task A_commit_based_on_a_read_from_before_InlineArtifactPartsAsync_fails_and_no_part_is_lost()
    {
        var (store, schema) = await CreateAsync();
        var record = await TwoArtifactsAsync(store);
        var off = Reopen(new NpgsqlDataSourceHolder(db.DataSource), schema, rows: false); // the documented downgrade
        var stale = (await off.GetAsync(Key))!;
        Assert.Equal(Canonical(record.Task), Canonical(stale.Task));

        Assert.Equal(1, await off.InlineArtifactPartsAsync());

        // What a torn read returned before the fix (the version read with the layout in rows, and no rows), and what an
        // untorn read from before the conversion returns: neither may be committed now.
        var torn = TaskStoreContract.WithState(stale with { Task = PostgresArtifactRows.WithoutParts(stale.Task) }, TaskState.Working);
        var whole = TaskStoreContract.WithState(stale, TaskState.Working);
        var working = TaskStoreContract.StatusEvent(Key.TaskId, TaskState.Working);
        foreach (var target in new TaskStore[] { off, store })
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => target.CommitAsync(new TaskCommit(torn, stale.Version, working, [])).AsTask());
            await Assert.ThrowsAsync<ConcurrencyException>(() => target.CommitAsync(new TaskCommit(whole, stale.Version, working, [])).AsTask());
        }

        var read = (await off.GetAsync(Key))!;
        Assert.Equal(stale.Version + 1, read.Version);
        Assert.Equal(Canonical(record.Task), Canonical(read.Task));
        Assert.Equal(["p0", "p1"], read.Task.Artifacts![0].Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(["b0"], read.Task.Artifacts![1].Parts.OfType<TextPart>().Select(p => p.Text));
    }

    /// <summary>
    /// Race found in review, with the interleaving forced by a hook: an instance with the option on reads the layout and
    /// rows before its compare-and-set, and InlineArtifactPartsAsync converts the task right then. Before the fix, the
    /// compare-and-set matched, set the layout back to rows and wrote only the appended part, so "b" and the older parts of
    /// "a" read back empty. Now the commit fails, and its retry from a new read keeps every part.
    /// </summary>
    [Fact]
    public async Task InlineArtifactPartsAsync_between_a_commits_read_and_its_compare_and_set_fails_that_commit()
    {
        var (store, schema) = await CreateAsync();
        var record = await TwoArtifactsAsync(store);
        var other = Reopen(new NpgsqlDataSourceHolder(db.DataSource), schema, rows: false);
        var converted = -1;
        store.BeforeCompareAndSet = async () =>
        {
            store.BeforeCompareAndSet = null;
            converted = await other.InlineArtifactPartsAsync();
        };

        var (next, evt) = Append(record, "a", "p2");
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.CommitAsync(new TaskCommit(record with { Task = next }, record.Version, evt, [])).AsTask());
        Assert.Equal(1, converted);
        Assert.Equal(PostgresArtifactRows.InlineLayout, await LayoutAsync(schema));

        // The engine reads the task again and applies the event to it.
        var current = (await store.GetAsync(Key))!;
        Assert.Equal(record.Version + 1, current.Version);
        Assert.Equal(Canonical(record.Task), Canonical(current.Task));
        (next, evt) = Append(current, "a", "p2");
        await CommitAsync(store, current, next, evt); // reads back as committed
        Assert.Equal(PostgresArtifactRows.RowsLayout, await LayoutAsync(schema));
        var read = (await other.GetAsync(Key))!;
        Assert.Equal(["p0", "p1", "p2"], read.Task.Artifacts![0].Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(["b0"], read.Task.Artifacts![1].Parts.OfType<TextPart>().Select(p => p.Text));
    }

    /// <summary>
    /// A read is never torn: the task row and its parts come from one snapshot. The interleaving is forced with a lock.
    /// Another transaction holds an exclusive lock on the parts table, so the read waits there. While it waits, that
    /// transaction writes a new version of the task (an append, or the conversion of InlineArtifactPartsAsync) and commits.
    /// A read in two statements returned the old task row (version N) with the new rows (N + 1): the append's part under
    /// the old version, or no parts at all after the conversion. One statement waits for the lock before it takes its
    /// snapshot, so it returns version N + 1 whole.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_read_returns_the_parts_of_the_version_it_returns(bool inline, bool list)
    {
        var (store, schema) = await CreateAsync();
        var record = await TwoArtifactsAsync(store);
        var s = $"\"{schema.Schema}\"";
        var expected = inline ? record.Task : Append(record, "a", "late").Next;

        await using var writer = await db.DataSource.OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var lockParts = new NpgsqlCommand($"LOCK TABLE {s}.a2a_artifact_parts IN ACCESS EXCLUSIVE MODE", writer, tx))
            await lockParts.ExecuteNonQueryAsync();

        var reading = Task.Run(async () => list
            ? Assert.Single((await store.ListAsync(new TaskQuery("t", "alice", null, null, null, 10, null))).Records)
            : (await store.GetAsync(Key))!);
        await WaitForLockWaitAsync($"{s}.a2a_artifact_parts", reading);

        await using (var change = new NpgsqlBatch(writer, tx))
        {
            if (inline)
            {
                // What InlineArtifactPartsAsync writes.
                change.BatchCommands.Add(Command($"UPDATE {s}.a2a_tasks SET task = $1, parts_layout = 0, version = version + 1 WHERE task_id = 'id1'",
                    A2AJson.Serialize(record.Task)));
                change.BatchCommands.Add(Command($"DELETE FROM {s}.a2a_artifact_parts WHERE task_id = 'id1'"));
            }
            else
            {
                // What an append writes with the option on: one row with the new part, and the next version.
                change.BatchCommands.Add(Command(
                    $"INSERT INTO {s}.a2a_artifact_parts (tenant, task_id, artifact_id, ordinal, part_count, parts) VALUES ('t', 'id1', 'a', 2, 1, $1)",
                    A2AJson.Serialize(new Artifact { ArtifactId = "a", Parts = [Part.FromText("late")] })));
                change.BatchCommands.Add(Command($"UPDATE {s}.a2a_tasks SET task = $1, version = version + 1 WHERE task_id = 'id1'",
                    A2AJson.Serialize(PostgresArtifactRows.WithoutParts(expected))));
            }

            await change.ExecuteNonQueryAsync();
        }

        Assert.False(reading.IsCompleted); // still waiting for the lock
        await tx.CommitAsync();
        var read = await reading.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(record.Version + 1, read.Version);
        Assert.Equal(Canonical(expected), Canonical(read.Task));
    }

    private static NpgsqlBatchCommand Command(string sql, params string[] args)
    {
        var cmd = new NpgsqlBatchCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = a });
        return cmd;
    }

    /// <summary>Waits until a query waits for a lock on <paramref name="table" />. Fails if <paramref name="reading" /> ends first.</summary>
    private async Task WaitForLockWaitAsync(string table, Task reading)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM pg_locks WHERE NOT granted AND relation = to_regclass($1)");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = table });
        while ((long)(await cmd.ExecuteScalarAsync())! == 0)
        {
            if (reading.IsCompleted)
            {
                await reading; // its exception, if any
                Assert.Fail("The read finished without waiting for the lock on the parts table.");
            }

            if (DateTime.UtcNow > deadline) Assert.Fail("The read never waited for the lock on the parts table.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Rows_go_with_their_task_and_ListTasks_returns_the_parts()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0");
        record = await CommitAsync(store, record, next, evt);

        var page = await store.ListAsync(new TaskQuery("t", "alice", null, null, null, 10, null));
        Assert.Equal(Canonical(record.Task), Canonical(Assert.Single(page.Records).Task));

        Assert.True(await store.DeleteAsync(Key));
        Assert.Equal(0, await RowsAsync(schema));
    }

    [Fact]
    public void The_copy_without_parts_keeps_every_other_artifact_property()
    {
        // Guards WithoutParts against a property added to Artifact later: every settable property is set here, and every one
        // but Parts must survive.
        var artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] };
        var values = new Dictionary<string, object?>();
        foreach (var p in typeof(Artifact).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            object? value = p.PropertyType == typeof(string) ? $"v-{p.Name}"
                : p.PropertyType == typeof(long?) ? 7L
                : p.PropertyType == typeof(Role?) ? Role.Agent
                : p.PropertyType == typeof(List<string>) ? new List<string> { "e" }
                : p.PropertyType == typeof(Dictionary<string, System.Text.Json.JsonElement>) ? new Dictionary<string, System.Text.Json.JsonElement> { ["k"] = A2AJson.SerializeToElement(1) }
                : p.Name == nameof(Artifact.Parts) ? p.GetValue(artifact)
                : throw new InvalidOperationException($"Artifact.{p.Name} ({p.PropertyType.Name}) is new: give it a value here and copy it in WithoutParts.");
            p.SetValue(artifact, value);
            values[p.Name] = value;
        }

        var copy = PostgresArtifactRows.WithoutParts(artifact);

        Assert.Empty(copy.Parts);
        foreach (var (name, value) in values.Where(v => v.Key != nameof(Artifact.Parts)))
            Assert.Equal(value, typeof(Artifact).GetProperty(name)!.GetValue(copy));

        // CopyParts, the way back: every property but Parts comes from the parsed artifact, the parts from the source.
        var parsed = PostgresArtifactRows.WithoutParts(artifact);
        var stored = new AgentTask { Id = "t", ContextId = "c", Status = new() { State = TaskState.Working }, Artifacts = [parsed] };
        var source = new AgentTask { Id = "t", ContextId = "c", Status = new() { State = TaskState.Working }, Artifacts = [artifact.CopyWithParts([Part.FromText("y")])] };
        PostgresArtifactRows.CopyParts(source, stored);
        var back = Assert.Single(stored.Artifacts!);
        Assert.Equal("xy", back.GetText());
        foreach (var (name, _) in values.Where(v => v.Key != nameof(Artifact.Parts)))
        {
            var want = typeof(Artifact).GetProperty(name)!.GetValue(parsed);
            var got = typeof(Artifact).GetProperty(name)!.GetValue(back);
            if (want is null || want.GetType().IsValueType) Assert.Equal(want, got);
            else Assert.Same(want, got);
        }
    }

    /// <summary>ADR-004's internal probe (A2A.Protocol has no InternalsVisibleTo for this assembly): whether the list was built.</summary>
    private static bool PartsBuilt(Artifact artifact)
    {
        var store = typeof(Artifact).GetField("PartsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(artifact)!;
        return (bool)store.GetType().GetProperty("IsMaterialized")!.GetValue(store)!;
    }

    [Fact]
    public async Task Commits_do_not_build_the_part_lists()
    {
        // ADR-004: the engine's artifact copies share their parts until someone reads Artifact.Parts. With the parts in
        // rows the store has no reason to read them all: it counts them and reads only the appended ones. So neither the
        // task it is given nor the one it returns has its list built, by single commits, a batch and a compaction alike,
        // and the next copy stays O(1).
        var (store, schema) = await CreateAsync(o => o.ArtifactRowsCompactAt = 4);
        var record = await CreateTaskAsync(store);
        var (next, evt) = Append(record, "a", "p0");
        record = (await store.CommitAsync(new TaskCommit(record with { Task = next }, record.Version, evt, []))).Record;
        Assert.False(PartsBuilt(record.Task.Artifacts![0])); // the first artifact's list was set; the returned copy shares it

        var expected = new List<string> { "p0" };
        for (var i = 1; i <= 9; i++)
        {
            (next, evt) = Append(record, "a", $"p{i}", $"q{i}");
            expected.AddRange([$"p{i}", $"q{i}"]);
            Assert.False(PartsBuilt(next.Artifacts![0]));
            var committed = await store.CommitAsync(new TaskCommit(record with { Task = next }, record.Version, evt, []));
            Assert.False(PartsBuilt(next.Artifacts![0]));
            Assert.False(PartsBuilt(committed.Record.Task.Artifacts![0]));
            record = committed.Record;
            Assert.InRange(await RowsAsync(schema, "a"), 1, 4); // compacted along the way
        }

        var events = new List<BatchedEvent>();
        var batched = record;
        foreach (var t in new[] { "x", "y", "z" })
        {
            var (n, e) = Append(batched, "a", t);
            expected.Add(t);
            batched = batched with { Task = n };
            events.Add(new BatchedEvent(EventEncoder.Encode(0, e), []));
        }

        var result = await store.CommitBatchAsync(new TaskBatchCommit(batched, record.Version, events));
        Assert.False(PartsBuilt(batched.Task.Artifacts![0]));
        Assert.False(PartsBuilt(result.Record.Task.Artifacts![0]));

        // Reading them now gives what was committed, and what the database holds.
        var read = (await store.GetAsync(Key))!.Task;
        Assert.Equal(expected, read.Artifacts![0].Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(Canonical(batched.Task), Canonical(read));
        Assert.Equal(Canonical(batched.Task), Canonical(result.Record.Task));
    }

    /// <summary>The engine end to end: an agent streams one artifact in many chunks onto Postgres with the option on.</summary>
    [Fact]
    public async Task A_streamed_artifact_reads_back_whole_through_the_engine()
    {
        var schema = await db.NewSchemaAsync();
        var card = new AgentCard
        {
            Name = "rows", Description = "rows", Version = "1.0.0",
            SupportedInterfaces = [new AgentInterface { Url = "http://localhost/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
            Capabilities = new AgentCapabilities { Streaming = true },
            DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "s", Name = "s", Description = "s", Tags = ["s"] }],
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = card;
            o.AllowAnonymous = true;
        }).AddAgentExecutor<ChunkAgent>().AddPostgresStores(db.DataSource, o =>
        {
            o.Schema = schema.Schema;
            o.CreateSchemaOnStartup = false;
            o.ArtifactRows = true;
            o.ArtifactRowsCompactAt = 16;
        });
        builder.Services.AddA2AServerAspNetCore();
        await using var app = builder.Build();
        app.MapA2A("/a2a");
        await app.StartAsync();
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(app.GetTestClient(), new Uri("http://localhost/a2a"), options), options, card);

        var task = ((TaskResponse)await client.SendMessageAsync("go")).Task;
        var read = await client.GetTaskAsync(task.Id);

        Assert.Equal(TaskState.Completed, read.Status.State);
        Assert.Equal(Enumerable.Range(0, ChunkAgent.Chunks).Select(i => $"c{i}"),
            read.Artifacts!.Single().Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal(Canonical(task), Canonical(read));
        await app.StopAsync();
    }

    private sealed class ChunkAgent : IAgentExecutor
    {
        public const int Chunks = 100;

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            var id = await updater.AddArtifactAsync([Part.FromText("c0")], name: "out", cancellationToken: ct);
            for (var i = 1; i < Chunks; i++)
            {
                await updater.AddArtifactAsync([Part.FromText($"c{i}")], artifactId: id, append: true,
                    lastChunk: i == Chunks - 1, cancellationToken: ct);
                if (i % 10 == 0) await Task.Delay(5, ct); // some chunks alone, some batched
            }

            await updater.CompleteAsync(cancellationToken: ct);
        }
    }
}
