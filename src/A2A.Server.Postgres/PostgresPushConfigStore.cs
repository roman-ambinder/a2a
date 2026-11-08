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

    /// <summary>Creates the store over <paramref name="dataSource"/>. Create the tables first (<see cref="PostgresSchema"/>).</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema; default <c>public</c>.</param>
    public PostgresPushConfigStore(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _table = $"{PostgresSchema.Quote((options ?? new()).Schema)}.a2a_push_configs";
    }

    /// <inheritdoc />
    public override async ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = record.Config.Id ?? throw new ArgumentException("The config must have an id.", nameof(record));
        await using var cmd = _db.CreateCommand(
            $"""
            INSERT INTO {_table} (tenant, owner, task_id, config_id, config) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (tenant, owner, task_id, config_id) DO UPDATE SET config = EXCLUDED.config
            """);
        AddKey(cmd, record.TaskKey);
        cmd.Parameters.Add(TextParam(id));
        cmd.Parameters.Add(TextParam(A2AJson.Serialize(record.Config)));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(TextParam(configId));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? new PushConfigRecord(task, A2AJson.Deserialize<TaskPushNotificationConfig>(json))
            : null;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT config FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 ORDER BY config_id");
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
        await using var cmd = _db.CreateCommand($"SELECT count(*) FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
        AddKey(cmd, task);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND config_id = $4");
        AddKey(cmd, task);
        cmd.Parameters.Add(TextParam(configId));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static void AddKey(NpgsqlCommand cmd, TaskKey task)
    {
        cmd.Parameters.Add(TextParam(task.Tenant));
        cmd.Parameters.Add(TextParam(task.Owner));
        cmd.Parameters.Add(TextParam(task.TaskId));
    }
}
