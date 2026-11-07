using System.Collections.Concurrent;
using A2A.Json;

namespace A2A.Server.Storage;

/// <summary>Process-local task store. For development and tests; data is lost on restart.</summary>
public sealed class InMemoryTaskStore : TaskStore
{
    private readonly ConcurrentDictionary<(string Tenant, string TaskId), TaskRecord> _tasks = new();
    private readonly ConcurrentDictionary<(string Tenant, string ContextId), ContextRecord> _contexts = new();

    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default) =>
        ValueTask.FromResult(
            _tasks.TryGetValue((key.Tenant, key.TaskId), out var r) && string.Equals(r.Owner, key.Owner, StringComparison.Ordinal)
                ? Copy(r)
                : null);

    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default)
    {
        if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(record));
        var stored = Copy(record with { Version = 1 });
        if (!_tasks.TryAdd((stored.Tenant, stored.Task.Id), stored)) throw new DuplicateTaskException(record.Key);
        return ValueTask.FromResult(Copy(stored));
    }

    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default)
    {
        var id = (record.Tenant, record.Task.Id);
        if (!_tasks.TryGetValue(id, out var current)
            || !string.Equals(current.Owner, record.Owner, StringComparison.Ordinal)
            || current.Version != expectedVersion)
            throw new ConcurrencyException(record.Key, expectedVersion);

        var next = Copy(record with { Version = expectedVersion + 1, CreatedAt = current.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow });
        if (!_tasks.TryUpdate(id, next, current)) throw new ConcurrencyException(record.Key, expectedVersion);
        return ValueTask.FromResult(Copy(next));
    }

    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default)
    {
        var cursor = PageCursor.Decode(query.PageToken);
        var matching = _tasks.Values
            .Where(r => string.Equals(r.Owner, query.Owner, StringComparison.Ordinal) && string.Equals(r.Tenant, query.Tenant, StringComparison.Ordinal))
            .Where(r => query.ContextId is null || r.Task.ContextId == query.ContextId)
            .Where(r => query.Status is null || r.Task.Status.State == query.Status)
            .Where(r => query.StatusTimestampAfter is null || (r.Task.Status.Timestamp ?? r.CreatedAt) >= query.StatusTimestampAfter)
            .OrderByDescending(r => (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks)
            .ThenBy(r => r.Task.Id, StringComparer.Ordinal)
            .ToList();

        IEnumerable<TaskRecord> rest = matching;
        if (cursor is { } c)
        {
            rest = matching.SkipWhile(r =>
            {
                var ticks = (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks;
                return ticks > c.Ticks || (ticks == c.Ticks && string.CompareOrdinal(r.Task.Id, c.Id) <= 0);
            });
        }

        var page = rest.Take(query.PageSize + 1).ToList();
        var hasMore = page.Count > query.PageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore ? PageCursor.Encode(page[^1]) : string.Empty;
        return ValueTask.FromResult(new TaskPage(page.Select(Copy).ToList(), next, matching.Count));
    }

    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default) =>
        ValueTask.FromResult(_contexts.TryGetValue((tenant, contextId), out var c) ? c : null);

    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default) =>
        ValueTask.FromResult(_contexts.GetOrAdd((record.Tenant, record.ContextId), record));

    private static TaskRecord Copy(TaskRecord r) => r with { Task = A2AJson.Clone(r.Task) };
}
