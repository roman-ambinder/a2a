using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
///     A task's <see cref="AgentTask.History" /> messages and <see cref="AgentTask.Timeline" /> entries in rows of
///     <c>a2a_task_entries</c>, one per row (<see cref="PostgresStoreOptions.HistoryRows" />, migration 0008, ADR-005). A
///     task with <c>entries_layout = 1</c> is stored with <c>"history": []</c> and <c>"timeline": []</c> in its JSON (each
///     only when the task has the list), and the items are the rows of that list in <c>ordinal</c> order. The ordinals of a
///     list are contiguous: an append takes the next ones, and the timeline cap deletes the lowest. So a status update
///     inserts its message and its timeline entry, and deletes the oldest timeline entry once the cap is reached.
/// </summary>
internal sealed class PostgresEntryRows(string entries, string tasks)
{
    /// <summary>The <c>entries_layout</c> of a task whose history and timeline are in rows.</summary>
    public const short RowsLayout = 1;

    /// <summary>The <c>entries_layout</c> of a task whose history and timeline are in its JSON, as before migration 0008.</summary>
    public const short InlineLayout = 0;

    /// <summary>The <c>list</c> of a history message.</summary>
    public const short HistoryList = 0;

    /// <summary>The <c>list</c> of a timeline entry.</summary>
    public const short TimelineList = 1;

    /// <summary>The quoted name of the table.</summary>
    public string Table => entries;

    /// <summary>A list's rows: the lowest and highest ordinal. Contiguous, so it has <c>Max - Min + 1</c> rows.</summary>
    public readonly record struct Range(long Min, long Max)
    {
        public long Count => Max - Min + 1;
    }

    /// <summary>What a task's rows hold: its layout, and the range of each list (null when the list has no rows).</summary>
    public sealed record Stored(short Layout, Range? History, Range? Timeline);

    /// <summary>
    ///     The history and timeline of a committed task, read once (<see cref="AgentTask.History" /> and
    ///     <see cref="AgentTask.Timeline" />). A2A.Protocol has no accessor that counts them or reads their newest items without
    ///     building the lists, as <see cref="ModelCopies.CountParts" /> does for parts, so the store builds them: O(n)
    ///     references in memory, not O(n) bytes written.
    /// </summary>
    public readonly record struct Lists(List<Message>? History, List<TimelineEntry>? Timeline);

    /// <summary>
    ///     Reads the task's layout and the range of each list, in the commit's transaction, before its compare-and-set. One
    ///     statement, so the layout and the ranges come from the same snapshot. Each range is a <c>min</c> and a <c>max</c>
    ///     over the primary key, which PostgreSQL answers from the index ends: O(log n), not a count of the rows. Every
    ///     write that changes the layout or the rows also changes the version (the commits,
    ///     <see cref="PostgresTaskStore.UpdateAsync" /> and <see cref="PostgresTaskStore.InlineHistoryAndTimelineAsync" />),
    ///     so when a write commits after the read the commit is based on, the compare-and-set fails and this result is unused.
    /// </summary>
    public async ValueTask<Stored> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TaskKey key,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT t.entries_layout,
                    (SELECT min(e.ordinal) FROM {entries} e WHERE e.tenant = $1 AND e.task_id = $2 AND e.list = {HistoryList}),
                    (SELECT max(e.ordinal) FROM {entries} e WHERE e.tenant = $1 AND e.task_id = $2 AND e.list = {HistoryList}),
                    (SELECT min(e.ordinal) FROM {entries} e WHERE e.tenant = $1 AND e.task_id = $2 AND e.list = {TimelineList}),
                    (SELECT max(e.ordinal) FROM {entries} e WHERE e.tenant = $1 AND e.task_id = $2 AND e.list = {TimelineList})
               FROM {tasks} t
              WHERE t.tenant = $1 AND t.task_id = $2
             """, conn, tx);
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new Stored(InlineLayout, null, null);
        Range? Read(int min) => reader.IsDBNull(min) ? null : new Range(reader.GetInt64(min), reader.GetInt64(min + 1));
        return new Stored(reader.GetInt16(0), Read(1), Read(3));
    }

    /// <summary>
    ///     A copy of <paramref name="task" /> for the task row: every property but <c>History</c> and <c>Timeline</c>
    ///     shared, and each of those an empty list when the task has it (null when not), so a read can tell the two apart.
    ///     With <paramref name="withoutParts" />, the artifacts have no parts either (<see cref="PostgresArtifactRows" />).
    ///     It is built field by field, not with <see cref="ModelCopies.CopyForUpdate" />, which would copy the lists once more.
    /// </summary>
    public static AgentTask ForRow(AgentTask task, Lists lists, bool withoutParts)
    {
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = task.Status,
            Artifacts = withoutParts && task.Artifacts is { } artifacts
                ? artifacts.ConvertAll(PostgresArtifactRows.WithoutParts)
                : task.Artifacts,
            History = lists.History is null ? null : [],
            Metadata = task.Metadata,
            Generation = task.Generation,
            Timeline = lists.Timeline is null ? null : []
        };
    }

    /// <summary>
    ///     The stored copy a commit returns: <paramref name="shell" /> (a <see cref="ModelCopies.CopyForUpdate" /> of the
    ///     committed task, taken before its lists were read, so it shares them in O(1)) with every other property from
    ///     <paramref name="parsed" />, the task row as written and read back.
    /// </summary>
    public static AgentTask Restore(AgentTask shell, AgentTask parsed)
    {
        shell.Id = parsed.Id;
        shell.ContextId = parsed.ContextId;
        shell.Status = parsed.Status;
        shell.Artifacts = parsed.Artifacts;
        shell.Metadata = parsed.Metadata;
        shell.Generation = parsed.Generation;
        return shell;
    }

    /// <summary>
    ///     Brings the rows of the task in line with <paramref name="lists" />, after its compare-and-set, in the same
    ///     transaction. <paramref name="events" /> are the commit's events, in order.
    /// </summary>
    /// <remarks>
    ///     Per list, the append path runs when the task is in rows, the events add <c>k</c> items to the list (a status
    ///     update its message, if any, and one timeline entry; a message update its message and one timeline entry), the
    ///     newest <c>k</c> items of the committed list are those items, and the count fits. For the history it must be the
    ///     stored count plus <c>k</c>; for the timeline it may be less, by the oldest entries the cap dropped. That path
    ///     inserts the <c>k</c> rows after the highest ordinal and deletes the rows below the kept range. A list the events
    ///     do not add to, whose count did not change, is left alone: only status and message updates change these lists.
    ///     Anything else rewrites the list: a task stored inline (or by an instance with the option off), and a commit that
    ///     does not match its rows.
    /// </remarks>
    public async ValueTask WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TaskKey key, Lists lists,
        Stored? stored, IEnumerable<StreamResponse> events, CancellationToken cancellationToken)
    {
        var addedHistory = new List<string>();
        var addedTimeline = new List<string>();
        foreach (var evt in events) Added(evt, addedHistory, addedTimeline);

        await using var batch = new NpgsqlBatch(conn, tx);
        if (stored is not { Layout: RowsLayout })
        {
            // A create, or a task stored inline (or written by an instance with the option off): start over.
            batch.BatchCommands.Add(Delete(key, null, null));
            if (lists.History is { Count: > 0 } history)
                batch.BatchCommands.Add(Insert(key, HistoryList, 0, history.ConvertAll(A2AJson.Serialize)));
            if (lists.Timeline is { Count: > 0 } timeline)
                batch.BatchCommands.Add(Insert(key, TimelineList, 0, timeline.ConvertAll(A2AJson.Serialize)));
        }
        else
        {
            Write(batch, key, HistoryList, lists.History, stored.History, addedHistory, A2AJson.Serialize, Kind,
                false);
            Write(batch, key, TimelineList, lists.Timeline, stored.Timeline, addedTimeline, A2AJson.Serialize, Kind,
                true);
        }

        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Write<T>(NpgsqlBatch batch, TaskKey key, short list, List<T>? items, Range? have,
        List<string> added, Func<T, string> rowJson, Func<T, string> kind, bool mayDropOldest)
    {
        var count = items?.Count ?? 0;
        var storedCount = have?.Count ?? 0;
        var k = added.Count;
        if (k == 0 && count == storedCount) return; // untouched
        if (k > 0 && count >= k && (mayDropOldest ? count <= storedCount + k : count == storedCount + k)
            && EndsWith(items!, added, kind))
        {
            var next = (have?.Max ?? -1) + 1;
            batch.BatchCommands.Add(Insert(key, list, next, items!.GetRange(count - k, k).ConvertAll(i => rowJson(i))));
            var newMax = next + k - 1;
            // Keep the newest `count` ordinals: the cap dropped the oldest entries from the task.
            if (storedCount + k > count) batch.BatchCommands.Add(Delete(key, list, newMax - count));
            return;
        }

        batch.BatchCommands.Add(Delete(key, list, null));
        if (count > 0) batch.BatchCommands.Add(Insert(key, list, 0, items!.ConvertAll(i => rowJson(i))));
    }

    /// <summary>True when the newest items of <paramref name="items" /> are what the events added, by <paramref name="kind" />.</summary>
    private static bool EndsWith<T>(List<T> items, List<string> added, Func<T, string> kind)
    {
        var start = items.Count - added.Count;
        for (var i = 0; i < added.Count; i++)
            if (!string.Equals(kind(items[start + i]), added[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>A history message, as <see cref="Added" /> describes one.</summary>
    private static string Kind(Message message) => "m:" + A2AJson.Serialize(message);

    /// <summary>A timeline entry, as <see cref="Added" /> describes one: its status or its message.</summary>
    private static string Kind(TimelineEntry entry) =>
        entry.Status is { } status ? "s:" + A2AJson.Serialize(status)
        : entry.Message is { } message ? "m:" + A2AJson.Serialize(message)
        : "?";

    /// <summary>
    ///     What one event adds to the history and the timeline, as the engine applies it: a status update adds its message
    ///     (if any) to the history and its status to the timeline; a message update adds its message to both. Other events
    ///     add nothing.
    /// </summary>
    private static void Added(StreamResponse evt, List<string> history, List<string> timeline)
    {
        switch (evt)
        {
            case StatusUpdateEvent { StatusUpdate.Status: var status }:
                if (status.Message is { } m) history.Add(Kind(m));
                timeline.Add("s:" + A2AJson.Serialize(status));
                break;
            case MessageUpdateEvent { MessageUpdate.Message: var message }:
                history.Add(Kind(message));
                timeline.Add(Kind(message));
                break;
        }
    }

    /// <summary>
    ///     Select-list expressions for a task row aliased <paramref name="alias" />: null for a task stored inline, else the
    ///     <c>entry</c> of each row of the list as a <c>text[]</c>, in ordinal order. They are part of the statement that
    ///     reads the task row, so they read the rows from the same snapshot: the entries always belong to the version read.
    ///     Pass the values to <see cref="Fill" />.
    /// </summary>
    public string Column(string alias, short list) =>
        $"CASE WHEN {alias}.entries_layout = {RowsLayout} THEN ARRAY(SELECT e.entry FROM {entries} e WHERE e.tenant = {alias}.tenant AND e.task_id = {alias}.task_id AND e.list = {list} ORDER BY e.ordinal) END";

    /// <summary>
    ///     Puts the rows (the values of <see cref="Column" />) back into <paramref name="task" />, parsed from a task row
    ///     with empty lists. Null, for a task stored inline, leaves it as it is. A list the task row does not have stays null.
    /// </summary>
    public static void Fill(AgentTask task, string[]? history, string[]? timeline)
    {
        if (history is not null && task.History is not null) task.History = Parse<Message>(history);
        if (timeline is not null && task.Timeline is not null) task.Timeline = Parse<TimelineEntry>(timeline);
    }

    private static List<T> Parse<T>(string[] rows)
    {
        var list = new List<T>(rows.Length);
        foreach (var json in rows) list.Add(A2AJson.Deserialize<T>(json));
        return list;
    }

    /// <summary>
    ///     The rows of the task, read in <paramref name="tx" />, as for <see cref="Fill" />. The caller holds the task row lock
    ///     (<c>FOR UPDATE</c>), which every write of the rows takes first, so the rows are those of the locked version.
    /// </summary>
    public async ValueTask<(string[] History, string[] Timeline)> ReadEntriesAsync(NpgsqlConnection conn,
        NpgsqlTransaction tx, string tenant, string taskId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT list, entry FROM {entries} WHERE tenant = $1 AND task_id = $2 ORDER BY list, ordinal", conn, tx);
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(taskId));
        var history = new List<string>();
        var timeline = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            (reader.GetInt16(0) == HistoryList ? history : timeline).Add(reader.GetString(1));
        return (history.ToArray(), timeline.ToArray());
    }

    /// <summary>One statement for any number of rows: <paramref name="rows" /> get the ordinals from <paramref name="first" />.</summary>
    private NpgsqlBatchCommand Insert(TaskKey key, short list, long first, List<string> rows)
    {
        var cmd = new NpgsqlBatchCommand(
            $"INSERT INTO {entries} (tenant, task_id, list, ordinal, entry) SELECT $1, $2, $3, $4 + u.o - 1, u.e FROM unnest($5::text[]) WITH ORDINALITY AS u(e, o)");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = list });
        cmd.Parameters.Add(Long(first));
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = rows.ToArray() });
        return cmd;
    }

    /// <summary>Deletes the task's rows: all, those of <paramref name="list" />, or those of it up to <paramref name="upTo" />.</summary>
    private NpgsqlBatchCommand Delete(TaskKey key, short? list, long? upTo)
    {
        var sql = $"DELETE FROM {entries} WHERE tenant = $1 AND task_id = $2";
        if (list is not null) sql += " AND list = $3";
        if (upTo is not null) sql += " AND ordinal <= $4";
        var cmd = new NpgsqlBatchCommand(sql);
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        if (list is { } l) cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = l });
        if (upTo is { } u) cmd.Parameters.Add(Long(u));
        return cmd;
    }
}
