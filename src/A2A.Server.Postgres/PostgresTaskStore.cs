using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using NpgsqlTypes;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="TaskStore"/>. Same behavior as <see cref="InMemoryTaskStore"/>: owner- and tenant-scoped reads,
/// optimistic concurrency on <see cref="TaskRecord.Version"/>, ListTasks newest first with keyset paging.
/// <see cref="CommitAsync"/> writes the task row, the event log (the tables <see cref="PostgresEventStore"/> reads) and the
/// push deliveries (the outbox <see cref="PostgresPushOutbox"/> reads) in one transaction, so give these stores the same
/// data source, <see cref="StreamingOptions"/> and schema.
/// </summary>
public sealed class PostgresTaskStore : TaskStore
{
    private readonly NpgsqlDataSource _db;
    private readonly StreamingOptions _streaming;
    private readonly TimeProvider _time;
    private readonly string _tasks;
    private readonly string _contexts;
    private readonly EventTables _eventTables;
    private readonly string _outbox;
    private readonly string _insertSql;
    private readonly string _updateSql;

    public PostgresTaskStore(NpgsqlDataSource dataSource, StreamingOptions streaming, PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _time = time ?? TimeProvider.System;
        var s = PostgresSchema.Quote((options ?? new()).Schema);
        _tasks = $"{s}.a2a_tasks";
        _contexts = $"{s}.a2a_contexts";
        _eventTables = EventTables.For(options);
        _outbox = PostgresPushOutbox.OutboxTable(options);
        _insertSql =
            $"""
            INSERT INTO {_tasks} (tenant, task_id, owner, context_id, state, status_ticks, version, created_ticks, updated_ticks, task)
            VALUES ($1, $2, $3, $4, $5, $6, 1, $7, $8, $9)
            ON CONFLICT (tenant, task_id) DO NOTHING
            """;
        _updateSql =
            $"""
            UPDATE {_tasks}
               SET context_id = $4, state = $5, status_ticks = COALESCE($6, created_ticks), version = version + 1, updated_ticks = $7, task = $8
             WHERE tenant = $1 AND task_id = $2 AND owner = $3 AND version = $9
            RETURNING created_ticks
            """;
    }

    public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task FROM {_tasks} WHERE tenant = $1 AND task_id = $2 AND owner = $3");
        cmd.Parameters.Add(Text(key.Tenant));
        cmd.Parameters.Add(Text(key.TaskId));
        cmd.Parameters.Add(Text(key.Owner));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader, key.Tenant) : null;
    }

    public override async ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default)
    {
        var (json, stored) = ForCreate(record);
        await using var cmd = _db.CreateCommand(_insertSql);
        AddInsertParameters(cmd, stored, json);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) throw new DuplicateTaskException(record.Key);
        return stored;
    }

    public override async ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        var (json, stored) = ForUpdate(record, expectedVersion);
        await using var cmd = _db.CreateCommand(_updateSql);
        AddUpdateParameters(cmd, stored, json, expectedVersion);
        var created = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (created is not long createdTicks) throw new ConcurrencyException(record.Key, expectedVersion);
        return stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
    }

    /// <summary>
    /// One transaction: the task insert or compare-and-set update (whose row lock serializes commits to the task, so log
    /// order = version order across instances), then the event append with its sequence, then the push deliveries with
    /// that sequence. Any failure rolls back all of it. The final COMMIT is not cancelled: once it is sent, a cancelled
    /// token must not leave the caller unsure whether the event and its deliveries were stored.
    /// </summary>
    public override async ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateDeliveries(commit);
        var key = commit.Record.Key;
        var (json, stored) = commit.IsCreate ? ForCreate(commit.Record) : ForUpdate(commit.Record, commit.ExpectedVersion);
        var eventJson = A2AJson.Serialize(commit.Event);

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (commit.IsCreate)
        {
            await using var insert = new NpgsqlCommand(_insertSql, conn, tx);
            AddInsertParameters(insert, stored, json);
            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) throw new DuplicateTaskException(key);
        }
        else
        {
            await using var update = new NpgsqlCommand(_updateSql, conn, tx);
            AddUpdateParameters(update, stored, json, commit.ExpectedVersion);
            if (await update.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long createdTicks)
                throw new ConcurrencyException(key, commit.ExpectedVersion);
            stored = stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
        }

        var now = _time.GetUtcNow().UtcTicks;
        var seq = await AppendEventAsync(conn, tx, _eventTables, key, eventJson, Math.Max(1, _streaming.ReplayBufferSize), now, ct).ConfigureAwait(false);
        if (commit.Deliveries.Count > 0)
        {
            await using var batch = new NpgsqlBatch(conn, tx);
            foreach (var d in commit.Deliveries) batch.BatchCommands.Add(PostgresPushOutbox.InsertCommand(_outbox, d, seq, now));
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return new CommitResult(stored, seq);
    }

    public override async ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = PageCursor.Decode(query.PageToken);

        var countArgs = new List<NpgsqlParameter>();
        var filter = Filter(query, countArgs);
        var pageArgs = new List<NpgsqlParameter>();
        var pageFilter = Filter(query, pageArgs);
        if (cursor is { } c)
        {
            pageArgs.Add(Long(c.Ticks));
            var t = pageArgs.Count;
            pageArgs.Add(Text(c.Id));
            pageFilter += $" AND (status_ticks < ${t} OR (status_ticks = ${t} AND task_id > ${t + 1}))";
        }
        pageArgs.Add(new NpgsqlParameter<int> { TypedValue = checked(Math.Max(0, query.PageSize) + 1) });

        await using var batch = _db.CreateBatch();
        var count = new NpgsqlBatchCommand($"SELECT count(*) FROM {_tasks} WHERE {filter}");
        foreach (var a in countArgs) count.Parameters.Add(a);
        var page = new NpgsqlBatchCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task FROM {_tasks} WHERE {pageFilter} ORDER BY status_ticks DESC, task_id LIMIT ${pageArgs.Count}");
        foreach (var a in pageArgs) page.Parameters.Add(a);
        batch.BatchCommands.Add(count);
        batch.BatchCommands.Add(page);

        await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var total = checked((int)reader.GetInt64(0));
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var records = new List<TaskRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) records.Add(ReadRecord(reader, query.Tenant));

        var hasMore = records.Count > query.PageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        var next = hasMore && records.Count > 0 ? PageCursor.Encode(records[^1]) : string.Empty;
        return new TaskPage(records, next, total);
    }

    public override async ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT owner, created_ticks FROM {_contexts} WHERE tenant = $1 AND context_id = $2");
        cmd.Parameters.Add(Text(tenant));
        cmd.Parameters.Add(Text(contextId));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new ContextRecord(tenant, reader.GetString(0), contextId, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
            : null;
    }

    public override async ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using (var cmd = _db.CreateCommand(
            $"INSERT INTO {_contexts} (tenant, context_id, owner, created_ticks) VALUES ($1, $2, $3, $4) ON CONFLICT (tenant, context_id) DO NOTHING"))
        {
            cmd.Parameters.Add(Text(record.Tenant));
            cmd.Parameters.Add(Text(record.ContextId));
            cmd.Parameters.Add(Text(record.Owner));
            cmd.Parameters.Add(Long(record.CreatedAt.UtcTicks));
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return record;
        }
        // Lost the race (or the context existed): a new statement sees the committed winner.
        return await GetContextAsync(record.Tenant, record.ContextId, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Context '{record.ContextId}' vanished while it was being created.");
    }

    private static (string Json, TaskRecord Stored) ForCreate(TaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(record));
        return Snapshot(record with { Version = 1 });
    }

    private (string Json, TaskRecord Stored) ForUpdate(TaskRecord record, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Snapshot(record with { Version = expectedVersion + 1, UpdatedAt = _time.GetUtcNow() });
    }

    private static void AddInsertParameters(NpgsqlCommand cmd, TaskRecord stored, string json)
    {
        cmd.Parameters.Add(Text(stored.Tenant));
        cmd.Parameters.Add(Text(stored.Task.Id));
        cmd.Parameters.Add(Text(stored.Owner));
        cmd.Parameters.Add(Text(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        cmd.Parameters.Add(Long(StatusTicks(stored)));
        cmd.Parameters.Add(Long(stored.CreatedAt.UtcTicks));
        cmd.Parameters.Add(Long(stored.UpdatedAt.UtcTicks));
        cmd.Parameters.Add(Text(json));
    }

    private static void AddUpdateParameters(NpgsqlCommand cmd, TaskRecord stored, string json, long expectedVersion)
    {
        cmd.Parameters.Add(Text(stored.Tenant));
        cmd.Parameters.Add(Text(stored.Task.Id));
        cmd.Parameters.Add(Text(stored.Owner));
        cmd.Parameters.Add(Text(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        // Without a status timestamp the sort key falls back to the creation time, which only the database row has.
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)stored.Task.Status.Timestamp?.UtcTicks ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(Long(stored.UpdatedAt.UtcTicks));
        cmd.Parameters.Add(Text(json));
        cmd.Parameters.Add(Long(expectedVersion));
    }

    /// <summary>Serializes the task once and returns both the JSON and a record rebuilt from it, so what is returned matches what is stored.</summary>
    private static (string Json, TaskRecord Stored) Snapshot(TaskRecord record)
    {
        var json = A2AJson.Serialize(record.Task);
        return (json, record with { Task = A2AJson.Deserialize<AgentTask>(json) });
    }

    private static TaskRecord ReadRecord(NpgsqlDataReader reader, string tenant) => new(
        tenant,
        reader.GetString(0),
        A2AJson.Deserialize<AgentTask>(reader.GetString(4)),
        reader.GetInt64(1),
        new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
        new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));

    private static long StatusTicks(TaskRecord r) => (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks;

    /// <summary>The WHERE clause for a query; adds its parameters (positional, $1..) to <paramref name="args"/>.</summary>
    private static string Filter(TaskQuery query, List<NpgsqlParameter> args)
    {
        args.Add(Text(query.Tenant));
        args.Add(Text(query.Owner));
        var filter = "tenant = $1 AND owner = $2";
        if (query.ContextId is not null)
        {
            args.Add(Text(query.ContextId));
            filter += $" AND context_id = ${args.Count}";
        }
        if (query.Status is { } status)
        {
            args.Add(new NpgsqlParameter<int> { TypedValue = (int)status });
            filter += $" AND state = ${args.Count}";
        }
        if (query.StatusTimestampAfter is { } after)
        {
            args.Add(Long(after.UtcTicks));
            filter += $" AND status_ticks >= ${args.Count}";
        }
        return filter;
    }
}
