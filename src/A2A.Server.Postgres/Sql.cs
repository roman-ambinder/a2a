using A2A.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace A2A.Server.Postgres;

/// <summary>Small helpers shared by the stores.</summary>
internal static class Sql
{
    public static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter<long> Long(long value) => new() { TypedValue = value };

    public static NpgsqlParameter NullableLong(long? value) =>
        new() { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

    /// <summary>
    /// Appends an event to a task's log inside <paramref name="tx"/> and returns its sequence: takes the next sequence from
    /// the log row (inserted for the first event; the row lock serializes appends to one task), trims the retained events
    /// to <paramref name="capacity"/> and inserts the event. An expired log starts over at 1, like a fresh in-memory log
    /// after eviction. Used by <see cref="PostgresEventStore.AppendAsync"/> and by <see cref="PostgresTaskStore.CommitAsync"/>.
    /// </summary>
    public static async ValueTask<long> AppendEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, EventTables tables, TaskKey task,
        string eventJson, int capacity, long nowTicks, CancellationToken ct)
    {
        long seq;
        await using (var upsert = new NpgsqlCommand(
            $"""
            INSERT INTO {tables.Logs} AS l (tenant, owner, task_id, last_seq, purge_ticks) VALUES ($1, $2, $3, 1, NULL)
            ON CONFLICT (tenant, owner, task_id) DO UPDATE SET
                last_seq    = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= $4 THEN 1 ELSE l.last_seq + 1 END,
                purge_ticks = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= $4 THEN NULL ELSE l.purge_ticks END
            RETURNING last_seq
            """, conn, tx))
        {
            AddKey(upsert.Parameters, task);
            upsert.Parameters.Add(Long(nowTicks));
            seq = (long)(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await using var batch = new NpgsqlBatch(conn, tx);
        // Trim to the replay buffer. Sequence 1 means a new or restarted log: drop anything left from an expired one.
        var trim = new NpgsqlBatchCommand(
            $"DELETE FROM {tables.Events} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND ($4 = 1 OR seq <= $4 - $5)");
        AddKey(trim.Parameters, task);
        trim.Parameters.Add(Long(seq));
        trim.Parameters.Add(Long(capacity));
        var insert = new NpgsqlBatchCommand($"INSERT INTO {tables.Events} (tenant, owner, task_id, seq, event) VALUES ($1, $2, $3, $4, $5)");
        AddKey(insert.Parameters, task);
        insert.Parameters.Add(Long(seq));
        insert.Parameters.Add(Text(eventJson));
        batch.BatchCommands.Add(trim);
        batch.BatchCommands.Add(insert);
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Adds the task key as the first three positional parameters (tenant, owner, task id).</summary>
    public static void AddKey(NpgsqlParameterCollection parameters, TaskKey task)
    {
        parameters.Add(Text(task.Tenant));
        parameters.Add(Text(task.Owner));
        parameters.Add(Text(task.TaskId));
    }

    /// <summary><paramref name="ticks"/> + <paramref name="span"/>, clamped to <see cref="DateTimeOffset.MaxValue"/> (an "infinite" span never overflows).</summary>
    public static long AddClamped(long ticks, TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return ticks;
        var max = DateTimeOffset.MaxValue.UtcTicks;
        return span.Ticks >= max - ticks ? max : ticks + span.Ticks;
    }
}

/// <summary>The quoted names of the event-log tables in one schema.</summary>
internal sealed record EventTables(string Logs, string Events)
{
    public static EventTables For(PostgresStoreOptions? options)
    {
        var s = PostgresSchema.Quote((options ?? new()).Schema);
        return new($"{s}.a2a_event_logs", $"{s}.a2a_events");
    }
}
