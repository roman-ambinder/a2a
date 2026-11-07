using System.Runtime.CompilerServices;
using A2A.Json;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="EventStore"/>. Same behavior as <see cref="InMemoryEventStore"/>: per-task sequences from 1, the
/// last <see cref="StreamingOptions.ReplayBufferSize"/> events retained, and a terminal task's log dropped
/// <see cref="StreamingOptions.TerminalRetention"/> after <see cref="MarkTerminalAsync"/>. A dropped log reads as empty
/// (sequence 0) at once; the rows are deleted later by <see cref="PostgresMaintenance"/>.
/// </summary>
public sealed class PostgresEventStore : EventStore
{
    private readonly NpgsqlDataSource _db;
    private readonly StreamingOptions _options;
    private readonly TimeProvider _time;
    private readonly string _logs;
    private readonly string _events;

    public PostgresEventStore(NpgsqlDataSource dataSource, StreamingOptions options, PostgresStoreOptions? storeOptions = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        var s = PostgresSchema.Quote((storeOptions ?? new()).Schema);
        _logs = $"{s}.a2a_event_logs";
        _events = $"{s}.a2a_events";
    }

    public override async ValueTask<long> AppendAsync(TaskKey task, StreamResponse evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var json = A2AJson.Serialize(evt);
        var capacity = Math.Max(1, _options.ReplayBufferSize);
        var now = _time.GetUtcNow().UtcTicks;

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The upsert locks the log row, so appends to one task are serialized. An expired log starts over at 1, like a
        // fresh in-memory log after eviction.
        long seq;
        await using (var upsert = new NpgsqlCommand(
            $"""
            INSERT INTO {_logs} AS l (tenant, owner, task_id, last_seq, purge_ticks) VALUES ($1, $2, $3, 1, NULL)
            ON CONFLICT (tenant, owner, task_id) DO UPDATE SET
                last_seq    = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= $4 THEN 1 ELSE l.last_seq + 1 END,
                purge_ticks = CASE WHEN l.purge_ticks IS NOT NULL AND l.purge_ticks <= $4 THEN NULL ELSE l.purge_ticks END
            RETURNING last_seq
            """, conn, tx))
        {
            AddKey(upsert, task);
            upsert.Parameters.Add(Long(now));
            seq = (long)(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await using (var batch = new NpgsqlBatch(conn, tx))
        {
            // Trim to the replay buffer. Sequence 1 means a new or restarted log: drop anything left from an expired one.
            var trim = new NpgsqlBatchCommand(
                $"DELETE FROM {_events} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND ($4 = 1 OR seq <= $4 - $5)");
            AddKey(trim, task);
            trim.Parameters.Add(Long(seq));
            trim.Parameters.Add(Long(capacity));
            var insert = new NpgsqlBatchCommand($"INSERT INTO {_events} (tenant, owner, task_id, seq, event) VALUES ($1, $2, $3, $4, $5)");
            AddKey(insert, task);
            insert.Parameters.Add(Long(seq));
            insert.Parameters.Add(Text(json));
            batch.BatchCommands.Add(trim);
            batch.BatchCommands.Add(insert);
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return seq;
    }

    public override async IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Read the retained events (at most ReplayBufferSize) in one go, so a slow consumer does not hold a connection.
        var snapshot = new List<SequencedEvent>();
        await using (var cmd = _db.CreateCommand(
            $"""
            SELECT e.seq, e.event FROM {_events} e
              JOIN {_logs} l ON l.tenant = e.tenant AND l.owner = e.owner AND l.task_id = e.task_id
             WHERE e.tenant = $1 AND e.owner = $2 AND e.task_id = $3 AND e.seq > $4
               AND (l.purge_ticks IS NULL OR l.purge_ticks > $5)
             ORDER BY e.seq
            """))
        {
            AddKey(cmd, task);
            cmd.Parameters.Add(Long(afterSequence));
            cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                snapshot.Add(new SequencedEvent(reader.GetInt64(0), A2AJson.Deserialize<StreamResponse>(reader.GetString(1))));
        }

        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    public override async ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"""
            SELECT min(e.seq) FROM {_events} e
              JOIN {_logs} l ON l.tenant = e.tenant AND l.owner = e.owner AND l.task_id = e.task_id
             WHERE e.tenant = $1 AND e.owner = $2 AND e.task_id = $3 AND (l.purge_ticks IS NULL OR l.purge_ticks > $4)
            """);
        AddKey(cmd, task);
        cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long oldest ? oldest : null;
    }

    public override async ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT last_seq FROM {_logs} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND (purge_ticks IS NULL OR purge_ticks > $4)");
        AddKey(cmd, task);
        cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long last ? last : 0;
    }

    public override async ValueTask MarkTerminalAsync(TaskKey task, CancellationToken ct = default)
    {
        // Like the in-memory store: only an existing log, and only the first call sets the deadline.
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_logs} SET purge_ticks = $4 WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND purge_ticks IS NULL");
        AddKey(cmd, task);
        cmd.Parameters.Add(Long(AddClamped(_time.GetUtcNow().UtcTicks, _options.TerminalRetention)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddKey(NpgsqlCommand cmd, TaskKey task)
    {
        cmd.Parameters.Add(Text(task.Tenant));
        cmd.Parameters.Add(Text(task.Owner));
        cmd.Parameters.Add(Text(task.TaskId));
    }

    private static void AddKey(NpgsqlBatchCommand cmd, TaskKey task)
    {
        cmd.Parameters.Add(Text(task.Tenant));
        cmd.Parameters.Add(Text(task.Owner));
        cmd.Parameters.Add(Text(task.TaskId));
    }
}
