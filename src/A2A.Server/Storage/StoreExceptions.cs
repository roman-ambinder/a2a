namespace A2A.Server.Storage;

/// <summary>Thrown by <see cref="TaskStore.UpdateAsync"/> when the stored version differs from the expected one (or the task is gone).</summary>
public sealed class ConcurrencyException(TaskKey key, long expected)
    : Exception($"Task '{key.TaskId}' was changed concurrently (expected version {expected}).")
{
    /// <summary>Key of the task that changed.</summary>
    public TaskKey Key { get; } = key;
    /// <summary>Version the caller expected.</summary>
    public long ExpectedVersion { get; } = expected;
}

/// <summary>Thrown by <see cref="TaskStore.CreateAsync"/> when a task with the same tenant and id already exists.</summary>
public sealed class DuplicateTaskException(TaskKey key)
    : Exception($"Task '{key.TaskId}' already exists.")
{
    /// <summary>Key of the task that already exists.</summary>
    public TaskKey Key { get; } = key;
}
