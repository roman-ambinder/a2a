using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A.Server.Coordination;
using A2A.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
///     An <see cref="ExecutionCoordinator" /> that keeps its leases in PostgreSQL (<c>a2a_execution_leases</c>,
///     <c>a2a_sweep_leases</c>, <c>a2a_cancel_requests</c>). A lease outlives a crash of its instance and then expires, so
///     the
///     retention sweep finds the execution through <see cref="ListExpiredAsync" /> and recovers its task. Instances that
///     share the
///     database also share the running check, the cancel requests and the sweep lease.
/// </summary>
/// <remarks>
///     <para>
///         Lease expiry is decided on the database clock (see <see cref="PostgresStoreOptions.UseDatabaseClock" />), so
///         instances
///         whose clocks disagree still agree on what has expired. Give it the same data source and schema as the stores.
///     </para>
///     <para>
///         Cancel requests are delivered by polling: every <see cref="PostgresStoreOptions.CancelPollInterval" /> the
///         coordinator
///         reads the rows added since it started, hands those of other instances to every reader, and skips its own.
///         Polling needs no
///         <c>LISTEN</c> connection, so it works through PgBouncer in transaction mode. A request row is deleted by the
///         first later
///         request once it is older than a few minutes, so the table stays small. Delivery is at most once, to the readers
///         listening
///         when the request is read: a request made less than one poll interval before a reader registers may reach it
///         too.
///     </para>
///     <para>
///         Call <see cref="StartAsync" /> before serving (the DI extension registers a hosted service that does); reading
///         <see cref="CancelRequestsAsync" /> also starts it.
///     </para>
/// </remarks>
public sealed class PostgresExecutionCoordinator : ExecutionCoordinator, IAsyncDisposable
{
    /// <summary>Most cancel requests one reader holds before the oldest is dropped (see <see cref="CancelRequestsAsync" />).</summary>
    internal const int CancelReaderCapacity = 1024;

    // A request row is deleted once older than this. Longer than CommitOverlap, so a row is never deleted before it is read.
    private static readonly TimeSpan CancelRetention = TimeSpan.FromMinutes(5);

    // The poll re-reads rows this young, so a row whose transaction committed after a later row's (ids are handed out before
    // the commit) is not missed. Rows seen twice are skipped by id.
    private static readonly TimeSpan CommitOverlap = TimeSpan.FromSeconds(10);
    private readonly string _cancels;
    private readonly DbClock _clock;

    private readonly NpgsqlDataSource _db;
    private readonly object _gate = new();
    private readonly TimeSpan _leaseTtl;
    private readonly string _leases;
    private readonly ILogger _logger;
    private readonly PostgresStoreOptions _options;
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private readonly string _sweeps;

    // Copy on write, changed under _gate: a request is fanned out from a snapshot, without the lock, so a dropped request is
    // logged (by the channel's drop callback) outside it.
    private Channel<TaskKey>[] _cancelReaders = [];
    private Task? _poller;
    private CancellationTokenSource? _stop;

    /// <summary>
    ///     Creates the coordinator over <paramref name="dataSource" />. Create the tables first (<see cref="PostgresSchema" />
    ///     ). Throws
    ///     <see cref="InvalidOperationException" /> for invalid <paramref name="coordination" /> options (
    ///     <see cref="CoordinationOptions.Validate" />).
    /// </summary>
    /// <param name="dataSource">The database.</param>
    /// <param name="options">The schema and <see cref="PostgresStoreOptions.CancelPollInterval" />; default <c>public</c>.</param>
    /// <param name="coordination">The lease time; default <see cref="CoordinationOptions" /> defaults.</param>
    /// <param name="time">
    ///     The clock for leases when <see cref="PostgresStoreOptions.UseDatabaseClock" /> is off. Default
    ///     <see cref="TimeProvider.System" />. The poll interval always runs on real time.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public PostgresExecutionCoordinator(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null,
        CoordinationOptions? coordination = null,
        TimeProvider? time = null, ILogger<PostgresExecutionCoordinator>? logger = null)
    {
        _db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? new PostgresStoreOptions();
        _clock = new DbClock(time, _options);
        coordination ??= new CoordinationOptions();
        coordination.Validate();
        _leaseTtl = coordination.LeaseTtl;
        var schema = PostgresSchema.Quote(_options.Schema);
        _leases = $"{schema}.a2a_execution_leases";
        _sweeps = $"{schema}.a2a_sweep_leases";
        _cancels = $"{schema}.a2a_cancel_requests";
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // $1..$3 task, $4 execution id, $5 now, $6 lease TTL (ticks). A new lease, or a renewal of this execution's own row.
    private string UpsertSql =>
        $"""
         INSERT INTO {_leases} (tenant, owner, task_id, execution_id, expiry_ticks)
         VALUES ($1, $2, $3, $4, {DbClock.Now(5)} + $6)
         ON CONFLICT (tenant, owner, task_id, execution_id) DO UPDATE SET expiry_ticks = EXCLUDED.expiry_ticks
         """;

    /// <summary>Same as <see cref="StopAsync" />.</summary>
    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    /// <summary>
    ///     Starts reading cancel requests: only requests added from now on are delivered. Idempotent. The poll loop keeps
    ///     running
    ///     when a read fails (it logs and tries again), but this call throws when the database cannot be reached.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_poller is not null) return;
            // Rows up to this id (and the clock reading) are older than the start: they are never delivered.
            await using var cmd = _db.CreateCommand($"SELECT COALESCE(max(id), 0), {DbClock.Now(1)} FROM {_cancels}");
            cmd.Parameters.Add(_clock.NowParam());
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var state = new PollState(reader.GetInt64(0), reader.GetInt64(1) - CommitOverlap.Ticks);
            var stop = new CancellationTokenSource();
            _stop = stop;
            _poller = Task.Run(() => PollAsync(state, stop.Token), CancellationToken.None);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Stops reading cancel requests. A later <see cref="StartAsync" /> starts again from then on.</summary>
    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_poller is not { } poller) return;
            _stop!.Cancel();
            try
            {
                await poller.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
            _stop = null;
            _poller = null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private NpgsqlCommand LeaseCommand(string sql, TaskKey task, string executionId)
    {
        var cmd = _db.CreateCommand(sql);
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(TextParam(executionId));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(_leaseTtl));
        return cmd;
    }

    /// <inheritdoc />
    public override async ValueTask RegisterAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        await using var cmd = LeaseCommand(UpsertSql, task, executionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Atomic across instances: a transaction takes an advisory lock on the task, so concurrent callers run one after the
    ///     other,
    ///     and each checks the leases that the one before committed. (A hash collision of two tasks only makes them wait for
    ///     each other.)
    /// </remarks>
    public override async ValueTask<bool> TryRegisterExclusiveAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        await using var conn = await _db.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var gate = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended('a2a_lease:' || $1 || chr(31) || $2 || chr(31) || $3, 0))",
                         conn, tx))
        {
            AddKey(gate.Parameters, task);
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A separate statement, so its snapshot is taken after the lock: it sees every lease committed before it.
        await using var cmd = new NpgsqlCommand(
            $"""
             INSERT INTO {_leases} (tenant, owner, task_id, execution_id, expiry_ticks)
             SELECT $1, $2, $3, $4, {DbClock.Now(5)} + $6
             WHERE NOT EXISTS (SELECT 1 FROM {_leases} l
                               WHERE l.tenant = $1 AND l.owner = $2 AND l.task_id = $3 AND l.expiry_ticks > {DbClock.Now(5)})
             ON CONFLICT (tenant, owner, task_id, execution_id) DO UPDATE SET expiry_ticks = EXCLUDED.expiry_ticks
             """, conn, tx);
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(TextParam(executionId));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(_leaseTtl));
        var won = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return won;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> HeartbeatAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        // Only this execution's own, unexpired row: a late heartbeat never brings an expired lease back.
        await using var cmd = LeaseCommand(
            $"""
             UPDATE {_leases} SET expiry_ticks = {DbClock.Now(5)} + $6
             WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND execution_id = $4 AND expiry_ticks > {DbClock.Now(5)}
             """, task, executionId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public override async ValueTask UnregisterAsync(TaskKey task, string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        await using var cmd =
            _db.CreateCommand(
                $"DELETE FROM {_leases} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND execution_id = $4");
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(TextParam(executionId));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_leases} WHERE tenant = $1 AND owner = $2 AND task_id = $3 AND expiry_ticks > {DbClock.Now(4)})");
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(_clock.NowParam());
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Adds a row tagged with this coordinator's origin id, so its own readers ignore it, and deletes the rows older than
    ///     a few
    ///     minutes in the same statement.
    /// </remarks>
    public override async ValueTask RequestCancelAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        await using var cmd = _db.CreateCommand(
            $"""
             WITH old AS (DELETE FROM {_cancels} WHERE created_ticks < {DbClock.Now(5)} - $6)
             INSERT INTO {_cancels} (tenant, owner, task_id, origin, created_ticks) VALUES ($1, $2, $3, $4, {DbClock.Now(5)})
             """);
        AddKey(cmd.Parameters, task);
        cmd.Parameters.Add(TextParam(_origin));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(CancelRetention));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Each reader has a bounded queue of <see cref="CancelReaderCapacity" /> requests. A reader that falls that far
    ///     behind loses
    ///     the oldest ones (delivery is at most once anyway), each logged as a warning; the execution then still stops when
    ///     its
    ///     lease is found lost or the task terminal.
    /// </remarks>
    public override async IAsyncEnumerable<TaskKey> CancelRequestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<TaskKey>(new BoundedChannelOptions(CancelReaderCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        }, dropped => PostgresLog.CancelRequestDropped(_logger, dropped.TaskId, CancelReaderCapacity));
        lock (_gate)
        {
            _cancelReaders = [.. _cancelReaders, channel];
        }

        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var task in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return task;
        }
        finally
        {
            lock (_gate)
            {
                _cancelReaders = Array.FindAll(_cancelReaders, reader => reader != channel);
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        if (max == 0) return [];
        await using var cmd = _db.CreateCommand(
            $"""
             SELECT tenant, owner, task_id, execution_id FROM {_leases} WHERE expiry_ticks <= {DbClock.Now(1)}
             ORDER BY expiry_ticks, tenant, owner, task_id, execution_id LIMIT $2
             """);
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = max });
        var expired = new List<(TaskKey, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            expired.Add((new TaskKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                reader.GetString(3)));
        return expired;
    }

    /// <inheritdoc />
    public override async ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        // A free name is inserted; a taken one is only overwritten once expired (the row lock makes a racing caller re-check).
        await using var cmd = _db.CreateCommand(
            $"""
             INSERT INTO {_sweeps} AS s (name, expiry_ticks) VALUES ($1, {DbClock.Now(2)} + $3)
             ON CONFLICT (name) DO UPDATE SET expiry_ticks = EXCLUDED.expiry_ticks WHERE s.expiry_ticks <= {DbClock.Now(2)}
             """);
        cmd.Parameters.Add(TextParam(name));
        cmd.Parameters.Add(_clock.NowParam());
        cmd.Parameters.Add(Span(duration));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private async Task PollAsync(PollState state, CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(_options.CancelPollInterval);
        try
        {
            do
            {
                try
                {
                    await PollOnceAsync(state, stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PostgresLog.CancelPollFailed(_logger, ex);
                }
            } while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task PollOnceAsync(PollState state, CancellationToken cancellationToken)
    {
        // One row per request, or a single row of NULLs (and the clock) when there are none.
        await using var cmd = _db.CreateCommand(
            $"""
             SELECT r.id, r.tenant, r.owner, r.task_id, r.origin, r.created_ticks, n.now
             FROM (SELECT {DbClock.Now(3)} AS now) n
             LEFT JOIN {_cancels} r ON r.id > $1 AND r.created_ticks > $2
             ORDER BY r.id
             """);
        cmd.Parameters.Add(Long(state.StartId));
        cmd.Parameters.Add(Long(state.Since));
        cmd.Parameters.Add(_clock.NowParam());
        var found = new List<(long Id, TaskKey Task, string Origin, long Created)>();
        long now;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            now = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                now = reader.GetInt64(6);
                if (reader.IsDBNull(0)) continue;
                found.Add((reader.GetInt64(0),
                    new TaskKey(reader.GetString(1), reader.GetString(2), reader.GetString(3)), reader.GetString(4),
                    reader.GetInt64(5)));
            }
        }

        foreach (var (id, task, origin, created) in found)
        {
            if (!state.Seen.TryAdd(id, created) || origin == _origin)
                continue; // handed out before, or our own: the sender cancels directly
            // A full reader drops its oldest request and logs it from TryWrite: never under _gate.
            foreach (var reader in Volatile.Read(ref _cancelReaders)) reader.Writer.TryWrite(task);
        }

        if (now == 0) return;
        state.Since = Math.Max(state.Since, now - CommitOverlap.Ticks);
        foreach (var (id, created) in state.Seen.ToArray())
            if (created <= state.Since)
                state.Seen.Remove(id);
    }

    /// <summary>
    ///     Where the poll stands: the id after which requests count, the age of the rows to re-read, and the ids already
    ///     handed out.
    /// </summary>
    private sealed class PollState(long startId, long since)
    {
        public long StartId { get; } = startId;

        public long Since { get; set; } = since;

        public Dictionary<long, long> Seen { get; } = [];
    }
}