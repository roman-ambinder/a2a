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
    /// Writes <see cref="TaskCommit.Record"/>, appends <see cref="TaskCommit.Event"/> to the task's event log with the next
    /// sequence, and stores <see cref="TaskCommit.Deliveries"/>, as one atomic unit: all of it or nothing. Commits to one
    /// task are serialized, so the log order equals the version order, also across instances that share the store.
    /// The event log is the <see cref="EventStore"/> and the deliveries go to the <see cref="Push.PushOutbox"/> that belong
    /// to this store (same backend, same data).
    /// </summary>
    /// <remarks>
    /// A create (<see cref="TaskCommit.ExpectedVersion"/> 0) inserts the task with version 1. Otherwise the stored version
    /// must equal <see cref="TaskCommit.ExpectedVersion"/>; the stored record gets version expected + 1 and keeps its creation time.
    /// Each delivery is stored with the event's sequence (<see cref="PushDelivery.Sequence"/>), attempt 0, due at once.
    /// </remarks>
    /// <returns>The stored record and the event's sequence.</returns>
    /// <exception cref="ConcurrencyException">The stored version differs, or the task does not exist for this owner. Nothing was written.</exception>
    /// <exception cref="DuplicateTaskException">A create, and a task with this tenant and id exists. Nothing was written.</exception>
    /// <exception cref="ArgumentException">A create whose record does not have version 0, or a delivery of another task (<see cref="ValidateDeliveries"/>).</exception>
    public abstract ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless every delivery of <paramref name="commit"/> is for the committed task
    /// (same tenant, owner and id). Call it in <see cref="CommitAsync"/> before writing anything.
    /// </summary>
    protected static void ValidateDeliveries(TaskCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var key = commit.Record.Key;
        foreach (var d in commit.Deliveries)
        {
            if (d.Task != key) throw new ArgumentException("Every push delivery must be for the committed task.", nameof(commit));
            ArgumentException.ThrowIfNullOrEmpty(d.ConfigId, nameof(commit));
            ArgumentException.ThrowIfNullOrEmpty(d.DeliveryId, nameof(commit));
        }
    }

    /// <summary>
    /// Returns tasks matching the query, sorted by status timestamp descending (spec §3.1.4). Must filter by owner and tenant before counting or paging.
    /// Throws <see cref="A2AException"/> (InvalidParams) for a bad page token.
    /// </summary>
    public abstract ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default);

    public abstract ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default);

    /// <summary>Atomic: stores <paramref name="record"/> unless the context exists, and returns whichever record is stored.</summary>
    public abstract ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default);

    // Retention. DeleteAsync is owner-scoped through its key; the List* members and DeleteContextAsync are maintenance
    // calls for the retention service and span all tenants and owners.

    /// <summary>
    /// Deletes the task row. Returns false when there is no such task for this tenant and owner (a foreign task looks missing).
    /// Only the task goes: its events, push configs and outbox rows belong to <see cref="EventStore.DeleteAsync"/>,
    /// <see cref="PushConfigStore.DeleteAllAsync"/> and the outbox, and the context stays (<see cref="DeleteContextAsync"/>).
    /// </summary>
    public abstract ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default);

    /// <summary>
    /// Maintenance, all tenants and owners: keys of tasks in a terminal state (completed, failed, canceled, rejected) whose
    /// status timestamp (the creation time when there is none) is earlier than <paramref name="terminalBefore"/>, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default);

    /// <summary>
    /// Maintenance, all tenants and owners: keys of tasks in INPUT_REQUIRED or AUTH_REQUIRED whose status timestamp (the
    /// creation time when there is none) is earlier than <paramref name="before"/>, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default);

    /// <summary>
    /// Maintenance: atomically deletes the context row only if no task refers to it any more. Returns false when the context
    /// does not exist in <paramref name="tenant"/> or a task still uses it. Tasks are never touched.
    /// </summary>
    public abstract ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default);

    /// <summary>
    /// Maintenance, all tenants and owners: contexts created before <paramref name="createdBefore"/> that no task refers to
    /// any more, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default);
}

/// <summary>Generates unguessable task ids.</summary>
public static class TaskIds
{
    /// <summary>16 random bytes, base64url without padding (22 characters).</summary>
    public static string New() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
}
