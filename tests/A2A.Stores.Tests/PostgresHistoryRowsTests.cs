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

/// <summary>The whole task store contract, with the history and timeline in rows (PostgresStoreOptions.HistoryRows).</summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresHistoryRowsTaskStoreTests(PostgresFixture db) : TaskStoreContract
{
    protected override async Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null)
    {
        var schema = await db.NewSchemaAsync(time);
        schema.HistoryRows = true;
        var streaming = new StreamingOptions();
        return (new PostgresTaskStore(db.DataSource, streaming, schema, time), new PostgresEventStore(db.DataSource, streaming, schema, time));
    }
}

/// <summary>The whole task store contract, with the artifact parts and the history and timeline in rows.</summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresAllRowsTaskStoreTests(PostgresFixture db) : TaskStoreContract
{
    protected override async Task<(TaskStore Tasks, EventStore Events)> CreateStoresAsync(TimeProvider? time = null)
    {
        var schema = await db.NewSchemaAsync(time);
        schema.HistoryRows = true;
        schema.ArtifactRows = true;
        var streaming = new StreamingOptions();
        return (new PostgresTaskStore(db.DataSource, streaming, schema, time), new PostgresEventStore(db.DataSource, streaming, schema, time));
    }
}

/// <summary>
/// PostgresStoreOptions.HistoryRows (ADR-005, docs/superpowers/specs/2026-11-13-postgres-history-rows-design.md): a status
/// update or a follow-up message inserts its history message and its timeline entry, the timeline cap deletes the oldest
/// rows, and a task reads back exactly as it was committed, and exactly as the same task stored inline.
/// </summary>
[Collection(ContainersCollection.Name)]
public sealed class PostgresHistoryRowsTests(PostgresFixture db)
{
    private static readonly TaskKey Key = new("t", "alice", "id1");

    private async Task<(PostgresTaskStore Store, PostgresStoreOptions Schema)> CreateAsync(bool rows = true, bool artifactRows = false)
    {
        var schema = await db.NewSchemaAsync();
        schema.HistoryRows = rows;
        schema.ArtifactRows = artifactRows;
        return (new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema), schema);
    }

    private PostgresTaskStore Reopen(PostgresStoreOptions schema, bool rows, bool artifactRows = false) =>
        new(db.DataSource, new StreamingOptions(), new PostgresStoreOptions
        {
            Schema = schema.Schema, UseDatabaseClock = schema.UseDatabaseClock, HistoryRows = rows, ArtifactRows = artifactRows,
        });

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

    private static Message AgentMessage(string text) =>
        new() { MessageId = Guid.NewGuid().ToString("N"), Role = Role.Agent, Parts = [Part.FromText(text)] };

    /// <summary>What the engine does for a status update (ApplyToCopy): the message to the history, the status to the timeline.</summary>
    private static (AgentTask Next, StreamResponse Event) Status(TaskRecord current, string? text, int maxTimeline = 0,
        TaskState state = TaskState.Working)
    {
        var status = new AgentTaskStatus { State = state, Message = text is null ? null : AgentMessage(text), Timestamp = DateTimeOffset.UtcNow };
        var generation = (current.Task.Generation ?? 0) + 1;
        var next = current.Task.CopyForUpdate();
        next.Status = new AgentTaskStatus { State = status.State, Message = status.Message, Timestamp = status.Timestamp };
        if (status.Message is { } m) next.AppendToHistory(m);
        next.AppendToTimeline(TimelineEntry.FromStatus(next.Status, generation), maxTimeline);
        next.Generation = generation;
        var evt = StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = Key.TaskId, ContextId = "ctx", Status = status, Generation = generation });
        return (next, evt);
    }

    /// <summary>What the request handler does for a follow-up message: the message to the history and the timeline.</summary>
    private static (AgentTask Next, StreamResponse Event) FollowUp(TaskRecord current, string text, int maxTimeline = 0)
    {
        var message = new Message { MessageId = Guid.NewGuid().ToString("N"), Role = Role.User, Parts = [Part.FromText(text)], TaskId = Key.TaskId, ContextId = "ctx" };
        var generation = (current.Task.Generation ?? 0) + 1;
        var next = current.Task.CopyForUpdate();
        next.AppendToHistory(message);
        next.Generation = generation;
        next.AppendToTimeline(TimelineEntry.FromMessage(message, generation), maxTimeline);
        var evt = StreamResponse.Of(new TaskMessageUpdateEvent { TaskId = Key.TaskId, ContextId = "ctx", Message = message, Generation = generation });
        return (next, evt);
    }

    /// <summary>A task created as the engine creates one: the client's message is its history and its first timeline entry.</summary>
    private static async Task<TaskRecord> CreateTaskAsync(TaskStore store)
    {
        var record = TaskStoreContract.NewRecord("t", "alice", "id1");
        var first = new Message { MessageId = "m0", Role = Role.User, Parts = [Part.FromText("hello")], TaskId = "id1", ContextId = "ctx" };
        record.Task.History = [first];
        record.Task.Generation = 1;
        record.Task.Timeline = [TimelineEntry.FromMessage(first, 1)];
        return (await store.CommitAsync(TaskStoreContract.CreateCommit(record))).Record;
    }

    private async Task<(long Count, long? Min, long? Max)> RowsAsync(PostgresStoreOptions schema, short list)
    {
        await using var cmd = db.DataSource.CreateCommand(
            $"SELECT count(*), min(ordinal), max(ordinal) FROM \"{schema.Schema}\".a2a_task_entries WHERE task_id = 'id1' AND list = {list}");
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private async Task<long> AllRowsAsync(PostgresStoreOptions schema)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT count(*) FROM \"{schema.Schema}\".a2a_task_entries");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<short> LayoutAsync(PostgresStoreOptions schema)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT entries_layout FROM \"{schema.Schema}\".a2a_tasks WHERE task_id = 'id1'");
        return (short)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> TaskJsonAsync(PostgresStoreOptions schema)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT task FROM \"{schema.Schema}\".a2a_tasks WHERE task_id = 'id1'");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Each_status_update_and_message_adds_its_rows_and_the_task_reads_back_as_committed()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        Assert.Equal(PostgresEntryRows.RowsLayout, await LayoutAsync(schema));
        Assert.Equal((1L, 0L, 0L), await RowsAsync(schema, PostgresEntryRows.HistoryList));
        Assert.Equal((1L, 0L, 0L), await RowsAsync(schema, PostgresEntryRows.TimelineList));

        for (var i = 0; i < 5; i++)
        {
            var (next, evt) = Status(record, $"step {i}");
            record = await CommitAsync(store, record, next, evt);
        }

        // A bare status update adds a timeline entry only; a follow-up message adds to both.
        var (bare, bareEvent) = Status(record, null);
        record = await CommitAsync(store, record, bare, bareEvent);
        var (follow, followEvent) = FollowUp(record, "more");
        record = await CommitAsync(store, record, follow, followEvent);

        Assert.Equal((7L, 0L, 6L), await RowsAsync(schema, PostgresEntryRows.HistoryList));
        Assert.Equal((8L, 0L, 7L), await RowsAsync(schema, PostgresEntryRows.TimelineList));
        // The task row holds empty lists: its size does not grow with them.
        var json = await TaskJsonAsync(schema);
        Assert.Contains("\"history\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"timeline\":[]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("step 0", json, StringComparison.Ordinal);

        // An artifact event changes neither list: no rows are written for them.
        var withArtifact = record.Task.CopyForUpdate();
        (withArtifact.Artifacts ??= []).Add(new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] });
        record = await CommitAsync(store, record, withArtifact,
            StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "id1", ContextId = "ctx", Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] } }));
        Assert.Equal((7L, 0L, 6L), await RowsAsync(schema, PostgresEntryRows.HistoryList));
        Assert.Equal((8L, 0L, 7L), await RowsAsync(schema, PostgresEntryRows.TimelineList));
        Assert.Equal(7, record.Task.History!.Count);
    }

    [Fact]
    public async Task The_timeline_cap_deletes_the_oldest_rows()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        for (var i = 0; i < 9; i++)
        {
            var (next, evt) = i % 3 == 2 ? FollowUp(record, $"user {i}", maxTimeline: 4) : Status(record, i % 2 == 0 ? $"step {i}" : null, maxTimeline: 4);
            record = await CommitAsync(store, record, next, evt);
            Assert.Equal(Math.Min(i + 2, 4), record.Task.Timeline!.Count);
            var (count, min, max) = await RowsAsync(schema, PostgresEntryRows.TimelineList);
            Assert.Equal(record.Task.Timeline!.Count, count);
            Assert.Equal(i + 1, max); // ordinals keep counting: one per appended entry
            Assert.Equal(max - count + 1, min); // contiguous: the lowest were deleted
        }

        // A smaller cap on another instance (two dropped at once), then a larger one again (none dropped).
        var (smaller, smallerEvent) = Status(record, "smaller", maxTimeline: 2);
        record = await CommitAsync(store, record, smaller, smallerEvent);
        Assert.Equal((2L, 9L, 10L), await RowsAsync(schema, PostgresEntryRows.TimelineList));
        var (larger, largerEvent) = Status(record, "larger", maxTimeline: 100);
        record = await CommitAsync(store, record, larger, largerEvent);
        Assert.Equal((3L, 9L, 11L), await RowsAsync(schema, PostgresEntryRows.TimelineList));
        // The history is never capped.
        Assert.Equal(record.Task.History!.Count, (await RowsAsync(schema, PostgresEntryRows.HistoryList)).Count);
    }

    [Fact]
    public async Task A_task_reads_back_exactly_as_the_same_task_stored_inline()
    {
        var (rows, rowsSchema) = await CreateAsync(rows: true);
        var (inline, _) = await CreateAsync(rows: false);
        var r = await CreateTaskAsync(rows);
        var i = await CreateTaskAsync(inline);
        for (var n = 0; n < 12; n++)
        {
            var (next, evt) = n % 4 == 3 ? FollowUp(r, $"user {n}", 5) : Status(r, n % 3 == 0 ? null : $"step {n} \u0000 é \"q\"", 5);
            r = await CommitAsync(rows, r, next, evt);
            i = (await inline.CommitAsync(new TaskCommit(i with { Task = next }, i.Version, evt, []))).Record;
        }

        var (done, doneEvent) = Status(r, "done", 5, TaskState.Completed);
        r = await CommitAsync(rows, r, done, doneEvent);
        i = (await inline.CommitAsync(new TaskCommit(i with { Task = done }, i.Version, doneEvent, []))).Record;

        var fromRows = (await rows.GetAsync(Key))!;
        var fromInline = (await inline.GetAsync(Key))!;
        Assert.Equal(A2AJson.Serialize(fromInline.Task), A2AJson.Serialize(fromRows.Task)); // byte for byte
        Assert.Equal(fromInline.Version, fromRows.Version);
        var listed = Assert.Single((await rows.ListAsync(new TaskQuery("t", "alice", null, null, null, 10, null))).Records);
        Assert.Equal(A2AJson.Serialize(fromInline.Task), A2AJson.Serialize(listed.Task));
        Assert.Equal(A2AJson.Serialize(fromInline.Task), A2AJson.Serialize((await rows.GetForMaintenanceAsync("t", "id1"))!.Task));
        Assert.Equal(PostgresEntryRows.RowsLayout, await LayoutAsync(rowsSchema));

        // The conversation memory reads the same messages, and cuts at the same bytes. The NUL in some messages makes
        // the inline store fall back to whole tasks; the rows are read as text.
        foreach (var maxBytes in new long[] { 1_000_000, 700, 1 })
        {
            var query = new ContextHistoryQuery("t", "alice", "ctx", 10, maxBytes, null);
            Assert.Equal((await inline.ReadContextMessagesAsync(query)).Select(A2AJson.Serialize),
                (await rows.ReadContextMessagesAsync(query)).Select(A2AJson.Serialize));
        }
    }

    [Fact]
    public async Task ReadContextMessages_reads_tasks_in_rows_and_inline_together()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "agent says");
        record = await CommitAsync(store, record, next, evt);
        (next, evt) = Status(record, "finished", state: TaskState.Completed);
        await CommitAsync(store, record, next, evt);
        // A second task of the context, stored inline.
        var other = TaskStoreContract.NewRecord("t", "alice", "id2", DateTimeOffset.UtcNow.AddMinutes(1));
        other.Task.Status.State = TaskState.Completed;
        other.Task.History = [AgentMessage("inline one")];
        await store.CreateAsync(other);

        var messages = await store.ReadContextMessagesAsync(new ContextHistoryQuery("t", "alice", "ctx", 10, 1_000_000, null));
        Assert.Equal(["hello", "agent says", "finished", "inline one"], messages.Select(m => m.GetText()));
        Assert.Equal(PostgresEntryRows.RowsLayout, await LayoutAsync(schema));
    }

    [Fact]
    public async Task A_null_list_and_an_empty_list_read_back_as_they_were()
    {
        var (store, _) = await CreateAsync();
        var record = TaskStoreContract.NewRecord("t", "alice", "id1");
        record.Task.History = [];
        record = (await store.CommitAsync(TaskStoreContract.CreateCommit(record))).Record;
        var read = (await store.GetAsync(Key))!.Task;
        Assert.NotNull(read.History);
        Assert.Empty(read.History);
        Assert.Null(read.Timeline);

        var (next, evt) = Status(record, null);
        record = await CommitAsync(store, record, next, evt);
        read = (await store.GetAsync(Key))!.Task;
        Assert.Empty(read.History!);
        Assert.Single(read.Timeline!);
    }

    [Fact]
    public async Task A_commit_that_does_not_match_the_rows_rewrites_the_list()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        record = await CommitAsync(store, record, next, evt);

        // A status event whose task also lost a history message and changed another: the counts do not match the rows,
        // so the history is rewritten from the task (ordinals from 0 again).
        (next, evt) = Status(record, "two");
        next.History = [AgentMessage("rewritten"), next.History![^1]];
        record = await CommitAsync(store, record, next, evt);
        Assert.Equal((2L, 0L, 1L), await RowsAsync(schema, PostgresEntryRows.HistoryList));

        // The newest message differs from the event's: rewritten too, never appended.
        (next, evt) = Status(record, "three");
        next.History![^1] = AgentMessage("not the event's");
        record = await CommitAsync(store, record, next, evt);
        Assert.Equal(["rewritten", "two", "not the event's"], (await store.GetAsync(Key))!.Task.History!.Select(m => m.GetText()));
        Assert.Equal(3, (await RowsAsync(schema, PostgresEntryRows.HistoryList)).Count);
    }

    [Fact]
    public async Task Turning_the_option_off_and_on_again_keeps_the_task_right()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        record = await CommitAsync(store, record, next, evt);

        // Off: the task is written with its lists inline, and the old rows are ignored.
        var off = Reopen(schema, rows: false);
        (next, evt) = Status(record, "two");
        record = await CommitAsync(off, record, next, evt);
        Assert.Equal(PostgresEntryRows.InlineLayout, await LayoutAsync(schema));
        Assert.Equal(3, (await store.GetAsync(Key))!.Task.History!.Count); // either store reads it right

        // On again: the inline task is moved to rows on its next commit.
        (next, evt) = Status(record, "three");
        record = await CommitAsync(store, record, next, evt);
        Assert.Equal(PostgresEntryRows.RowsLayout, await LayoutAsync(schema));
        Assert.Equal((4L, 0L, 3L), await RowsAsync(schema, PostgresEntryRows.HistoryList));
        Assert.Equal(["hello", "one", "two", "three"], (await off.GetAsync(Key))!.Task.History!.Select(m => m.GetText()));

        // UpdateAsync writes inline, whatever the option says.
        var updated = await store.UpdateAsync(record with { Task = record.Task.CopyForUpdate() }, record.Version);
        Assert.Equal(PostgresEntryRows.InlineLayout, await LayoutAsync(schema));
        Assert.Equal(Canonical(record.Task), Canonical((await store.GetAsync(Key))!.Task));
        (next, evt) = Status(updated, "four");
        await CommitAsync(store, updated, next, evt);
        Assert.Equal((5L, 0L, 4L), await RowsAsync(schema, PostgresEntryRows.HistoryList));
    }

    [Fact]
    public async Task InlineHistoryAndTimelineAsync_moves_every_task_back_to_inline_lists()
    {
        var (store, schema) = await CreateAsync(artifactRows: true);
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        record = await CommitAsync(store, record, next, evt);
        var withArtifact = record.Task.CopyForUpdate();
        (withArtifact.Artifacts ??= []).Add(new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] });
        record = await CommitAsync(store, record, withArtifact,
            StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "id1", ContextId = "ctx", Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] } }));

        Assert.Equal(1, await store.InlineHistoryAndTimelineAsync());
        Assert.Equal(0, await store.InlineHistoryAndTimelineAsync());

        Assert.Equal(PostgresEntryRows.InlineLayout, await LayoutAsync(schema));
        Assert.Equal(0, await AllRowsAsync(schema));
        var json = await TaskJsonAsync(schema);
        Assert.Contains("one", json, StringComparison.Ordinal);
        Assert.Contains("\"parts\":[]", json, StringComparison.Ordinal); // the parts stay in their rows
        var read = (await store.GetAsync(Key))!;
        Assert.Equal(Canonical(record.Task), Canonical(read.Task));
        Assert.Equal(A2AJson.Serialize(record.Task), A2AJson.Serialize(read.Task));
        Assert.Equal(record.Version + 1, read.Version);
        Assert.Equal(record.UpdatedAt, read.UpdatedAt);

        // And the other way: the parts go back, the lists stay inline.
        Assert.Equal(1, await store.InlineArtifactPartsAsync());
        Assert.Equal(Canonical(record.Task), Canonical((await store.GetAsync(Key))!.Task));
        Assert.Equal(PostgresEntryRows.InlineLayout, await LayoutAsync(schema));
    }

    /// <summary>
    /// A read of a task in rows, then InlineHistoryAndTimelineAsync, then a commit of what that read returned (or of what a
    /// torn read would have returned: the task row in rows with no rows): the conversion moves the version, so neither
    /// commit passes its compare-and-set, from an instance with the option off or on.
    /// </summary>
    [Fact]
    public async Task A_commit_based_on_a_read_from_before_InlineHistoryAndTimelineAsync_fails_and_nothing_is_lost()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        record = await CommitAsync(store, record, next, evt);
        var off = Reopen(schema, rows: false);
        var stale = (await off.GetAsync(Key))!;

        Assert.Equal(1, await off.InlineHistoryAndTimelineAsync());

        var torn = stale.Task.CopyForUpdate();
        torn.History = [];
        torn.Timeline = [];
        foreach (var target in new TaskStore[] { off, store })
        foreach (var task in new[] { stale.Task, torn })
        {
            var (n, e) = Status(stale with { Task = task }, "late");
            await Assert.ThrowsAsync<ConcurrencyException>(() => target.CommitAsync(new TaskCommit(stale with { Task = n }, stale.Version, e, [])).AsTask());
        }

        var read = (await off.GetAsync(Key))!;
        Assert.Equal(stale.Version + 1, read.Version);
        Assert.Equal(Canonical(record.Task), Canonical(read.Task));
    }

    /// <summary>
    /// The interleaving forced by the hook: an instance with the option on reads the layout and the ranges before its
    /// compare-and-set, and InlineHistoryAndTimelineAsync converts the task right then. The commit fails (else it would set
    /// the layout back to rows and insert only its new entries, losing the older ones), and its retry from a new read
    /// keeps every message.
    /// </summary>
    [Fact]
    public async Task InlineHistoryAndTimelineAsync_between_a_commits_read_and_its_compare_and_set_fails_that_commit()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        record = await CommitAsync(store, record, next, evt);
        var other = Reopen(schema, rows: false);
        var converted = -1;
        store.BeforeCompareAndSet = async () =>
        {
            store.BeforeCompareAndSet = null;
            converted = await other.InlineHistoryAndTimelineAsync();
        };

        (next, evt) = Status(record, "two");
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.CommitAsync(new TaskCommit(record with { Task = next }, record.Version, evt, [])).AsTask());
        Assert.Equal(1, converted);
        Assert.Equal(PostgresEntryRows.InlineLayout, await LayoutAsync(schema));

        var current = (await store.GetAsync(Key))!;
        Assert.Equal(record.Version + 1, current.Version);
        Assert.Equal(Canonical(record.Task), Canonical(current.Task));
        (next, evt) = Status(current, "two");
        await CommitAsync(store, current, next, evt);
        Assert.Equal(PostgresEntryRows.RowsLayout, await LayoutAsync(schema));
        Assert.Equal(["hello", "one", "two"], (await other.GetAsync(Key))!.Task.History!.Select(m => m.GetText()));
        Assert.Equal(3, (await other.GetAsync(Key))!.Task.Timeline!.Count);
    }

    /// <summary>
    /// A read is never torn: the task row and its history and timeline rows come from one snapshot. Another transaction holds
    /// an exclusive lock on the entries table, so the read waits there; meanwhile that transaction writes a new version (an
    /// appended status entry, or the conversion of InlineHistoryAndTimelineAsync) and commits. One statement waits for the
    /// lock before it takes its snapshot, so it returns version N + 1 whole.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_read_returns_the_entries_of_the_version_it_returns(bool inline, bool list)
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (one, oneEvent) = Status(record, "one");
        record = await CommitAsync(store, record, one, oneEvent);
        var s = $"\"{schema.Schema}\"";
        var late = Status(record, "late").Next;
        var expected = inline ? record.Task : late;

        await using var writer = await db.DataSource.OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await using (var lockRows = new NpgsqlCommand($"LOCK TABLE {s}.a2a_task_entries IN ACCESS EXCLUSIVE MODE", writer, tx))
            await lockRows.ExecuteNonQueryAsync();

        var reading = Task.Run(async () => list
            ? Assert.Single((await store.ListAsync(new TaskQuery("t", "alice", null, null, null, 10, null))).Records)
            : (await store.GetAsync(Key))!);
        await WaitForLockWaitAsync($"{s}.a2a_task_entries", reading);

        await using (var change = new NpgsqlBatch(writer, tx))
        {
            if (inline)
            {
                // What InlineHistoryAndTimelineAsync writes.
                change.BatchCommands.Add(Command($"UPDATE {s}.a2a_tasks SET task = $1, entries_layout = 0, version = version + 1 WHERE task_id = 'id1'",
                    A2AJson.Serialize(record.Task)));
                change.BatchCommands.Add(Command($"DELETE FROM {s}.a2a_task_entries WHERE task_id = 'id1'"));
            }
            else
            {
                // What a status update writes with the option on: its message and its timeline entry, and the next version.
                change.BatchCommands.Add(Command(
                    $"INSERT INTO {s}.a2a_task_entries (tenant, task_id, list, ordinal, entry) VALUES ('t', 'id1', 0, 2, $1), ('t', 'id1', 1, 2, $2)",
                    A2AJson.Serialize(late.History![^1]), A2AJson.Serialize(late.Timeline![^1])));
                change.BatchCommands.Add(Command($"UPDATE {s}.a2a_tasks SET task = $1, version = version + 1 WHERE task_id = 'id1'",
                    A2AJson.Serialize(PostgresEntryRows.ForRow(late, new PostgresEntryRows.Lists(late.History, late.Timeline), false))));
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
                await reading;
                Assert.Fail("The read finished without waiting for the lock on the entries table.");
            }

            if (DateTime.UtcNow > deadline) Assert.Fail("The read never waited for the lock on the entries table.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Rows_go_with_their_task()
    {
        var (store, schema) = await CreateAsync();
        var record = await CreateTaskAsync(store);
        var (next, evt) = Status(record, "one");
        await CommitAsync(store, record, next, evt);
        Assert.Equal(4, await AllRowsAsync(schema));

        Assert.True(await store.DeleteAsync(Key));
        Assert.Equal(0, await AllRowsAsync(schema));
    }

    [Fact]
    public void The_task_row_copy_and_the_restored_copy_keep_every_other_task_property()
    {
        // Guards ForRow and Restore against a property added to AgentTask later: every settable property is set here, and
        // every one but History and Timeline must survive both.
        var task = new AgentTask { Id = "x", Status = new AgentTaskStatus { State = TaskState.Working } };
        var values = new Dictionary<string, object?>();
        foreach (var p in typeof(AgentTask).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            object? value = p.Name switch
            {
                nameof(AgentTask.History) => new List<Message> { AgentMessage("h") },
                nameof(AgentTask.Timeline) => new List<TimelineEntry> { TimelineEntry.FromStatus(new AgentTaskStatus { State = TaskState.Working }, 1) },
                _ when p.PropertyType == typeof(string) => $"v-{p.Name}",
                _ when p.PropertyType == typeof(long?) => 7L,
                _ when p.PropertyType == typeof(AgentTaskStatus) => new AgentTaskStatus { State = TaskState.InputRequired },
                _ when p.PropertyType == typeof(List<Artifact>) => new List<Artifact> { new() { ArtifactId = "a", Parts = [Part.FromText("p")] } },
                _ when p.PropertyType == typeof(Dictionary<string, System.Text.Json.JsonElement>) => new Dictionary<string, System.Text.Json.JsonElement> { ["k"] = A2AJson.SerializeToElement(1) },
                _ => throw new InvalidOperationException($"AgentTask.{p.Name} ({p.PropertyType.Name}) is new: give it a value here and copy it in PostgresEntryRows.ForRow and Restore."),
            };
            p.SetValue(task, value);
            values[p.Name] = value;
        }

        var lists = new PostgresEntryRows.Lists(task.History, task.Timeline);
        var row = PostgresEntryRows.ForRow(task, lists, false);
        Assert.Empty(row.History!);
        Assert.Empty(row.Timeline!);
        foreach (var (name, value) in values.Where(v => v.Key is not (nameof(AgentTask.History) or nameof(AgentTask.Timeline))))
            Assert.Equal(value, typeof(AgentTask).GetProperty(name)!.GetValue(row));

        var restored = PostgresEntryRows.Restore(task.CopyForUpdate(), row);
        Assert.Equal(A2AJson.Serialize(task), A2AJson.Serialize(restored));
        foreach (var (name, _) in values.Where(v => v.Key is not (nameof(AgentTask.History) or nameof(AgentTask.Timeline))))
        {
            var want = typeof(AgentTask).GetProperty(name)!.GetValue(row);
            var got = typeof(AgentTask).GetProperty(name)!.GetValue(restored);
            if (want is null || want.GetType().IsValueType) Assert.Equal(want, got);
            else Assert.Same(want, got);
        }
    }

    /// <summary>
    /// The engine end to end: an agent sends many status updates and the client a follow-up, on Postgres with the option
    /// on and a small timeline cap. The task read from the rows equals the same task after InlineHistoryAndTimelineAsync.
    /// </summary>
    [Fact]
    public async Task A_status_heavy_task_reads_back_whole_through_the_engine()
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
            o.Limits.MaxTimelineEntries = 10;
        }).AddAgentExecutor<StatusAgent>().AddPostgresStores(db.DataSource, o =>
        {
            o.Schema = schema.Schema;
            o.CreateSchemaOnStartup = false;
            o.HistoryRows = true;
        });
        builder.Services.AddA2AServerAspNetCore();
        await using var app = builder.Build();
        app.MapA2A("/a2a");
        await app.StartAsync();
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(app.GetTestClient(), new Uri("http://localhost/a2a"), options), options, card);

        var task = ((TaskResponse)await client.SendMessageAsync("go")).Task;
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        var done = ((TaskResponse)await client.SendMessageAsync("again", taskId: task.Id)).Task;
        Assert.Equal(TaskState.Completed, done.Status.State);
        var read = await client.GetTaskAsync(task.Id);
        await app.StopAsync();

        var store = new PostgresTaskStore(db.DataSource, new StreamingOptions(), schema);
        var tenant = (await db.DataSource.CreateCommand($"SELECT tenant FROM \"{schema.Schema}\".a2a_tasks").ExecuteScalarAsync() as string)!;
        var fromRows = (await store.GetForMaintenanceAsync(tenant, task.Id))!.Task;
        // go, 2 x 30 status messages, the question, again; the timeline keeps its newest 10.
        Assert.Equal(2 + 2 * StatusAgent.Updates + 1, fromRows.History!.Count);
        Assert.Equal(10, fromRows.Timeline!.Count);
        Assert.Equal(A2AJson.Serialize(read.History), A2AJson.Serialize(fromRows.History));
        // (A 1.0 client gets no timeline; the comparison with the inline copy below covers it.)

        Assert.Equal(1, await store.InlineHistoryAndTimelineAsync());
        Assert.Equal(A2AJson.Serialize(fromRows), A2AJson.Serialize((await store.GetForMaintenanceAsync(tenant, task.Id))!.Task));
    }

    private sealed class StatusAgent : IAgentExecutor
    {
        public const int Updates = 30;

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            for (var i = 0; i < Updates; i++)
            {
                await updater.StartWorkAsync($"step {i} of {context.UserText}", ct);
                // Artifact events in between change neither list.
                if (i % 10 == 5) await updater.AddArtifactAsync($"artifact {i} of {context.UserText}", cancellationToken: ct);
            }

            if (context.UserText == "go") await updater.RequireInputAsync("more?", ct);
            else await updater.CompleteAsync(cancellationToken: ct);
        }
    }
}
