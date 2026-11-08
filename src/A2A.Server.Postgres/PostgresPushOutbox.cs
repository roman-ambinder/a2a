using A2A.Server.Push;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="PushOutbox"/> over <c>a2a_push_outbox</c> and <c>a2a_push_dead_letters</c>. Entries are written
/// by <see cref="PostgresTaskStore.CommitAsync"/> in the commit's transaction, so give both the same data source and schema.
/// Leasing locks the rows it takes (<c>FOR UPDATE SKIP LOCKED</c>) and only takes the lowest sequence of a task and config,
/// so several instances can share one outbox. Times come from the <see cref="TimeProvider"/> (the instances' clocks should agree).
/// </summary>
public sealed class PostgresPushOutbox : PushOutbox
{
    private const string Columns = "tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt, payload";
    private const string KeyFilter = "tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4 AND seq = $5";

    /// <summary>The entry key ($1..$5) and its lease token ($6): only the current lease holder's writes match.</summary>
    private const string LeasedFilter = KeyFilter + " AND lease_token = $6";

    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly string _outbox;
    private readonly string _dead;

    public PostgresPushOutbox(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _time = time ?? TimeProvider.System;
        _outbox = OutboxTable(options);
        _dead = $"{PostgresSchema.Quote((options ?? new()).Schema)}.a2a_push_dead_letters";
    }

    internal static string OutboxTable(PostgresStoreOptions? options) => $"{PostgresSchema.Quote((options ?? new()).Schema)}.a2a_push_outbox";

    /// <summary>
    /// The insert of a new delivery (due now, attempt 0, the event's JSON as <paramref name="payload"/>) for
    /// <see cref="PostgresTaskStore.CommitAsync"/>'s transaction. An existing entry with the same key is kept, never replaced.
    /// </summary>
    internal static NpgsqlBatchCommand InsertCommand(string outboxTable, PushDelivery d, long seq, byte[] payload, long nowTicks)
    {
        var cmd = new NpgsqlBatchCommand(
            $"""
            INSERT INTO {outboxTable} (tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt, payload, due_ticks, lease_ticks, last_error)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 0, $9, $8, NULL, NULL)
            ON CONFLICT (tenant, owner, task_id, config_id, seq) DO NOTHING
            """);
        AddKey(cmd.Parameters, d.Task);
        cmd.Parameters.Add(Text(d.ConfigId));
        cmd.Parameters.Add(Long(seq));
        cmd.Parameters.Add(Text(d.DeliveryId));
        cmd.Parameters.Add(NullableText(d.TraceParent));
        cmd.Parameters.Add(Long(nowTicks));
        cmd.Parameters.Add(Bytes(payload));
        return cmd;
    }

    public override async ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default)
    {
        if (max <= 0) return [];
        var now = _time.GetUtcNow().UtcTicks;
        // The lease and due conditions are on o itself, so a row another leaser just took is re-checked after its lock (or
        // skipped while locked). NOT EXISTS keeps every entry queued behind a lower sequence of its task and config.
        await using var cmd = _db.CreateCommand(
            $"""
            WITH picked AS (
                SELECT o.tenant, o.owner, o.task_id, o.config_id, o.seq FROM {_outbox} o
                WHERE o.due_ticks <= $1 AND (o.lease_ticks IS NULL OR o.lease_ticks <= $1)
                  AND NOT EXISTS (SELECT 1 FROM {_outbox} p
                                  WHERE p.tenant = o.tenant AND p.owner = o.owner AND p.task_id = o.task_id
                                    AND p.config_id = o.config_id AND p.seq < o.seq)
                ORDER BY o.due_ticks, o.seq
                LIMIT $3
                FOR UPDATE OF o SKIP LOCKED
            )
            UPDATE {_outbox} o SET lease_ticks = $2, lease_token = $4 FROM picked p
            WHERE o.tenant = p.tenant AND o.owner = p.owner AND o.task_id = p.task_id AND o.config_id = p.config_id AND o.seq = p.seq
            RETURNING o.tenant, o.owner, o.task_id, o.config_id, o.seq, o.delivery_id, o.trace_parent, o.attempt, o.payload, o.due_ticks, o.last_error, o.lease_token
            """);
        cmd.Parameters.Add(Long(now));
        cmd.Parameters.Add(Long(AddClamped(now, lease)));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = max });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        var list = new List<OutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadEntry(reader));
        return list.OrderBy(e => e.DueAt).ThenBy(e => e.Delivery.Sequence).ToList();
    }

    public override async ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken ct = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand($"DELETE FROM {_outbox} WHERE {LeasedFilter}");
        foreach (var p in parameters) cmd.Parameters.Add(p);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public override async ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_outbox} SET attempt = $7, due_ticks = $8, last_error = $9, lease_ticks = NULL, lease_token = NULL WHERE {LeasedFilter}");
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = attempt });
        cmd.Parameters.Add(Long(dueAt.UtcTicks));
        cmd.Parameters.Add(Text(error));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public override async ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken ct = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand(
            $"""
            WITH moved AS (DELETE FROM {_outbox} WHERE {LeasedFilter} RETURNING {Columns})
            INSERT INTO {_dead} ({Columns}, dead_ticks, last_error)
            SELECT tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt + 1, payload, $7, $8 FROM moved
            ON CONFLICT (tenant, owner, task_id, config_id, seq) DO UPDATE SET
                delivery_id = EXCLUDED.delivery_id, trace_parent = EXCLUDED.trace_parent, attempt = EXCLUDED.attempt,
                payload = EXCLUDED.payload, dead_ticks = EXCLUDED.dead_ticks, last_error = EXCLUDED.last_error
            """);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
        cmd.Parameters.Add(Text(error));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public override async ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var cmd = _db.CreateCommand(
            $"""
            SELECT {Columns}, dead_ticks, last_error FROM {_dead}
            WHERE tenant = $1 ORDER BY dead_ticks, owner, task_id, config_id, seq LIMIT $2
            """);
        cmd.Parameters.Add(Text(tenant));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, max) });
        var list = new List<OutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadEntry(reader));
        return list;
    }

    public override async ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        // An entry with the same key (a task log that restarted at this sequence) is never touched: its lease and token stay,
        // and the dead letter is kept. The NOT EXISTS leaves the dead letter in place; a racing insert of the same key makes
        // the statement fail on the unique key, which rolls the delete back too.
        await using var cmd = _db.CreateCommand(
            $"""
            WITH moved AS (
                DELETE FROM {_dead} WHERE {KeyFilter}
                  AND NOT EXISTS (SELECT 1 FROM {_outbox} o WHERE o.tenant = $1 AND o.owner = $2 AND o.task_id = $3 AND o.config_id = $4 AND o.seq = $5)
                RETURNING {Columns})
            INSERT INTO {_outbox} ({Columns}, due_ticks, lease_ticks, last_error)
            SELECT tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, 0, payload, $6, NULL, NULL FROM moved
            """);
        AddEntryKey(cmd.Parameters, d);
        cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
        try
        {
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public override async ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var batch = _db.CreateBatch();
        foreach (var table in new[] { _outbox, _dead })
        {
            var delete = new NpgsqlBatchCommand($"DELETE FROM {table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
            AddKey(delete.Parameters, task);
            batch.BatchCommands.Add(delete);
        }
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The parameters of <see cref="LeasedFilter"/> for <paramref name="leased"/>; false when it carries no lease.</summary>
    private static bool AddLeasedKey(OutboxEntry leased, out List<NpgsqlParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(leased);
        parameters = [];
        if (leased.LeaseToken is not { } token) return false;
        var d = leased.Delivery;
        parameters.AddRange([Text(d.Task.Tenant), Text(d.Task.Owner), Text(d.Task.TaskId), Text(d.ConfigId), Long(d.Sequence),
            new NpgsqlParameter<Guid> { TypedValue = token }]);
        return true;
    }

    /// <summary>The entry key as $1..$5: tenant, owner, task id, config id, sequence.</summary>
    private static void AddEntryKey(NpgsqlParameterCollection parameters, PushDelivery d)
    {
        AddKey(parameters, d.Task);
        parameters.Add(Text(d.ConfigId));
        parameters.Add(Long(d.Sequence));
    }

    /// <summary>Reads <see cref="Columns"/>, then the due (or dead-letter) ticks, the last error and, when selected, the lease token.</summary>
    private static OutboxEntry ReadEntry(NpgsqlDataReader r)
    {
        var delivery = new PushDelivery(new TaskKey(r.GetString(0), r.GetString(1), r.GetString(2)), r.GetString(3), r.GetInt64(4), r.GetString(5))
        {
            TraceParent = r.IsDBNull(6) ? null : r.GetString(6),
        };
        return new OutboxEntry(delivery, r.GetInt32(7), new DateTimeOffset(r.GetInt64(9), TimeSpan.Zero), r.IsDBNull(10) ? null : r.GetString(10))
        {
            Payload = r.GetFieldValue<byte[]>(8),
            LeaseToken = r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetGuid(11) : null,
        };
    }
}
