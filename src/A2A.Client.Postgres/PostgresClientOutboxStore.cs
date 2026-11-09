using A2A.Client.Outbox;
using Npgsql;
using static A2A.Client.Postgres.Sql;

namespace A2A.Client.Postgres;

/// <summary>
/// PostgreSQL <see cref="ClientOutboxStore"/> over <c>a2a_client_outbox</c>. Create the table first (<see cref="PostgresClientOutboxSchema"/>).
/// Leasing locks the rows it takes (<c>FOR UPDATE SKIP LOCKED</c>), so several dispatchers, in several instances of your app, can share
/// one table: each entry is leased by one of them at a time. Due times, leases and ages are on the database clock (see
/// <see cref="PostgresClientOutboxOptions.UseDatabaseClock"/>), so instances whose clocks disagree do not take each other's leases.
/// Writes after a lease carry the lease token, so a holder whose lease was taken over changes nothing.
/// </summary>
/// <remarks>
/// The request and the result are stored in the table as they are: bodies of messages and answers, which may hold personal data.
/// Credentials are never stored. Protect the table like the data in the messages.
/// To enqueue or acknowledge in your own transaction, use the overloads that take a connection and a transaction.
/// </remarks>
public sealed class PostgresClientOutboxStore : ClientOutboxStore
{
    private const string Open = "status IN (0, 1)";
    private const string Results = "status IN (2, 3)";

    private readonly NpgsqlDataSource _db;
    private readonly DbClock _clock;
    private readonly string _table;

    /// <summary>Creates the store over <paramref name="dataSource"/>.</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema (default <c>public</c>) and the clock choice.</param>
    /// <param name="time">The clock when <see cref="PostgresClientOutboxOptions.UseDatabaseClock"/> is off. Default <see cref="TimeProvider.System"/>.</param>
    public PostgresClientOutboxStore(NpgsqlDataSource dataSource, PostgresClientOutboxOptions? options = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _clock = new DbClock(time, options);
        _table = PostgresClientOutboxSchema.Table(options);
    }

    /// <summary>The columns of an entry, in the order <see cref="ReadEntry"/> reads them, then the age (needs "now" as parameter $1).</summary>
    private static string SelectList(string alias, string inDoubt) =>
        $"{alias}.id, {alias}.peer, {alias}.correlation, {alias}.request, {alias}.status, {alias}.attempts, {inDoubt}, {alias}.remote_task_id, " +
        $"{alias}.remote_context_id, {alias}.remote_state, {alias}.last_error, {alias}.created_ticks, {alias}.due_ticks, {alias}.collected_ticks, {alias}.result, " +
        $"{DbClock.Now(1)} - {alias}.created_ticks AS age_ticks, {alias}.lease_token";

    private NpgsqlCommand Command(string sql, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null) =>
        connection is null ? _db.CreateCommand(sql) : new NpgsqlCommand(sql, connection, transaction);

    /// <inheritdoc />
    public override ValueTask<bool> EnqueueAsync(ClientOutboxMessage message, CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(message, null, null, cancellationToken);

    /// <summary>
    /// Adds the message as part of <paramref name="transaction"/> on <paramref name="connection"/>, which are yours: write your own rows in
    /// the same transaction and commit once. When the transaction rolls back, nothing is sent. Returns false, and keeps the old entry,
    /// when the id exists. The caller commits and disposes both.
    /// </summary>
    public ValueTask<bool> EnqueueAsync(ClientOutboxMessage message, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return EnqueueCoreAsync(message, connection, transaction, cancellationToken);
    }

    private async ValueTask<bool> EnqueueCoreAsync(ClientOutboxMessage message, NpgsqlConnection? connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var cmd = Command(
            $"""
            INSERT INTO {_table} (id, peer, correlation, request, status, attempts, in_doubt, created_ticks, due_ticks)
            VALUES ($1, $2, $3, $4, 0, 0, false, {DbClock.Now(5)}, {DbClock.Now(5)})
            ON CONFLICT (id) DO NOTHING
            """, connection, transaction);
        cmd.Parameters.Add(TextParam(message.Id));
        cmd.Parameters.Add(TextParam(message.Peer));
        cmd.Parameters.Add(NullableText(message.Correlation));
        cmd.Parameters.Add(Bytes(message.Request.ToArray()));
        cmd.Parameters.Add(_clock.NowParam());
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<ClientOutboxEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var cmd = _db.CreateCommand($"SELECT {SelectList("o", "o.in_doubt")} FROM {_table} o WHERE o.id = $2");
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(TextParam(id));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader, withToken: false) : null;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return [];
        // The due and lease conditions are in the locking SELECT, so a row another leaser just took is re-checked after its lock (or
        // skipped while locked). RETURNING shows the new row, so the in-doubt value from before the lease is taken from the CTE.
        await using var cmd = _db.CreateCommand(
            $"""
            WITH picked AS (
                SELECT id, in_doubt AS was_in_doubt FROM {_table}
                WHERE {Open} AND due_ticks <= {DbClock.Now(1)} AND (lease_ticks IS NULL OR lease_ticks <= {DbClock.Now(1)})
                ORDER BY due_ticks, created_ticks, id
                LIMIT $3
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {_table} o SET attempts = o.attempts + 1,
                in_doubt = CASE WHEN o.status = 0 THEN true ELSE o.in_doubt END,
                lease_ticks = {DbClock.Now(1)} + $2, lease_token = $4
            FROM picked p WHERE o.id = p.id
            RETURNING {SelectList("o", "p.was_in_doubt")}
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(lease));
        cmd.Parameters.Add(Int(max));
        cmd.Parameters.Add(Uuid(Guid.NewGuid()));
        var list = new List<ClientOutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(ReadEntry(reader, withToken: true));
        return list.OrderBy(e => e.DueAt).ThenBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public override async ValueTask<bool> RescheduleAsync(ClientOutboxEntry leased, TimeSpan delay, string? error, bool inDoubt, CancellationToken cancellationToken = default)
    {
        if (!Leased(leased, out var token)) return false;
        await using var cmd = _db.CreateCommand(
            $"""
            UPDATE {_table} SET due_ticks = {DbClock.Now(1)} + $2, last_error = $3, in_doubt = $4, lease_ticks = NULL, lease_token = NULL
            WHERE id = $5 AND lease_token = $6 AND {Open}
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(delay));
        cmd.Parameters.Add(NullableText(error));
        cmd.Parameters.Add(Bool(inDoubt));
        cmd.Parameters.Add(TextParam(leased.Id));
        cmd.Parameters.Add(Uuid(token));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> MarkTrackingAsync(ClientOutboxEntry leased, string taskId, string? contextId, TaskState state, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (!Leased(leased, out var token)) return false;
        await using var cmd = _db.CreateCommand(
            $"""
            UPDATE {_table} SET status = 1, remote_task_id = $3, remote_context_id = $4, remote_state = $5, attempts = 0, in_doubt = false,
                last_error = NULL, due_ticks = {DbClock.Now(1)} + $2, lease_ticks = NULL, lease_token = NULL
            WHERE id = $6 AND lease_token = $7 AND {Open}
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(delay));
        cmd.Parameters.Add(TextParam(taskId));
        cmd.Parameters.Add(NullableText(contextId));
        cmd.Parameters.Add(Int((int)state));
        cmd.Parameters.Add(TextParam(leased.Id));
        cmd.Parameters.Add(Uuid(token));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> FinishAsync(ClientOutboxEntry leased, ClientOutboxOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!Leased(leased, out var token)) return false;
        await using var cmd = _db.CreateCommand(
            $"""
            UPDATE {_table} SET status = $1, remote_task_id = COALESCE($2, remote_task_id), remote_context_id = COALESCE($3, remote_context_id),
                remote_state = COALESCE($4, remote_state), result = $5, last_error = $6, lease_ticks = NULL, lease_token = NULL
            WHERE id = $7 AND lease_token = $8 AND {Open}
            """);
        cmd.Parameters.Add(Int((int)outcome.Status));
        cmd.Parameters.Add(NullableText(outcome.RemoteTaskId));
        cmd.Parameters.Add(NullableText(outcome.RemoteContextId));
        cmd.Parameters.Add(NullableInt(outcome.RemoteState is { } s ? (int)s : null));
        cmd.Parameters.Add(NullableBytes(outcome.Result.IsEmpty ? null : outcome.Result.ToArray()));
        cmd.Parameters.Add(NullableText(outcome.Error));
        cmd.Parameters.Add(TextParam(leased.Id));
        cmd.Parameters.Add(Uuid(token));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseResultsAsync(string? correlation, int max, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return [];
        await using var cmd = _db.CreateCommand(
            $"""
            WITH picked AS (
                SELECT id FROM {_table}
                WHERE {Results} AND collected_ticks IS NULL AND (lease_ticks IS NULL OR lease_ticks <= {DbClock.Now(1)})
                  AND ($3::text IS NULL OR correlation = $3::text)
                ORDER BY created_ticks, id
                LIMIT $4
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {_table} o SET lease_ticks = {DbClock.Now(1)} + $2, lease_token = $5
            FROM picked p WHERE o.id = p.id
            RETURNING {SelectList("o", "o.in_doubt")}
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(lease));
        cmd.Parameters.Add(NullableText(correlation));
        cmd.Parameters.Add(Int(max));
        cmd.Parameters.Add(Uuid(Guid.NewGuid()));
        var list = new List<ClientOutboxEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) list.Add(ReadEntry(reader, withToken: true));
        return list.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public override ValueTask<bool> AcknowledgeAsync(ClientOutboxEntry leased, CancellationToken cancellationToken = default) =>
        AcknowledgeCoreAsync(leased, null, null, cancellationToken);

    /// <summary>
    /// Marks the result as collected as part of <paramref name="transaction"/> on <paramref name="connection"/>, which are yours: change
    /// your own state in the same transaction, so a result is handled once for your data. When the transaction rolls back, the result
    /// stays and is returned again after its lease. The caller commits and disposes both.
    /// </summary>
    public ValueTask<bool> AcknowledgeAsync(ClientOutboxEntry leased, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return AcknowledgeCoreAsync(leased, connection, transaction, cancellationToken);
    }

    private async ValueTask<bool> AcknowledgeCoreAsync(ClientOutboxEntry leased, NpgsqlConnection? connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!Leased(leased, out var token)) return false;
        await using var cmd = Command(
            $"""
            UPDATE {_table} SET collected_ticks = {DbClock.Now(1)}, lease_ticks = NULL, lease_token = NULL
            WHERE id = $2 AND lease_token = $3 AND {Results} AND collected_ticks IS NULL
            """, connection, transaction);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(TextParam(leased.Id));
        cmd.Parameters.Add(Uuid(token));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> HastenAsync(string peer, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(taskId);
        await using var cmd = _db.CreateCommand($"UPDATE {_table} SET due_ticks = {DbClock.Now(1)} WHERE peer = $2 AND remote_task_id = $3 AND status = 1");
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(TextParam(peer));
        cmd.Parameters.Add(TextParam(taskId));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask<int> PurgeCollectedAsync(TimeSpan olderThan, int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return 0;
        await using var cmd = _db.CreateCommand(
            $"""
            WITH old AS (
                SELECT id FROM {_table}
                WHERE collected_ticks IS NOT NULL AND collected_ticks <= {DbClock.Now(1)} - $2
                ORDER BY collected_ticks, id
                LIMIT $3
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM {_table} t USING old WHERE t.id = old.id
            """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(olderThan));
        cmd.Parameters.Add(Int(max));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Leased(ClientOutboxEntry leased, out Guid token)
    {
        ArgumentNullException.ThrowIfNull(leased);
        token = leased.LeaseToken ?? Guid.Empty;
        return leased.LeaseToken is not null;
    }

    /// <summary>Reads the columns of <see cref="SelectList"/>.</summary>
    private static ClientOutboxEntry ReadEntry(NpgsqlDataReader r, bool withToken) =>
        new(r.GetString(0), r.GetString(1), r.GetFieldValue<byte[]>(3), (ClientOutboxStatus)r.GetInt32(4))
        {
            Correlation = r.IsDBNull(2) ? null : r.GetString(2),
            Attempts = r.GetInt32(5),
            InDoubt = r.GetBoolean(6),
            RemoteTaskId = r.IsDBNull(7) ? null : r.GetString(7),
            RemoteContextId = r.IsDBNull(8) ? null : r.GetString(8),
            RemoteState = r.IsDBNull(9) ? null : (TaskState)r.GetInt32(9),
            LastError = r.IsDBNull(10) ? null : r.GetString(10),
            CreatedAt = new DateTimeOffset(r.GetInt64(11), TimeSpan.Zero),
            DueAt = new DateTimeOffset(r.GetInt64(12), TimeSpan.Zero),
            CollectedAt = r.IsDBNull(13) ? null : new DateTimeOffset(r.GetInt64(13), TimeSpan.Zero),
            ResultPayload = r.IsDBNull(14) ? null : new ReadOnlyMemory<byte>(r.GetFieldValue<byte[]>(14)),
            Age = TimeSpan.FromTicks(Math.Max(0, r.GetInt64(15))),
            LeaseToken = withToken && !r.IsDBNull(16) ? r.GetGuid(16) : null,
        };
}
