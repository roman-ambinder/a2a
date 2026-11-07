using System.Collections.Concurrent;
using System.Security.Cryptography;
using A2A.Json;
using A2A.Server.Storage;

namespace A2A.Tests.Storage;

/// <summary>
/// A <see cref="TaskStore"/> that checks the ownership contract: once a task is written to the store or returned by it, nobody
/// may change it. It hashes every task it was given or returned, and on every call re-hashes all of them. A changed task is
/// recorded in <see cref="Violations"/> and the call throws.
/// </summary>
internal sealed class OwnershipCheckingTaskStore : TaskStore
{
    private readonly InMemoryTaskStore _inner = new();
    private readonly object _gate = new();
    private readonly Dictionary<(TaskKey Key, long Version), List<(AgentTask Task, byte[] Hash)>> _seen = new();

    /// <summary>One entry per detected change: the task id and the version of the record that was changed.</summary>
    public ConcurrentQueue<string> Violations { get; } = new();

    /// <summary>Re-hashes every task seen so far. Throws when one changed.</summary>
    public void Verify()
    {
        lock (_gate)
        {
            foreach (var ((key, version), tasks) in _seen)
            foreach (var (task, hash) in tasks)
            {
                if (Hash(task).AsSpan().SequenceEqual(hash)) continue;
                Violations.Enqueue($"{key.TaskId} v{version}");
                throw new InvalidOperationException("record mutated after return");
            }
        }
    }

    public override async ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default)
    {
        Verify();
        var r = await _inner.GetAsync(key, ct).ConfigureAwait(false);
        return r is null ? null : Track(r);
    }

    public override async ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default)
    {
        Verify();
        var r = await _inner.CreateAsync(record, ct).ConfigureAwait(false);
        Track(record with { Version = r.Version });
        return Track(r);
    }

    public override async ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        Verify();
        var r = await _inner.UpdateAsync(record, expectedVersion, ct).ConfigureAwait(false);
        Track(record with { Version = r.Version });
        return Track(r);
    }

    public override async ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default)
    {
        Verify();
        var page = await _inner.ListAsync(query, ct).ConfigureAwait(false);
        foreach (var r in page.Records) Track(r);
        return page;
    }

    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default)
    {
        Verify();
        return _inner.GetContextAsync(tenant, contextId, ct);
    }

    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default)
    {
        Verify();
        return _inner.GetOrCreateContextAsync(record, ct);
    }

    private TaskRecord Track(TaskRecord r)
    {
        lock (_gate)
        {
            var id = (r.Key, r.Version);
            if (!_seen.TryGetValue(id, out var tasks)) _seen[id] = tasks = [];
            foreach (var (task, _) in tasks)
                if (ReferenceEquals(task, r.Task)) return r;
            tasks.Add((r.Task, Hash(r.Task)));
        }
        return r;
    }

    private static byte[] Hash(AgentTask task) => SHA256.HashData(A2AJson.SerializeToUtf8Bytes(task));
}
