using A2A.Server.Push;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="PushOutbox"/> over <c>a2a_push_outbox</c> and <c>a2a_push_dead_letters</c>. Entries are written
/// by <see cref="PostgresTaskStore.CommitAsync"/> in the commit's transaction, so give both the same data source and schema.
/// Leasing locks the rows it takes (<c>FOR UPDATE SKIP LOCKED</c>) and only takes the lowest sequence of a task and config,
/// so several instances can share one outbox. Due times and leases are on the database clock (see
/// <see cref="PostgresStoreOptions.UseDatabaseClock"/>), so instances whose clocks disagree do not take each other's leases.
/// </summary>
public sealed class PostgresPushOutbox : PushOutbox
{
    private const string Columns = "tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt, payload";
    private const string KeyFilter = "tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4 AND seq = $5";

    /// <summary>The entry key ($1..$5) and its lease token ($6): only the current lease holder's writes match.</summary>
    private const string LeasedFilter = KeyFilter + " AND lease_token = $6";

    private readonly NpgsqlDataSource _db;
    private readonly DbClock _clock;
    private readonly string _outbox;
    private readonly string _dead;

    /// <summary>Creates the outbox over <paramref name="dataSource"/>. Create the tables first (<see cref="PostgresSchema"/>).</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    /// <param name="time">The clock for due times and leases when <see cref="PostgresStoreOptions.UseDatabaseClock"/> is off.
    /// Default <see cref="TimeProvider.System"/>.</param>
    public PostgresPushOutbox(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _clock = new DbClock(time, options);
        _outbox = OutboxTable(options);
        _dead = $"{PostgresSchema.Quote((options ?? new()).Schema)}.a2a_push_dead_letters";
    }

    internal static string OutboxTable(PostgresStoreOptions? options) => $"{PostgresSchema.Quote((options ?? new()).Schema)}.a2a_push_outbox";

    /// <summary>
    /// The insert of a new delivery (due now: <paramref name="nowTicks"/>, or the database clock when null; attempt 0, the event's JSON as <paramref name="payload"/>) for
    /// <see cref="PostgresTaskStore.CommitAsync"/>'s transaction. An existing entry with the same key is kept, never replaced.
    /// </summary>
    internal static NpgsqlBatchCommand InsertCommand(string outboxTable, PushDelivery d, long seq, byte[] payload, long? nowTicks)
    {
        var cmd = new NpgsqlBatchCommand(
            $"""
            INSERT INTO {outboxTable} (tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt, payload, due_ticks, queued_ticks, lease_ticks, last_error)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 0, $9, {DbClock.Now(8)}, {DbClock.Now(8)}, NULL, NULL)
            ON CONFLICT (tenant, owner, task_id, config_id, seq) DO NOTHING
            """);
        AddKey(cmd.Parameters, d.Task);
        cmd.Parameters.Add(TextParam(d.ConfigId));
        cmd.Parameters.Add(Long(seq));
        cmd.Parameters.Add(TextParam(d.DeliveryId));
        cmd.Parameters.Add(NullableText(d.TraceParent));
        cmd.Parameters.Add(NullableLong(nowTicks));
        cmd.Parameters.Add(Bytes(payload));
        return cmd;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> EnqueueAsync(PushDelivery delivery, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // Same insert as a commit's, but skipped when a dead letter has the key. A concurrent insert of the same key is a no-op
        // through ON CONFLICT, so the second of two racing catch-ups returns false.
        await using var cmd = _db.CreateCommand(
            $"""
            INSERT INTO {_outbox} (tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt, payload, due_ticks, queued_ticks, lease_ticks, last_error)
            SELECT $1, $2, $3, $4, $5, $6, $7, 0, $9, {DbClock.Now(8)}, {DbClock.Now(8)}, NULL, NULL
            WHERE NOT EXISTS (SELECT 1 FROM {_dead} x WHERE x.tenant = $1 AND x.owner = $2 AND x.task_id = $3 AND x.config_id = $4 AND x.seq = $5)
            ON CONFLICT (tenant, owner, task_id, config_id, seq) DO NOTHING
            """);
        AddEntryKey(cmd.Parameters, delivery);
        cmd.Parameters.Add(TextParam(delivery.DeliveryId));
        cmd.Parameters.Add(NullableText(delivery.TraceParent));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Bytes(payload.ToArray()));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return [];
        // The lease and due conditions are on o itself, so a row another leaser just took is re-checked after its lock (or
        // skipped while locked). NOT EXISTS keeps every entry queued behind a lower sequence of its task and config.
        await using var cmd = _db.CreateCommand(
            $"""
            WITH picked AS (
                SELECT o.tenant, o.owner, o.task_id, o.config_id, o.seq FROM {_outbox} o
                WHERE o.due_ticks <= {DbClock.Now(1)} AND (o.lease_ticks IS NULL OR o.lease_ticks <= {DbClock.Now(1)})
                  AND NOT EXISTS (SELECT 1 FROM {_outbox} p
                                  WHERE p.tenant = o.tenant AND p.owner = o.owner AND p.task_id = o.task_id
                                    AND p.config_id = o.config_id AND p.seq < o.seq)
                ORDER BY o.due_ticks, o.seq
                LIMIT $3
                FOR UPDATE OF o SKIP LOCKED
            )
            UPDATE {_outbox} o SET lease_ticks = {DbClock.Now(1)} + $2, lease_token = $4 FROM picked p
            WHERE o.tenant = p.tenant AND o.owner = p.owner AND o.task_id = p.task_id AND o.config_id = p.config_id AND o.seq = p.seq
            RETURNING o.tenant, o.owner, o.task_id, o.config_id, o.seq, o.delivery_id, o.trace_parent, o.attempt, o.payload, o.due_ticks, o.last_error, o.lease_token,
                {DbClock.Now(1)} - COALESCE(o.queued_ticks, o.due_ticks) AS age_ticks
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(lease));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = max });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        var list = new List<OutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(ReadEntry(reader));
        return list.OrderBy(e => e.DueAt).ThenBy(e => e.Delivery.Sequence).ToList();
    }

    /// <inheritdoc />
    public override async ValueTask<bool> CompleteAsync(OutboxEntry leased, CancellationToken cancellationToken = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand($"DELETE FROM {_outbox} WHERE {LeasedFilter}");
        foreach (var p in parameters) cmd.Parameters.Add(p);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> RescheduleAsync(OutboxEntry leased, int attempt, DateTimeOffset dueAt, string error, CancellationToken cancellationToken = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_outbox} SET attempt = $7, due_ticks = {DbClock.Now(8)} + $10, last_error = $9, lease_ticks = NULL, lease_token = NULL WHERE {LeasedFilter}");
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = attempt });
        var (due, offset) = _clock.Relative(dueAt);
        cmd.Parameters.Add(NullableLong(due));
        cmd.Parameters.Add(TextParam(error));
        cmd.Parameters.Add(Long(offset));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> DeadLetterAsync(OutboxEntry leased, string error, CancellationToken cancellationToken = default)
    {
        if (!AddLeasedKey(leased, out var parameters)) return false;
        await using var cmd = _db.CreateCommand(
            $"""
            WITH moved AS (DELETE FROM {_outbox} WHERE {LeasedFilter} RETURNING {Columns})
            INSERT INTO {_dead} ({Columns}, dead_ticks, last_error)
            SELECT tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, attempt + 1, payload, {DbClock.Now(7)}, $8 FROM moved
            ON CONFLICT (tenant, owner, task_id, config_id, seq) DO UPDATE SET
                delivery_id = EXCLUDED.delivery_id, trace_parent = EXCLUDED.trace_parent, attempt = EXCLUDED.attempt,
                payload = EXCLUDED.payload, dead_ticks = EXCLUDED.dead_ticks, last_error = EXCLUDED.last_error
            """);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(TextParam(error));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var cmd = _db.CreateCommand(
            $"""
            SELECT {Columns}, dead_ticks, last_error FROM {_dead}
            WHERE tenant = $1 ORDER BY dead_ticks, owner, task_id, config_id, seq LIMIT $2
            """);
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, max) });
        var list = new List<OutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(ReadEntry(reader));
        return list;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken cancellationToken = default)
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
            INSERT INTO {_outbox} ({Columns}, due_ticks, queued_ticks, lease_ticks, last_error)
            SELECT tenant, owner, task_id, config_id, seq, delivery_id, trace_parent, 0, payload, {DbClock.Now(6)}, {DbClock.Now(6)}, NULL, NULL FROM moved
            """);
        AddEntryKey(cmd.Parameters, d);
        cmd.Parameters.Add(_clock.NowParam());
        try
        {
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var batch = _db.CreateBatch();
        foreach (var table in new[] { _outbox, _dead })
        {
            var delete = new NpgsqlBatchCommand($"DELETE FROM {table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
            AddKey(delete.Parameters, task);
            batch.BatchCommands.Add(delete);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The parameters of <see cref="LeasedFilter"/> for <paramref name="leased"/>; false when it carries no lease.</summary>
    private static bool AddLeasedKey(OutboxEntry leased, out List<NpgsqlParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(leased);
        parameters = [];
        if (leased.LeaseToken is not { } token) return false;
        var d = leased.Delivery;
        parameters.AddRange([TextParam(d.Task.Tenant), TextParam(d.Task.Owner), TextParam(d.Task.TaskId), TextParam(d.ConfigId), Long(d.Sequence),
            new NpgsqlParameter<Guid> { TypedValue = token }]);
        return true;
    }

    /// <summary>The entry key as $1..$5: tenant, owner, task id, config id, sequence.</summary>
    private static void AddEntryKey(NpgsqlParameterCollection parameters, PushDelivery d)
    {
        AddKey(parameters, d.Task);
        parameters.Add(TextParam(d.ConfigId));
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
            Age = r.FieldCount > 12 && !r.IsDBNull(12) ? TimeSpan.FromTicks(Math.Max(0, r.GetInt64(12))) : TimeSpan.Zero,
        };
    }
}
