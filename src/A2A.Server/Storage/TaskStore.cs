using System.Security.Cryptography;
using A2A.Signing;

namespace A2A.Server.Storage;

/// <summary>Identifies a task for one caller. A task owned by another caller is never reachable through another owner's key.</summary>
public readonly record struct TaskKey(string Tenant, string Owner, string TaskId);

/// <summary>A stored task plus its owner and optimistic-concurrency version.</summary>
public sealed record TaskRecord(string Tenant, string Owner, AgentTask Task, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public TaskKey Key => new(Tenant, Owner, Task.Id);
}

/// <summary>A conversation context and the caller that first used it.</summary>
public sealed record ContextRecord(string Tenant, string Owner, string ContextId, DateTimeOffset CreatedAt);

/// <summary>A ListTasks query, already scoped to the caller.</summary>
public sealed record TaskQuery(string Tenant, string Owner, string? ContextId, TaskState? Status, DateTimeOffset? StatusTimestampAfter, int PageSize, string? PageToken);

public sealed record TaskPage(IReadOnlyList<TaskRecord> Records, string NextPageToken, int TotalSize);

/// <summary>
/// Task persistence. Reads are owner-scoped: a record owned by someone else looks like a missing one.
/// Implementations must return copies, not shared instances.
/// </summary>
public abstract class TaskStore
{
    /// <summary>Returns the task, or null when it does not exist or <see cref="TaskKey.Owner"/> is not its owner.</summary>
    public abstract ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default);

    /// <summary>Inserts a new task. <see cref="TaskRecord.Version"/> must be 0 on input; the stored record has version 1.</summary>
    /// <exception cref="DuplicateTaskException">A task with this tenant and id exists.</exception>
    public abstract ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default);

    /// <summary>Replaces the task if its stored version equals <paramref name="expectedVersion"/>. Returns the stored record with version expected + 1.</summary>
    /// <exception cref="ConcurrencyException">The stored version differs, or the task does not exist for this owner.</exception>
    public abstract ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Returns tasks matching the query, sorted by status timestamp descending (spec §3.1.4). Must filter by owner and tenant before counting or paging.
    /// Throws <see cref="A2AException"/> (InvalidParams) for a bad page token.
    /// </summary>
    public abstract ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default);

    public abstract ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default);

    /// <summary>Atomic: stores <paramref name="record"/> unless the context exists, and returns whichever record is stored.</summary>
    public abstract ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default);
}

/// <summary>Generates unguessable task ids.</summary>
public static class TaskIds
{
    /// <summary>16 random bytes, base64url without padding (22 characters).</summary>
    public static string New() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
}
