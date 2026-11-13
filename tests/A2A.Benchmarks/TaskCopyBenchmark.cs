using A2A.Client;
using A2A.Json;
using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Benchmarks;

/// <summary>
///     ADR-004: the copies the engine makes of a task's lists for each event. One long task per operation, sent with a
///     blocking SendMessage. Each benchmark has a twin with half the events: when the copies are O(1) per event, doubling the
///     events about doubles the allocation; with an O(n) copy per event it grows about 3.5 times.
///     The <c>Serialized</c> cases store through a wrapper that also serializes the task on each commit, as a durable store
///     that writes the task JSON per event does (Postgres): they measure what serializing a task whose lists are shared
///     costs on top of the engine.
/// </summary>
[MemoryDiagnoser]
public class TaskCopyBenchmark
{
    private BenchHost _statusHost = null!;
    private BenchHost _appendHost = null!;
    private BenchHost _statusSerializedHost = null!;
    private BenchHost _appendSerializedHost = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _statusHost = await BenchHost.StartAsync<StatusAgent>();
        // The in-memory store without IBatchTaskCommit, so every append is its own commit (no queueing or timer needed).
        _appendHost = await BenchHost.StartAsync<AppendAgent>(services: s => SingleCommit(s, false));
        _statusSerializedHost = await BenchHost.StartAsync<StatusAgent>(services: s => SingleCommit(s, true));
        _appendSerializedHost = await BenchHost.StartAsync<AppendAgent>(services: s => SingleCommit(s, true));
    }

    private static void SingleCommit(IServiceCollection services, bool serialize) =>
        services.AddSingleton<TaskStore>(sp =>
            new SingleCommitStore(new InMemoryTaskStore((InMemoryEventStore)sp.GetRequiredService<EventStore>(),
                (InMemoryPushOutbox)sp.GetRequiredService<PushOutbox>()), serialize));

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _statusHost.DisposeAsync();
        await _appendHost.DisposeAsync();
        await _statusSerializedHost.DisposeAsync();
        await _appendSerializedHost.DisposeAsync();
    }

    /// <summary>4,000 status updates, each with a message: the history and the timeline both grow by one per event.</summary>
    [Benchmark]
    public Task StatusMessages4000() => RunAsync(_statusHost, "4000");

    /// <summary>The same with 2,000 updates.</summary>
    [Benchmark]
    public Task StatusMessages2000() => RunAsync(_statusHost, "2000");

    /// <summary>2,000 appends to one artifact, each stored by its own commit: the part list grows by one per event.</summary>
    [Benchmark]
    public Task Appends2000() => RunAsync(_appendHost, "2000");

    /// <summary>The same with 1,000 appends.</summary>
    [Benchmark]
    public Task Appends1000() => RunAsync(_appendHost, "1000");

    /// <summary><see cref="StatusMessages2000" /> with the task serialized on each commit.</summary>
    [Benchmark]
    public Task StatusMessages2000Serialized() => RunAsync(_statusSerializedHost, "2000");

    /// <summary><see cref="Appends2000" /> with the task serialized on each commit.</summary>
    [Benchmark]
    public Task Appends2000Serialized() => RunAsync(_appendSerializedHost, "2000");

    private static async Task RunAsync(BenchHost host, string count)
    {
        var response = await host.Client.SendMessageAsync(count);
        if (response is not TaskResponse { Task.Status.State: TaskState.Completed })
            throw new InvalidOperationException("The task did not complete.");
    }

    private static int Count(AgentRequestContext context) => int.Parse(context.Message.GetText(),
        System.Globalization.CultureInfo.InvariantCulture);

    private sealed class StatusAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            var n = Count(context);
            for (var i = 0; i < n; i++) await updater.StartWorkAsync("step", ct);
            await updater.CompleteAsync(ct);
        }
    }

    private sealed class AppendAgent : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            var n = Count(context);
            await updater.StartWorkAsync(cancellationToken: ct);
            var id = await updater.AddArtifactAsync([Part.FromText("chunk")], name: "out", cancellationToken: ct);
            for (var i = 1; i < n; i++)
                await updater.AddArtifactAsync([Part.FromText("chunk")], artifactId: id, append: true, cancellationToken: ct);
            await updater.CompleteAsync(ct);
        }
    }

    /// <summary>
    ///     The in-memory task store without <see cref="IBatchTaskCommit" />: one commit per event. With
    ///     <paramref name="serialize" />, each commit also serializes the task (the bytes are dropped).
    /// </summary>
    private sealed class SingleCommitStore(InMemoryTaskStore inner, bool serialize) : TaskStore, ITaskCommitLookup
    {
        public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId, CancellationToken ct = default) =>
            inner.FindCommitAsync(task, mutationId, ct);

        public override ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default)
        {
            if (serialize) _ = A2AJson.SerializeToUtf8Bytes(commit.Record.Task);
            return inner.CommitAsync(commit, ct);
        }

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
}
