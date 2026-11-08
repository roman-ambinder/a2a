using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using NpgsqlTypes;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="TaskStore"/>. Same behavior as <see cref="InMemoryTaskStore"/>: owner- and tenant-scoped reads,
/// optimistic concurrency on <see cref="TaskRecord.Version"/>, ListTasks newest first with keyset paging.
/// </summary>
/// <remarks>
/// Tasks are stored as JSON text. Every read returns a freshly deserialized instance, and a write returns the instance rebuilt
/// from the JSON it stored, so a caller never shares an instance with another caller (the <see cref="TaskStore"/> ownership
/// contract allows this).
/// </remarks>
public sealed class PostgresTaskStore : TaskStore
{
    private readonly NpgsqlDataSource _db;
    private readonly TimeProvider _time;
    private readonly string _tasks;
    private readonly string _contexts;

    /// <summary>Creates the store over <paramref name="dataSource"/>. Create the tables first (<see cref="PostgresSchema"/>).</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    /// <param name="time">The clock for <see cref="TaskRecord.UpdatedAt"/>. Default <see cref="TimeProvider.System"/>.</param>
    public PostgresTaskStore(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _time = time ?? TimeProvider.System;
        var s = PostgresSchema.Quote((options ?? new()).Schema);
        _tasks = $"{s}.a2a_tasks";
        _contexts = $"{s}.a2a_contexts";
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT owner, version, created_ticks, updated_ticks, task FROM {_tasks} WHERE tenant = $1 AND task_id = $2 AND owner = $3");
        cmd.Parameters.Add(TextParam(key.Tenant));
        cmd.Parameters.Add(TextParam(key.TaskId));
        cmd.Parameters.Add(TextParam(key.Owner));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader, key.Tenant) : null;
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(record));
        var (json, stored) = Snapshot(record with { Version = 1 });

        await using var cmd = _db.CreateCommand(
            $"""
            INSERT INTO {_tasks} (tenant, task_id, owner, context_id, state, status_ticks, version, created_ticks, updated_ticks, task)
            VALUES ($1, $2, $3, $4, $5, $6, 1, $7, $8, $9)
            ON CONFLICT (tenant, task_id) DO NOTHING
            """);
        cmd.Parameters.Add(TextParam(stored.Tenant));
        cmd.Parameters.Add(TextParam(stored.Task.Id));
        cmd.Parameters.Add(TextParam(stored.Owner));
        cmd.Parameters.Add(TextParam(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        cmd.Parameters.Add(Long(StatusTicks(stored)));
        cmd.Parameters.Add(Long(stored.CreatedAt.UtcTicks));
        cmd.Parameters.Add(Long(stored.UpdatedAt.UtcTicks));
        cmd.Parameters.Add(TextParam(json));
        if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) throw new DuplicateTaskException(record.Key);
        return stored;
    }

    /// <inheritdoc />
    public override async ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var now = _time.GetUtcNow();
        var (json, stored) = Snapshot(record with { Version = expectedVersion + 1, UpdatedAt = now });

        await using var cmd = _db.CreateCommand(
            $"""
            UPDATE {_tasks}
               SET context_id = $4, state = $5, status_ticks = COALESCE($6, created_ticks), version = version + 1, updated_ticks = $7, task = $8
             WHERE tenant = $1 AND task_id = $2 AND owner = $3 AND version = $9
            RETURNING created_ticks
            """);
        cmd.Parameters.Add(TextParam(stored.Tenant));
        cmd.Parameters.Add(TextParam(stored.Task.Id));
        cmd.Parameters.Add(TextParam(stored.Owner));
        cmd.Parameters.Add(TextParam(stored.Task.ContextId ?? string.Empty));
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = (int)stored.Task.Status.State });
        // Without a status timestamp the sort key falls back to the creation time, which only the database row has.
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)stored.Task.Status.Timestamp?.UtcTicks ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(Long(now.UtcTicks));
        cmd.Parameters.Add(TextParam(json));
        cmd.Parameters.Add(Long(expectedVersion));

        var created = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (created is not long createdTicks) throw new ConcurrencyException(record.Key, expectedVersion);
        return stored with { CreatedAt = new DateTimeOffset(createdTicks, TimeSpan.Zero) };
    }

    /// <inheritdoc />
    public override async ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
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
            pageArgs.Add(TextParam(c.Id));
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

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var total = checked((int)reader.GetInt64(0));
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<TaskRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadRecord(reader, query.Tenant));

        var hasMore = records.Count > query.PageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        var next = hasMore && records.Count > 0 ? PageCursor.Encode(records[^1]) : string.Empty;
        return new TaskPage(records, next, total);
    }

    /// <inheritdoc />
    public override async ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT owner, created_ticks FROM {_contexts} WHERE tenant = $1 AND context_id = $2");
        cmd.Parameters.Add(TextParam(tenant));
        cmd.Parameters.Add(TextParam(contextId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ContextRecord(tenant, reader.GetString(0), contextId, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
            : null;
    }

    /// <inheritdoc />
    public override async ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using (var cmd = _db.CreateCommand(
            $"INSERT INTO {_contexts} (tenant, context_id, owner, created_ticks) VALUES ($1, $2, $3, $4) ON CONFLICT (tenant, context_id) DO NOTHING"))
        {
            cmd.Parameters.Add(TextParam(record.Tenant));
            cmd.Parameters.Add(TextParam(record.ContextId));
            cmd.Parameters.Add(TextParam(record.Owner));
            cmd.Parameters.Add(Long(record.CreatedAt.UtcTicks));
            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return record;
        }
        // Lost the race (or the context existed): a new statement sees the committed winner.
        return await GetContextAsync(record.Tenant, record.ContextId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Context '{record.ContextId}' vanished while it was being created.");
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
        args.Add(TextParam(query.Tenant));
        args.Add(TextParam(query.Owner));
        var filter = "tenant = $1 AND owner = $2";
        if (query.ContextId is not null)
        {
            args.Add(TextParam(query.ContextId));
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
