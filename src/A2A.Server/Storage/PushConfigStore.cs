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
    public abstract ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns one registration, or null when it does not exist for this owner.</summary>
    public abstract ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken cancellationToken = default);

    /// <summary>Returns the task's configs ordered by <see cref="TaskPushNotificationConfig.Id"/> (ordinal).</summary>
    public abstract ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>Returns the number of registrations of the task.</summary>
    public abstract ValueTask<int> CountAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>Deletes a registration. Returns false when it did not exist.</summary>
    public abstract ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every config of the task. Owner-scoped through <paramref name="task"/>: another owner's key deletes nothing.</summary>
    public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically stores <paramref name="record"/> unless that would take the task over <paramref name="limit"/> configs.
    /// Replacing an existing config id always succeeds; a new id succeeds only when the task has fewer than
    /// <paramref name="limit"/> configs. Returns false (nothing stored) at the limit. Concurrent savers never exceed the limit.
    /// </summary>
    public abstract ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken cancellationToken = default);
}

/// <summary>In-memory <see cref="PushConfigStore"/> for development and tests. Keeps and returns the instances it is given, without copying.</summary>
/// <remarks>
/// Configs are indexed by task, so listing or counting one task's configs never looks at another task's. A task's inner
/// dictionary is removed when its last config is deleted; save and delete of the same task serialize on that dictionary,
/// so a save can never land in a dictionary that is being removed.
/// </remarks>
public sealed class InMemoryPushConfigStore : PushConfigStore
{
    private readonly ConcurrentDictionary<TaskKey, ConcurrentDictionary<string, PushConfigRecord>> _configs = new();

    /// <inheritdoc />
    public override ValueTask SaveAsync(PushConfigRecord record, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var task = _configs.GetOrAdd(record.TaskKey, static _ => new ConcurrentDictionary<string, PushConfigRecord>(StringComparer.Ordinal));
            lock (task)
            {
                // A delete may have removed this (empty) dictionary after GetOrAdd returned it: try again with the current one.
                if (!_configs.TryGetValue(record.TaskKey, out var current) || !ReferenceEquals(current, task)) continue;
                task[record.Config.Id!] = record;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var task = _configs.GetOrAdd(record.TaskKey, static _ => new ConcurrentDictionary<string, PushConfigRecord>(StringComparer.Ordinal));
            lock (task)
            {
                if (!_configs.TryGetValue(record.TaskKey, out var current) || !ReferenceEquals(current, task)) continue;
                if (!task.ContainsKey(record.Config.Id!) && task.Count >= limit)
                {
                    // Do not keep an empty dictionary created only for a refused save.
                    if (task.IsEmpty) _configs.TryRemove(new KeyValuePair<TaskKey, ConcurrentDictionary<string, PushConfigRecord>>(record.TaskKey, task));
                    return ValueTask.FromResult(false);
                }
                task[record.Config.Id!] = record;
                return ValueTask.FromResult(true);
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask DeleteAllAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(task, out var configs)) return ValueTask.CompletedTask;
        lock (configs)
        {
            configs.Clear();
            _configs.TryRemove(new KeyValuePair<TaskKey, ConcurrentDictionary<string, PushConfigRecord>>(task, configs));
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_configs.TryGetValue(task, out var configs) && configs.TryGetValue(configId, out var r) ? r : null);

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(task, out var configs)) return ValueTask.FromResult<IReadOnlyList<PushConfigRecord>>([]);
        var list = configs.Values.ToArray();
        Array.Sort(list, static (a, b) => string.CompareOrdinal(a.Config.Id, b.Config.Id));
        return ValueTask.FromResult<IReadOnlyList<PushConfigRecord>>(list);
    }

    /// <inheritdoc />
    public override ValueTask<int> CountAsync(TaskKey task, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_configs.TryGetValue(task, out var configs) ? configs.Count : 0);

    /// <inheritdoc />
    public override ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(task, out var configs)) return ValueTask.FromResult(false);
        lock (configs)
        {
            var removed = configs.TryRemove(configId, out _);
            if (configs.IsEmpty) _configs.TryRemove(new KeyValuePair<TaskKey, ConcurrentDictionary<string, PushConfigRecord>>(task, configs));
            return ValueTask.FromResult(removed);
        }
    }
}
