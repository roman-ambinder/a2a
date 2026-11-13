using System.Diagnostics;
using System.Globalization;
using A2A;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Postgres;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

// ADR-003, action item 1: what one long chunked artifact costs on Postgres, with and without batch commits, and with and
// without PostgresStoreOptions.ArtifactRows.
//
// For each case an in-process server with the Postgres stores runs an agent that streams one artifact in N chunks of B
// bytes, and the client sends one blocking SendMessage. Reported per case: the task-store commits, the wall time, and the
// WAL the database wrote (pg_current_wal_insert_lsn before and after), which counts every byte the commits wrote: the task
// row, the event log, TOAST and indexes. "WAL / payload" is that WAL divided by the artifact's own bytes (N x B).
//
// Run: dotnet run -c Release --project tests/A2A.Benchmarks.Postgres
// It starts postgres:16-alpine with Testcontainers (Docker), or uses A2A_BENCH_POSTGRES as a connection string.
//
// With the argument "paced" it measures memory instead: an agent that waits 1 ms before each append (so chunks do not
// queue into batches, as with a model streaming tokens), N appends to one artifact, and the bytes the process allocated
// during the SendMessage (GC.GetTotalAllocatedBytes), per append.
//
// With the argument "status" it measures a status-heavy task (ADR-005): an agent that sends N status updates, each with a
// message of B bytes (or none: "bare"), then completes. Status updates are never batched, so each one is a commit. Reported:
// commits, wall time, WAL, and the bytes the process allocated, with PostgresStoreOptions.HistoryRows on and off.

var paced = args.Contains("paced");
var status = args.Contains("status");
var cases = new (int Chunks, int ChunkBytes)[] { (1000, 100), (1000, 1000), (250, 10_000) };
var external = Environment.GetEnvironmentVariable("A2A_BENCH_POSTGRES");
PostgreSqlContainer? container = null;
if (string.IsNullOrEmpty(external))
{
    container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    await container.StartAsync();
}

var connectionString = external ?? container!.GetConnectionString();
try
{
    if (status)
    {
        Console.WriteLine("| Updates | Message bytes | History rows | Commits | Total ms | ms / update | WAL MB | WAL KB / update | Allocated MB | KB / update |");
        Console.WriteLine("|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        await Bench.RunAsync(connectionString, 50, 100, batching: true, rows: false, warmUp: true, statusUpdates: true);
        foreach (var (n, bytes) in new[] { (1000, 100), (2000, 100), (4000, 100), (1000, 1000), (2000, 0) })
            foreach (var historyRows in new[] { false, true })
            {
                var r = await Bench.RunAsync(connectionString, n, bytes, batching: true, rows: false, warmUp: false,
                    statusUpdates: true, historyRows: historyRows);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {n} | {(bytes == 0 ? "bare" : bytes.ToString(CultureInfo.InvariantCulture))} | {(historyRows ? "on" : "off")} | {r.Commits} | " +
                    $"{r.Elapsed.TotalMilliseconds:F0} | {r.Elapsed.TotalMilliseconds / n:F2} | {r.WalBytes / 1048576.0:F1} | " +
                    $"{r.WalBytes / 1024.0 / n:F1} | {r.Allocated / 1048576.0:F1} | {r.Allocated / 1024.0 / n:F1} |"));
            }

        return;
    }

    if (paced)
    {
        Console.WriteLine("| Appends | Artifact rows | Commits | Allocated MB | KB / append | Total ms |");
        Console.WriteLine("|---:|---|---:|---:|---:|---:|");
        await Bench.RunAsync(connectionString, 50, 100, batching: true, rows: true, warmUp: true, paced: true);
        foreach (var n in new[] { 500, 2000 })
            foreach (var rows in new[] { true, false })
            {
                var r = await Bench.RunAsync(connectionString, n, 100, batching: true, rows, warmUp: false, paced: true);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {n} | {(rows ? "on" : "off")} | {r.Commits} | {r.Allocated / 1048576.0:F2} | {r.Allocated / 1024.0 / n:F1} | " +
                    $"{r.Elapsed.TotalMilliseconds:F0} |"));
            }

        return;
    }

    Console.WriteLine("| Chunks | Chunk bytes | Batch commits | Artifact rows | Commits | Total ms | ms / chunk | WAL MB | WAL / payload |");
    Console.WriteLine("|---:|---:|---|---|---:|---:|---:|---:|---:|");
    await Bench.RunAsync(connectionString, 50, 100, batching: true, rows: false, warmUp: true); // JIT, pool, schema
    foreach (var (chunks, bytes) in cases)
        foreach (var (batching, rows) in new[] { (true, false), (false, false), (true, true), (false, true) })
        {
            var r = await Bench.RunAsync(connectionString, chunks, bytes, batching, rows, warmUp: false);
            var payload = (double)chunks * bytes;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {chunks} | {bytes} | {(batching ? "on" : "off")} | {(rows ? "on" : "off")} | {r.Commits} | {r.Elapsed.TotalMilliseconds:F0} | " +
                $"{r.Elapsed.TotalMilliseconds / chunks:F2} | {r.WalBytes / 1048576.0:F1} | {r.WalBytes / payload:F1} |"));
        }
}
finally
{
    if (container is not null) await container.DisposeAsync();
}

internal static class Bench
{
    private const string BaseUrl = "http://localhost/a2a";

    public static async Task<(int Commits, TimeSpan Elapsed, long WalBytes, long Allocated)> RunAsync(
        string connectionString, int chunks, int chunkBytes, bool batching, bool rows, bool warmUp, bool paced = false,
        bool statusUpdates = false, bool historyRows = false)
    {
        var schema = "bench_" + Guid.NewGuid().ToString("N")[..12];
        await using var db = NpgsqlDataSource.Create(connectionString);
        var counter = new CommitCounter();
        var card = new AgentCard
        {
            Name = "Bench Agent", Description = "Postgres benchmark agent", Version = "1.0.0",
            SupportedInterfaces = [new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc }],
            Capabilities = new AgentCapabilities { Streaming = true },
            DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "bench", Name = "Bench", Description = "Benchmark", Tags = ["bench"] }],
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ChunkPlan(chunks, chunkBytes, paced));
        var a2a = builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = card;
            o.AllowAnonymous = true;
        });
        if (statusUpdates) a2a.AddAgentExecutor<StatusAgent>();
        else a2a.AddAgentExecutor<ChunkAgent>();
        a2a.AddPostgresStores(db, o =>
        {
            o.Schema = schema;
            o.ArtifactRows = rows;
            o.HistoryRows = historyRows;
        });
        // The same Postgres task store, wrapped to count commits; without IBatchTaskCommit when batching is off.
        builder.Services.Replace(ServiceDescriptor.Singleton<TaskStore>(sp =>
        {
            var inner = new PostgresTaskStore(db, sp.GetRequiredService<IOptions<A2AServerOptions>>().Value.Streaming,
                new PostgresStoreOptions { Schema = schema, ArtifactRows = rows, HistoryRows = historyRows });
            return batching ? new CountingBatchStore(inner, counter) : new CountingStore(inner, counter);
        }));
        builder.Services.AddA2AServerAspNetCore();
        a2a.ConfigureRateLimiting(o => o.Enabled = false);

        await using var app = builder.Build();
        app.MapA2A("/a2a");
        await app.StartAsync();
        var options = new A2AClientOptions();
        var client = new A2AClient(new JsonRpcTransport(app.GetTestClient(), new Uri(BaseUrl), options), options, card);

        var before = await WalLsnAsync(db);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        // No time limit: a long status-heavy task without history rows takes minutes.
        var task = ((TaskResponse)await client.SendMessageAsync("go", options: new A2ARequestOptions { Timeout = Timeout.InfiniteTimeSpan })).Task;
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var after = await WalLsnAsync(db);
        if (task.Status.State != TaskState.Completed)
            throw new InvalidOperationException($"The task ended {task.Status.State}.");
        if (!warmUp && statusUpdates)
        {
            // The client's message and each update's message (the completion has none). The response returns only the
            // newest MaxHistoryLengthReturned, so the stored task is read.
            await using var tenantCmd = db.CreateCommand($"SELECT tenant FROM \"{schema}\".a2a_tasks WHERE task_id = $1");
            tenantCmd.Parameters.Add(new NpgsqlParameter { Value = task.Id });
            var tenant = (string)(await tenantCmd.ExecuteScalarAsync())!;
            var stored = await app.Services.GetRequiredService<TaskStore>().GetForMaintenanceAsync(tenant, task.Id);
            var history = stored!.Task.History?.Count ?? 0;
            var expected = chunkBytes == 0 ? 1 : chunks + 1;
            if (history != expected) throw new InvalidOperationException($"Expected {expected} history messages, got {history}.");
        }
        else if (!warmUp)
        {
            var parts = task.Artifacts?.Sum(a => a.Parts.Count) ?? 0;
            if (parts != chunks) throw new InvalidOperationException($"Expected {chunks} parts, got {parts}.");
        }

        await app.StopAsync();
        await using (var drop = db.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE")) await drop.ExecuteNonQueryAsync();
        return (counter.Commits, watch.Elapsed, await WalDiffAsync(db, after, before), allocated);
    }

    private static async Task<string> WalLsnAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("SELECT pg_current_wal_insert_lsn()::text");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> WalDiffAsync(NpgsqlDataSource db, string after, string before)
    {
        await using var cmd = db.CreateCommand("SELECT pg_wal_lsn_diff($1::pg_lsn, $2::pg_lsn)::bigint");
        cmd.Parameters.Add(new NpgsqlParameter { Value = after });
        cmd.Parameters.Add(new NpgsqlParameter { Value = before });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

internal sealed record ChunkPlan(int Chunks, int ChunkBytes, bool Paced);

/// <summary>
/// Sends <see cref="ChunkPlan.Chunks"/> status updates (working), each with a message of <see cref="ChunkPlan.ChunkBytes"/>
/// random letters, or with no message when that is 0, then completes. Each update is its own commit (never batched).
/// </summary>
internal sealed class StatusAgent(ChunkPlan plan) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var random = new Random(42);
        for (var i = 0; i < plan.Chunks; i++)
        {
            var text = plan.ChunkBytes == 0 ? null : string.Create(plan.ChunkBytes, random, static (span, r) =>
            {
                for (var j = 0; j < span.Length; j++) span[j] = (char)('a' + r.Next(26));
            });
            await updater.StartWorkAsync(text, ct);
        }

        await updater.CompleteAsync(cancellationToken: ct);
    }
}

/// <summary>
/// Streams one artifact in <see cref="ChunkPlan.Chunks"/> chunks as fast as it can (or 1 ms apart when
/// <see cref="ChunkPlan.Paced"/>), then completes.
/// </summary>
internal sealed class ChunkAgent(ChunkPlan plan) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        // Text that compresses about as well as prose (pglz compresses TOAST values), not a repeated character.
        var random = new Random(42);
        string Chunk() => string.Create(plan.ChunkBytes, random, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('a' + r.Next(26));
        });

        await updater.StartWorkAsync(cancellationToken: ct);
        var id = await updater.AddArtifactAsync([Part.FromText(Chunk())], name: "out", cancellationToken: ct);
        for (var i = 1; i < plan.Chunks; i++)
        {
            if (plan.Paced) await Task.Delay(1, ct);
            await updater.AddArtifactAsync([Part.FromText(Chunk())], artifactId: id, append: true,
                lastChunk: i == plan.Chunks - 1, cancellationToken: ct);
        }

        await updater.CompleteAsync(cancellationToken: ct);
    }
}

internal sealed class CommitCounter
{
    private int _commits;
    public int Commits => _commits;
    public void Add() => Interlocked.Increment(ref _commits);
}

/// <summary>The Postgres task store, counting commits. No batch commits: one commit per event.</summary>
internal class CountingStore(PostgresTaskStore inner, CommitCounter counter) : TaskStore, ITaskCommitLookup
{
    protected PostgresTaskStore Inner => inner;
    protected CommitCounter Counter => counter;

    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
    {
        counter.Add();
        return inner.CommitAsync(commit, ct);
    }

    public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken ct = default) =>
        inner.FindCommitAsync(task, mutationId, ct);

    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) => inner.GetAsync(key, ct);
    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default) => inner.CreateAsync(record, ct);
    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default) => inner.UpdateAsync(record, expectedVersion, ct);
    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);
    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) => inner.GetContextAsync(tenant, contextId, ct);
    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) => inner.GetOrCreateContextAsync(record, ct);
    public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default) => inner.ListExpiredAsync(terminalBefore, max, ct);
    public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default) => inner.ListInterruptedBeforeAsync(before, max, ct);
    public override ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default) => inner.DeleteContextAsync(tenant, contextId, ct);
    public override ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default) => inner.ListOrphanContextsAsync(createdBefore, max, ct);
    public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken ct = default) => inner.GetForMaintenanceAsync(tenant, taskId, ct);
}

/// <summary>The same, with the Postgres store's batch commits.</summary>
internal sealed class CountingBatchStore(PostgresTaskStore inner, CommitCounter counter)
    : CountingStore(inner, counter), IBatchTaskCommit
{
    public ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit, CancellationToken ct = default)
    {
        Counter.Add();
        return Inner.CommitBatchAsync(commit, ct);
    }
}
