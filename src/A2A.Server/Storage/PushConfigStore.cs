using System.Collections.Concurrent;
using A2A.Json;

namespace A2A.Server.Storage;

/// <summary>A push config and the task it belongs to. <see cref="Config"/> holds protected secrets, never plaintext.</summary>
public sealed record PushConfigRecord(TaskKey TaskKey, TaskPushNotificationConfig Config);

/// <summary>
/// Webhook registrations, keyed by tenant, owner and task. A config owned by someone else looks like a missing one.
/// Implementations must return copies, not shared instances.
/// </summary>
public abstract class PushConfigStore
{
    public abstract ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default);

    public abstract ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default);

    /// <summary>Returns the task's configs ordered by <see cref="TaskPushNotificationConfig.Id"/> (ordinal).</summary>
    public abstract ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default);

    public abstract ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default);

    public abstract ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default);
}

/// <summary>In-memory <see cref="PushConfigStore"/> for development and tests.</summary>
public sealed class InMemoryPushConfigStore : PushConfigStore
{
    private readonly ConcurrentDictionary<(TaskKey Task, string Id), PushConfigRecord> _configs = new();

    public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default)
    {
        _configs[(record.TaskKey, record.Config.Id!)] = Copy(record);
        return ValueTask.CompletedTask;
    }

    public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.TryGetValue((task, configId), out var r) ? Copy(r) : null);

    public override ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PushConfigRecord>>(_configs
            .Where(kv => kv.Key.Task == task)
            .Select(kv => Copy(kv.Value))
            .OrderBy(r => r.Config.Id, StringComparer.Ordinal)
            .ToList());

    public override ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.Count(kv => kv.Key.Task == task));

    public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.TryRemove((task, configId), out _));

    private static PushConfigRecord Copy(PushConfigRecord r) => r with { Config = A2AJson.Clone(r.Config) };
}
