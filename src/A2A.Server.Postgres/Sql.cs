using A2A.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace A2A.Server.Postgres;

/// <summary>Small helpers shared by the stores.</summary>
internal static class Sql
{
    public static NpgsqlParameter<string> TextParam(string value)
    {
        return new NpgsqlParameter<string> { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Text };
    }

    public static NpgsqlParameter<long> Long(long value)
    {
        return new NpgsqlParameter<long> { TypedValue = value };
    }

    public static NpgsqlParameter<byte[]> Bytes(byte[] value)
    {
        return new NpgsqlParameter<byte[]> { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Bytea };
    }

    public static NpgsqlParameter NullableText(string? value)
    {
        return new NpgsqlParameter { Value = value is null ? DBNull.Value : value, NpgsqlDbType = NpgsqlDbType.Text };
    }

    public static NpgsqlParameter NullableLong(long? value)
    {
        return new NpgsqlParameter { Value = value is { } v ? v : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
    }

    /// <summary>
    ///     Appends an event to a task's log inside <paramref name="tx" /> and returns its sequence: takes the next sequence
    ///     from
    ///     the log row (inserted for the first event; the row lock serializes appends to one task), trims the retained events
    ///     to <paramref name="capacity" /> and inserts the event. An expired log starts over at 1, like a fresh in-memory log
    ///     after eviction. Used by <see cref="PostgresEventStore.AppendAsync" /> and by
    ///     <see cref="PostgresTaskStore.CommitAsync" />.
    /// </summary>
    public static async ValueTask<long> AppendEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        EventTables tables, TaskKey task,
        string eventJson, int capacity, long? nowTicks, CancellationToken cancellationToken, string? mutationId = null)
    {
        long seq;
        await using (var upsert = new NpgsqlCommand(
                         $"""
                          INSERT INTO {tables.Logs} AS l (tenant, owner, task_id, last_seq, purge_ticks) VALUES ($1, $2, $3, 1, NULL)
                          ON CONFLICT (tenant, owner, task_id) DO UPDATE SET
                              last_seq    = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= {DbClock.Now(4)} THEN 1 ELSE l.last_seq + 1 END,
                              purge_ticks = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= {DbClock.Now(4)} THEN NULL ELSE l.purge_ticks END
                          RETURNING last_seq
                          """, conn, tx))
        {
            AddKey(upsert.Parameters, task);
            upsert.Parameters.Add(NullableLong(nowTicks));
            seq = (long)(await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await using var batch = new NpgsqlBatch(conn, tx);
        // Trim to the replay buffer. Sequence 1 means a new or restarted log: drop anything left from an expired one.
        var trim = new NpgsqlBatchCommand(
            $"DELETE FROM {tables.Events} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND ($4 = 1 OR seq <= $4 - $5)");
        AddKey(trim.Parameters, task);
        trim.Parameters.Add(Long(seq));
        trim.Parameters.Add(Long(capacity));
        var insert =
            new NpgsqlBatchCommand(
                mutationId is null
                    ? $"INSERT INTO {tables.Events} (tenant, owner, task_id, seq, event) VALUES ($1, $2, $3, $4, $5)"
                    : $"INSERT INTO {tables.Events} (tenant, owner, task_id, seq, event, mutation_id) VALUES ($1, $2, $3, $4, $5, $6)");
        AddKey(insert.Parameters, task);
        insert.Parameters.Add(Long(seq));
        insert.Parameters.Add(TextParam(eventJson));
        if (mutationId is not null) insert.Parameters.Add(TextParam(mutationId));
        batch.BatchCommands.Add(trim);
        batch.BatchCommands.Add(insert);
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Adds the task key as the first three positional parameters (tenant, owner, task id).</summary>
    public static void AddKey(NpgsqlParameterCollection parameters, TaskKey task)
    {
        parameters.Add(TextParam(task.Tenant));
        parameters.Add(TextParam(task.Owner));
        parameters.Add(TextParam(task.TaskId));
    }

    /// <summary>
    ///     A span as ticks for SQL arithmetic: zero for a zero or negative span (<see cref="Timeout.InfiniteTimeSpan" /> too,
    ///     like
    ///     <see cref="AddClamped" />), at most 100 years so adding it to a timestamp never overflows.
    /// </summary>
    public static NpgsqlParameter<long> Span(TimeSpan span)
    {
        return Long(span <= TimeSpan.Zero ? 0 : Math.Min(span.Ticks, TimeSpan.FromDays(36525).Ticks));
    }

    /// <summary>
    ///     <paramref name="ticks" /> + <paramref name="span" />, clamped to <see cref="DateTimeOffset.MaxValue" /> (an
    ///     "infinite" span never overflows).
    /// </summary>
    public static long AddClamped(long ticks, TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return ticks;
        var max = DateTimeOffset.MaxValue.UtcTicks;
        return span.Ticks >= max - ticks ? max : ticks + span.Ticks;
    }
}

/// <summary>
///     The clock that decides deadlines (due times, leases, claim timeouts, retention ends). With
///     <see cref="PostgresStoreOptions.UseDatabaseClock" /> (the default) it is the database's clock, so instances whose
///     clocks
///     disagree still agree on what has expired: a store passes a NULL "now" parameter and the SQL uses <see cref="Now" />
///     , which
///     falls back to <c>statement_timestamp()</c>. Otherwise the store passes the <see cref="TimeProvider" />'s ticks.
/// </summary>
internal sealed class DbClock(TimeProvider? time, PostgresStoreOptions? options)
{
    /// <summary>The database's time as .NET UTC ticks (microsecond precision).</summary>
    public const string NowTicksSql =
        "((extract(epoch from statement_timestamp()) * 10000000)::bigint + 621355968000000000)";

    public TimeProvider Time { get; } = time ?? TimeProvider.System;

    public bool UsesDatabase { get; } = (options ?? new PostgresStoreOptions()).UseDatabaseClock;

    /// <summary>The value of a "now" parameter: null (the database decides) or the instance's ticks.</summary>
    public long? NowTicks => UsesDatabase ? null : Time.GetUtcNow().UtcTicks;

    /// <summary>A "now" parameter for <see cref="Now" />.</summary>
    public NpgsqlParameter NowParam()
    {
        return Sql.NullableLong(NowTicks);
    }

    /// <summary>
    ///     SQL for "now" in ticks: positional parameter <paramref name="parameter" />, or the database clock when it is
    ///     NULL.
    /// </summary>
    public static string Now(int parameter)
    {
        return $"COALESCE(${parameter}::bigint, {NowTicksSql})";
    }

    /// <summary>
    ///     An absolute time computed on the instance's clock (<paramref name="at" />) as a "now" parameter value and an offset
    ///     in
    ///     ticks: on the database clock, the time left until it (never negative) is added to the database's now.
    /// </summary>
    public (long? Now, long Offset) Relative(DateTimeOffset at)
    {
        return UsesDatabase ? (null, Math.Max(0, (at - Time.GetUtcNow()).Ticks)) : (at.UtcTicks, 0);
    }
}

/// <summary>The quoted names of the event-log tables in one schema.</summary>
internal sealed record EventTables(string Logs, string Events)
{
    public static EventTables For(PostgresStoreOptions? options)
    {
        var s = PostgresSchema.Quote((options ?? new PostgresStoreOptions()).Schema);
        return new EventTables($"{s}.a2a_event_logs", $"{s}.a2a_events");
    }
}