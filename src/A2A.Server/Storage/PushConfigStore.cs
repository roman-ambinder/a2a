using System.Collections.Concurrent;

namespace A2A.Server.Storage;

/// <summary>A push config and the task it belongs to. <see cref="Config"/> holds protected secrets, never plaintext.</summary>
public sealed record PushConfigRecord(TaskKey TaskKey, TaskPushNotificationConfig Config);

/// <summary>
/// Webhook registrations, keyed by tenant, owner and task. A config owned by someone else looks like a missing one.
/// </summary>
/// <remarks>
/// Ownership: the store owns the config it is given and the instance it returns; callers never mutate them. A caller that
/// needs different values (for example redacted or unprotected secrets) makes a copy with <see cref="ModelCopies.DeepCopy(TaskPushNotificationConfig)"/>.
/// </remarks>
public abstract class PushConfigStore
{
    /// <summary>Stores a registration, replacing one with the same task and id.</summary>
    public abstract ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default);

    /// <summary>Returns one registration, or null when it does not exist for this owner.</summary>
    public abstract ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default);

    /// <summary>Returns the task's configs ordered by <see cref="TaskPushNotificationConfig.Id"/> (ordinal).</summary>
    public abstract ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>Returns the number of registrations of the task.</summary>
    public abstract ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default);

    /// <summary>Deletes a registration. Returns false when it did not exist.</summary>
    public abstract ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default);
}

/// <summary>In-memory <see cref="PushConfigStore"/> for development and tests. Keeps and returns the instances it is given, without copying.</summary>
public sealed class InMemoryPushConfigStore : PushConfigStore
{
    private readonly ConcurrentDictionary<(TaskKey Task, string Id), PushConfigRecord> _configs = new();

    /// <inheritdoc />
    public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default)
    {
        _configs[(record.TaskKey, record.Config.Id!)] = record;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.TryGetValue((task, configId), out var r) ? r : null);

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PushConfigRecord>>(_configs
            .Where(kv => kv.Key.Task == task)
            .Select(kv => kv.Value)
            .OrderBy(r => r.Config.Id, StringComparer.Ordinal)
            .ToList());

    /// <inheritdoc />
    public override ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.Count(kv => kv.Key.Task == task));

    /// <inheritdoc />
    public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default) =>
        ValueTask.FromResult(_configs.TryRemove((task, configId), out _));
}
