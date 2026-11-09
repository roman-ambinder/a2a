using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
/// <remarks>
/// The payload stored for an event is its encoded bytes (<see cref="SequencedEvent.Json"/>, the server encodes each event
/// once), kept as <c>text</c> and returned unchanged: replays write them to the wire as they are. Reads return events
/// rebuilt from those bytes, with <see cref="SequencedEvent.Json"/> set.
/// </remarks>
public sealed class PostgresEventStore : EventStore
{
    private readonly NpgsqlDataSource _db;
    private readonly StreamingOptions _options;
    private readonly DbClock _clock;
    private readonly EventTables _tables;
    private readonly string _logs;
    private readonly string _events;

    /// <summary>Creates the store over <paramref name="dataSource"/>. Create the tables first (<see cref="PostgresSchema"/>).</summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">Retention: <see cref="StreamingOptions.ReplayBufferSize"/> and <see cref="StreamingOptions.TerminalRetention"/>.</param>
    /// <param name="storeOptions">The schema; default <c>public</c>.</param>
    /// <param name="time">The clock for retention when <see cref="PostgresStoreOptions.UseDatabaseClock"/> is off. Default <see cref="TimeProvider.System"/>.</param>
    public PostgresEventStore(NpgsqlDataSource dataSource, StreamingOptions options, PostgresStoreOptions? storeOptions = null, TimeProvider? time = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = new DbClock(time, storeOptions);
        _tables = EventTables.For(storeOptions);
        _logs = _tables.Logs;
        _events = _tables.Events;
    }

    /// <inheritdoc />
    public override async ValueTask<long> AppendAsync(TaskKey task, SequencedEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt.Event, nameof(evt));
        // The bytes the server encoded, as they are (encoded here only for an event made without them).
        var json = Encoding.UTF8.GetString(EventEncoder.EnsureEncoded(evt).Json.Span);

        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var seq = await AppendEventAsync(conn, tx, _tables, task, json, Math.Max(1, _options.ReplayBufferSize), _clock.NowTicks, cancellationToken)
            .ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return seq;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence, CancellationToken cancellationToken = default) =>
        ReadAsync(task, afterSequence, null, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<SequencedEvent> ReadRangeAsync(TaskKey task, long afterSequence, long beforeSequence,
        CancellationToken cancellationToken = default) =>
        ReadAsync(task, afterSequence, beforeSequence, cancellationToken);

    private async IAsyncEnumerable<SequencedEvent> ReadAsync(TaskKey task, long afterSequence, long? beforeSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (beforeSequence is { } b && b <= afterSequence + 1) yield break;
        // Read the retained events (at most ReplayBufferSize) in one go, so a slow consumer does not hold a connection.
        var snapshot = new List<SequencedEvent>();
        await using (var cmd = _db.CreateCommand(
            $"""
            SELECT e.seq, e.event FROM {_events} e
              JOIN {_logs} l ON l.tenant = e.tenant AND l.owner = e.owner AND l.task_id = e.task_id
             WHERE e.tenant = $1 AND e.owner = $2 AND e.task_id = $3 AND e.seq > $4 AND ($6::bigint IS NULL OR e.seq < $6)
               AND (l.purge_ticks IS NULL OR l.purge_ticks > {DbClock.Now(5)})
             ORDER BY e.seq
            """))
        {
            AddKey(cmd, task);
            cmd.Parameters.Add(Long(afterSequence));
            cmd.Parameters.Add(_clock.NowParam());
            cmd.Parameters.Add(NullableLong(beforeSequence));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                snapshot.Add(FromStored(reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var e in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    /// <summary>The stored bytes and the event rebuilt from them.</summary>
    private static SequencedEvent FromStored(long sequence, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var evt = JsonSerializer.Deserialize(bytes, A2AJson.GetTypeInfo<StreamResponse>()) ?? throw new JsonException("Null stored event.");
        return new SequencedEvent(sequence, evt, bytes);
    }

    /// <inheritdoc />
    public override async ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"""
            SELECT min(e.seq) FROM {_events} e
              JOIN {_logs} l ON l.tenant = e.tenant AND l.owner = e.owner AND l.task_id = e.task_id
             WHERE e.tenant = $1 AND e.owner = $2 AND e.task_id = $3 AND (l.purge_ticks IS NULL OR l.purge_ticks > {DbClock.Now(4)})
            """);
        AddKey(cmd, task);
        cmd.Parameters.Add(_clock.NowParam());
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long oldest ? oldest : null;
    }

    /// <inheritdoc />
    public override async ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT last_seq FROM {_logs} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND (purge_ticks IS NULL OR purge_ticks > {DbClock.Now(4)})");
        AddKey(cmd, task);
        cmd.Parameters.Add(_clock.NowParam());
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long last ? last : 0;
    }

    /// <inheritdoc />
    public override async ValueTask MarkTerminalAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        // Like the in-memory store: only an existing log, and only the first call sets the deadline.
        await using var cmd = _db.CreateCommand(
            $"UPDATE {_logs} SET purge_ticks = {DbClock.Now(4)} + $5 WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND purge_ticks IS NULL");
        AddKey(cmd, task);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(_options.TerminalRetention));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DeleteAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        // Events and the log header go together: the sequence counter lives in the header, so a later append restarts at 1.
        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var batch = new NpgsqlBatch(conn, tx))
        {
            var events = new NpgsqlBatchCommand($"DELETE FROM {_events} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
            Sql.AddKey(events.Parameters, task);
            var logs = new NpgsqlBatchCommand($"DELETE FROM {_logs} WHERE tenant = $1 AND owner = $2 AND task_id = $3");
            Sql.AddKey(logs.Parameters, task);
            batch.BatchCommands.Add(events);
            batch.BatchCommands.Add(logs);
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The stored bytes and the event rebuilt from them.</summary>
    internal static SequencedEvent FromStoredText(long sequence, string text) => FromStored(sequence, text);

    private static void AddKey(NpgsqlCommand cmd, TaskKey task) => Sql.AddKey(cmd.Parameters, task);
}
