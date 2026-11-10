using A2A.Json;
using A2A.Server.Storage;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
///     PostgreSQL <see cref="PushConfigStore" />. Configs are keyed by tenant, owner, task and config id; a config of
///     another
///     owner looks missing. The handler protects secrets with <see cref="Push.IPushSecretProtector" /> before saving, so
///     the
///     stored JSON never holds plaintext tokens. Use a protector with persisted keys (the default one is per process).
/// </summary>
public sealed class PostgresPushConfigStore : PushConfigStore
{
    private readonly NpgsqlDataSource _db;
    private readonly string _table;
    private readonly string _upsertSql;

    /// <summary>
    ///     Creates the store over <paramref name="dataSource" />. Create the tables first (<see cref="PostgresSchema" />
    ///     ).
    /// </summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    public PostgresPushConfigStore(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schema = PostgresSchema.Quote((options ?? new PostgresStoreOptions()).Schema);
        _table = $"{schema}.a2a_push_configs";
        _upsertSql =
            $"""
             INSERT INTO {_table} (tenant, owner, task_id, config_id, config) VALUES ($1, $2, $3, $4, $5)
             ON CONFLICT (tenant, owner, task_id, config_id) DO UPDATE SET config = EXCLUDED.config
             """;
    }

    /// <inheritdoc />
    public override async ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = record.Config.Id ?? throw new ArgumentException("The config must have an id.", nameof(record));
        await using var cmd = _db.CreateCommand(_upsertSql);
        AddKey(cmd, record.TaskKey);
        cmd.Parameters.Add(TextParam(id));
        cmd.Parameters.Add(TextParam(A2AJson.Serialize(record.Config)));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand(
                $"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(TextParam(configId));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? new PushConfigRecord(task, A2AJson.Deserialize<TaskPushNotificationConfig>(json))
            : null;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task,
        CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand(
                $"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 ORDER BY config_id");
        AddKey(cmd, task);
        var list = new List<PushConfigRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new PushConfigRecord(task, A2AJson.Deserialize<TaskPushNotificationConfig>(reader.GetString(0))));
        return list;
    }

    /// <inheritdoc />
    public override async ValueTask<int> CountAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand($"SELECT count(*) FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
        AddKey(cmd, task);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> DeleteAsync(TaskKey task, string configId,
        CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand(
                $"DELETE FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(TextParam(configId));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd =
            _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
        AddKey(cmd, task);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     One transaction: takes a transaction-level advisory lock on the task key (tenant, owner, task id), so concurrent
    ///     savers
    ///     of one task take turns, then counts and writes. It does not depend on the task row, and the task's commits do not
    ///     wait on it.
    /// </summary>
    public override async ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = record.Config.Id ?? throw new ArgumentException("The config must have an id.", nameof(record));
        var json = A2AJson.Serialize(record.Config);

        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Always the same lock, whether or not the task row exists (so there is no window where two savers use different
        // locks), and one that task commits and updates do not wait on. Released when the transaction ends.
        await using (var advisory = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtext('a2a_push_configs:' || $1 || chr(1) || $2 || chr(1) || $3))",
                         conn, tx))
        {
            AddKey(advisory, record.TaskKey);
            await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            check.Parameters.Add(TextParam(id));
            await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            count = reader.GetInt64(0);
            exists = reader.GetBoolean(1);
        }

        if (!exists && count >= limit) return false; // rolls back (nothing written)

        await using (var save = new NpgsqlCommand(_upsertSql, conn, tx))
        {
            AddKey(save, record.TaskKey);
            save.Parameters.Add(TextParam(id));
            save.Parameters.Add(TextParam(json));
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static void AddKey(NpgsqlCommand cmd, TaskKey task)
    {
        cmd.Parameters.Add(TextParam(task.Tenant));
        cmd.Parameters.Add(TextParam(task.Owner));
        cmd.Parameters.Add(TextParam(task.TaskId));
    }
}