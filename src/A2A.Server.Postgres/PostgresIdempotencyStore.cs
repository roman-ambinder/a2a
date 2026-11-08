using System.Buffers;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Idempotency;
using Npgsql;
using NpgsqlTypes;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// PostgreSQL <see cref="IdempotencyStore"/>. Same behavior as <see cref="InMemoryIdempotencyStore"/> for claims, mismatches
/// and retention (a finished entry is kept for <see cref="IdempotencyOptions.Retention"/>), with two differences that come
/// from being shared by several instances:
/// <list type="bullet">
/// <item>A request waiting on a key held elsewhere polls the row every <see cref="PostgresStoreOptions.IdempotencyPollInterval"/>.</item>
/// <item>A running claim older than <see cref="PostgresStoreOptions.IdempotencyClaimTimeout"/> counts as abandoned, so a crashed
/// instance does not hold its keys forever. <see cref="RenewAsync"/> restarts that clock for a live request.</item>
/// </list>
/// There is no <see cref="IdempotencyOptions.MaxEntries"/> cap: rows live until their retention ends.
/// </summary>
public sealed class PostgresIdempotencyStore : IdempotencyStore
{
    private static readonly IdempotencyResult New = new IdempotencyResult.New();
    private static readonly IdempotencyResult Mismatch = new IdempotencyResult.Mismatch();

    private readonly NpgsqlDataSource _db;
    private readonly IdempotencyOptions _options;
    private readonly PostgresStoreOptions _storeOptions;
    private readonly TimeProvider _time;
    private readonly string _table;

    /// <summary>Creates the store over <paramref name="dataSource"/>. Create the tables first (<see cref="PostgresSchema"/>).</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">Retention of finished entries (<see cref="IdempotencyOptions.Retention"/>).</param>
    /// <param name="storeOptions">The schema, claim timeout and poll interval.</param>
    /// <param name="time">The clock for retention and claim timeouts. Default <see cref="TimeProvider.System"/>.</param>
    public PostgresIdempotencyStore(NpgsqlDataSource dataSource, IdempotencyOptions options, PostgresStoreOptions? storeOptions = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _storeOptions = storeOptions ?? new();
        _time = time ?? TimeProvider.System;
        _table = $"{PostgresSchema.Quote(_storeOptions.Schema)}.a2a_idempotency";
    }

    /// <inheritdoc />
    public override async ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestHash);
        while (true)
        {
            var now = _time.GetUtcNow().UtcTicks;
            await using (var insert = _db.CreateCommand(
                $"INSERT INTO {_table} (tenant, principal, key, hash, claim_id, claimed_ticks) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING"))
            {
                AddScope(insert, scope);
                insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = requestHash, NpgsqlDbType = NpgsqlDbType.Bytea });
                insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
                insert.Parameters.Add(Long(now));
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return New;
            }

            var row = await ReadAsync(scope, cancellationToken).ConfigureAwait(false);
            if (row is not { } r) continue; // removed in between: try to claim again

            if (r.Outcome is not null ? r.ExpiresTicks <= now : IsStale(r.ClaimedTicks, now))
            {
                // Expired outcome, or a claim nobody finished in time: drop exactly that entry and claim again.
                await DeleteClaimAsync(scope, r.ClaimId, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!r.Hash.AsSpan().SequenceEqual(requestHash)) return Mismatch;
            if (r.Outcome is not null) return new IdempotencyResult.Completed(OutcomeJson.Read(r.Outcome));
            return new IdempotencyResult.InProgress(WaitAsync(scope, r.ClaimId));
        }
    }

    /// <inheritdoc />
    public override async ValueTask CompleteAsync(IdempotencyScope scope, IdempotencyOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_table} SET outcome = $4, expires_ticks = $5 WHERE tenant = $1 AND principal = $2 AND key = $3 AND outcome IS NULL");
        AddScope(cmd, scope);
        cmd.Parameters.Add(TextParam(OutcomeJson.Write(outcome)));
        cmd.Parameters.Add(Long(AddClamped(_time.GetUtcNow().UtcTicks, _options.Retention)));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask AbandonAsync(IdempotencyScope scope, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND principal = $2 AND key = $3 AND outcome IS NULL");
        AddScope(cmd, scope);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask RenewAsync(IdempotencyScope scope, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_table} SET claimed_ticks = $4 WHERE tenant = $1 AND principal = $2 AND key = $3 AND outcome IS NULL");
        AddScope(cmd, scope);
        cmd.Parameters.Add(Long(_time.GetUtcNow().UtcTicks));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls the claim until it has an outcome (result), disappears or goes stale (canceled: the caller tries again).
    /// </summary>
    private async Task<IdempotencyOutcome> WaitAsync(IdempotencyScope scope, Guid claimId)
    {
        var interval = _storeOptions.IdempotencyPollInterval > TimeSpan.Zero ? _storeOptions.IdempotencyPollInterval : TimeSpan.FromMilliseconds(200);
        while (true)
        {
            await Task.Delay(interval, _time).ConfigureAwait(false);
            var row = await ReadAsync(scope, CancellationToken.None).ConfigureAwait(false);
            if (row is not { } r || r.ClaimId != claimId || (r.Outcome is null && IsStale(r.ClaimedTicks, _time.GetUtcNow().UtcTicks)))
                throw new TaskCanceledException("The idempotent request was abandoned.");
            if (r.Outcome is not null) return OutcomeJson.Read(r.Outcome);
        }
    }

    private bool IsStale(long claimedTicks, long now)
    {
        var timeout = _storeOptions.IdempotencyClaimTimeout;
        return timeout != Timeout.InfiniteTimeSpan && timeout > TimeSpan.Zero && AddClamped(claimedTicks, timeout) <= now;
    }

    private async ValueTask<Row?> ReadAsync(IdempotencyScope scope, CancellationToken cancellationToken)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT hash, claim_id, claimed_ticks, outcome, expires_ticks FROM {_table} WHERE tenant = $1 AND principal = $2 AND key = $3");
        AddScope(cmd, scope);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new Row(
            reader.GetFieldValue<byte[]>(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? long.MaxValue : reader.GetInt64(4));
    }

    private async ValueTask DeleteClaimAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken)
    {
        await using var cmd = _db.CreateCommand($"DELETE FROM {_table} WHERE tenant = $1 AND principal = $2 AND key = $3 AND claim_id = $4");
        AddScope(cmd, scope);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = claimId });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScope(NpgsqlCommand cmd, IdempotencyScope scope)
    {
        cmd.Parameters.Add(TextParam(scope.Tenant));
        cmd.Parameters.Add(TextParam(scope.Principal));
        cmd.Parameters.Add(TextParam(scope.Key));
    }

    private readonly record struct Row(byte[] Hash, Guid ClaimId, long ClaimedTicks, string? Outcome, long ExpiresTicks);
}

/// <summary>JSON form of <see cref="IdempotencyOutcome"/>, written by hand so it needs no reflection.</summary>
internal static class OutcomeJson
{
    public static string Write(IdempotencyOutcome outcome)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            switch (outcome)
            {
                case IdempotencyOutcome.TaskCreated t:
                    w.WriteString("kind", "task");
                    w.WriteString("taskId", t.TaskId);
                    w.WriteNumber("afterSequence", t.AfterSequence);
                    break;
                case IdempotencyOutcome.DirectMessage m:
                    w.WriteString("kind", "message");
                    w.WritePropertyName("message");
                    JsonSerializer.Serialize(w, m.Message, A2AJson.GetTypeInfo<Message>());
                    break;
                default:
                    throw new ArgumentException($"Unknown outcome type {outcome.GetType().Name}.", nameof(outcome));
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static IdempotencyOutcome Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.GetProperty("kind").GetString() switch
        {
            "task" => new IdempotencyOutcome.TaskCreated(root.GetProperty("taskId").GetString()!, root.GetProperty("afterSequence").GetInt64()),
            "message" => new IdempotencyOutcome.DirectMessage(A2AJson.Deserialize<Message>(root.GetProperty("message"))),
            var kind => throw new JsonException($"Unknown idempotency outcome kind '{kind}'."),
        };
    }
}
