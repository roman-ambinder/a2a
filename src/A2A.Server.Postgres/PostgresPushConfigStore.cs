using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="PushConfigStore"/>. Configs are keyed by tenant, owner, task and config id; a config of another
/// owner looks missing. The handler protects secrets with <see cref="Push.IPushSecretProtector"/> before saving, so the
/// stored JSON never holds plaintext tokens. Use a protector with persisted keys (the default one is per process).
/// </summary>
public sealed class PostgresPushConfigStore : PushConfigStore
{
    private readonly NpgsqlDataSource _db;
    private readonly string _table;
    private readonly string _tasks;

    public PostgresPushConfigStore(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schema = PostgresSchema.Quote((options ?? new()).Schema);
        _table = $"{schema}.a2a_push_configs";
        _tasks = $"{schema}.a2a_tasks";
    }

    public override async ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = record.Config.Id ?? throw new ArgumentException("The config must have an id.", nameof(record));
        await using var cmd = _db.CreateCommand(
            $"""
            INSERT INTO {_table} (tenant, owner, task_id, config_id, config) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (tenant, owner, task_id, config_id) DO UPDATE SET config = EXCLUDED.config
            """);
        AddKey(cmd, record.TaskKey);
        cmd.Parameters.Add(Text(id));
        cmd.Parameters.Add(Text(A2AJson.Serialize(record.Config)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(Text(configId));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? new PushConfigRecord(task, A2AJson.Deserialize<TaskPushNotificationConfig>(json))
            : null;
    }

    public override async ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 ORDER BY config_id");
        AddKey(cmd, task);
        var list = new List<PushConfigRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new PushConfigRecord(task, A2AJson.Deserialize<TaskPushNotificationConfig>(reader.GetString(0))));
        return list;
    }

    public override async ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT count(*) FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
        AddKey(cmd, task);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    public override async ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(Text(configId));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public override async ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
        AddKey(cmd, task);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One transaction: locks the task's row in <c>a2a_tasks</c> (<c>FOR UPDATE</c>), so concurrent savers of one task take
    /// turns, then counts and writes. A task without a row (not created yet, or already deleted) has nothing to lock; savers
    /// of such a task are serialized with a transaction-level advisory lock on the task key instead.
    /// </summary>
    public override async ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = record.Config.Id ?? throw new ArgumentException("The config must have an id.", nameof(record));
        var json = A2AJson.Serialize(record.Config);

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var locker = new NpgsqlCommand(
            $"SELECT 1 FROM {_tasks} WHERE tenant = $1 AND owner = $2 AND task_id = $3 FOR UPDATE", conn, tx))
        {
            AddKey(locker, record.TaskKey);
            if (await locker.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                await using var advisory = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtext('a2a_push_configs:' || $1 || chr(1) || $2 || chr(1) || $3))", conn, tx);
                AddKey(advisory, record.TaskKey);
                await advisory.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        bool exists;
        long count;
        await using (var check = new NpgsqlCommand(
            $"""
            SELECT count(*), coalesce(bool_or(config_id = $4), false) FROM {_table}
             WHERE tenant = $1 AND owner = $2 AND task_id = $3
            """, conn, tx))
        {
            AddKey(check, record.TaskKey);
            check.Parameters.Add(Text(id));
            await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            count = reader.GetInt64(0);
            exists = reader.GetBoolean(1);
        }
        if (!exists && count >= limit) return false; // rolls back (nothing written)

        await using (var save = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} (tenant, owner, task_id, config_id, config) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (tenant, owner, task_id, config_id) DO UPDATE SET config = EXCLUDED.config
            """, conn, tx))
        {
            AddKey(save, record.TaskKey);
            save.Parameters.Add(Text(id));
            save.Parameters.Add(Text(json));
            await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static void AddKey(NpgsqlCommand cmd, TaskKey task)
    {
        cmd.Parameters.Add(Text(task.Tenant));
        cmd.Parameters.Add(Text(task.Owner));
        cmd.Parameters.Add(Text(task.TaskId));
    }
}
