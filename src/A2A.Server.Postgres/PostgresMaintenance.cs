using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static A2A.Server.Postgres.Sql;

namespace A2A.Server.Postgres;

/// <summary>
/// Deletes rows that reads already ignore: event logs of terminal tasks past their retention (with their events) and
/// finished idempotency entries past theirs. Safe to run from every instance at once. Tasks, contexts, push configs and
/// push deliveries are not touched here: the retention sweep (<c>AddA2ARetention</c>) deletes them through the stores.
/// </summary>
/// <param name="dataSource">The database.</param>
/// <param name="options">The schema; default <c>public</c>.</param>
/// <param name="time">The clock that decides what has expired. Default <see cref="TimeProvider.System"/>.</param>
public sealed class PostgresMaintenance(NpgsqlDataSource dataSource, PostgresStoreOptions? options = null, TimeProvider? time = null)
{
    private readonly string _schema = PostgresSchema.Quote((options ?? new()).Schema);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Deletes expired rows. Returns how many event logs and idempotency entries were removed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<(int EventLogs, int IdempotencyEntries)> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcTicks;
        await using var batch = dataSource.CreateBatch();
        var logs = new NpgsqlBatchCommand(
            $"""
            WITH gone AS (
                DELETE FROM {_schema}.a2a_event_logs WHERE purge_ticks IS NOT NULL AND purge_ticks <= $1
                RETURNING tenant, owner, task_id
            ), events AS (
                DELETE FROM {_schema}.a2a_events e USING gone g
                 WHERE e.tenant = g.tenant AND e.owner = g.owner AND e.task_id = g.task_id
            )
            SELECT count(*) FROM gone
            """);
        logs.Parameters.Add(Long(now));
        var idem = new NpgsqlBatchCommand(
            $"WITH gone AS (DELETE FROM {_schema}.a2a_idempotency WHERE expires_ticks IS NOT NULL AND expires_ticks <= $1 RETURNING 1) SELECT count(*) FROM gone");
        idem.Parameters.Add(Long(now));
        batch.BatchCommands.Add(logs);
        batch.BatchCommands.Add(idem);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var logCount = (int)reader.GetInt64(0);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (logCount, (int)reader.GetInt64(0));
    }
}

/// <summary>Creates the schema at startup (when enabled), then runs <see cref="PostgresMaintenance"/> on an interval.</summary>
internal sealed class PostgresStoreHostedService(
    NpgsqlDataSource dataSource,
    PostgresStoreOptions options,
    PostgresMaintenance maintenance,
    TimeProvider? time = null,
    ILogger<PostgresStoreHostedService>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the app takes requests, so the first request finds the tables.
        if (options.CreateSchemaOnStartup)
            await PostgresSchema.EnsureSchemaAsync(dataSource, options.Schema, cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.CleanupInterval;
        if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan) return;
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var (logs, keys) = await maintenance.PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (logs + keys > 0) PostgresLog.CleanupRemoved(_logger, logs, keys);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PostgresLog.CleanupFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Source-generated log messages of A2A.Server.Postgres (event ids 1000-1099).</summary>
internal static partial class PostgresLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "A2A Postgres cleanup removed {EventLogs} event logs and {Keys} idempotency entries")]
    public static partial void CleanupRemoved(ILogger logger, int eventLogs, int keys);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "A2A Postgres cleanup failed; will retry")]
    public static partial void CleanupFailed(ILogger logger, Exception exception);
}
