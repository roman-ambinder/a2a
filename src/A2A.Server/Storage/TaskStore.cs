using System.Security.Cryptography;
using A2A.Json;
using A2A.Signing;

namespace A2A.Server.Storage;

/// <summary>Identifies a task for one caller. A task owned by another caller is never reachable through another owner's key.</summary>
public readonly record struct TaskKey(string Tenant, string Owner, string TaskId);

/// <summary>A stored task plus its owner and optimistic-concurrency version.</summary>
public sealed record TaskRecord(string Tenant, string Owner, AgentTask Task, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The key of this record.</summary>
    public TaskKey Key => new(Tenant, Owner, Task.Id);
}

/// <summary>A conversation context and the caller that first used it.</summary>
public sealed record ContextRecord(string Tenant, string Owner, string ContextId, DateTimeOffset CreatedAt)
{
    /// <summary>
    /// A rolling summary of the conversation, written by the agent (<see cref="IContextSummaryStore"/>). Null when there is none.
    /// A store that cannot keep it may leave it null.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// What <see cref="TaskStore.ReadContextMessagesAsync"/> reads: the newest tasks of one context, for one caller. Both limits always apply,
/// and the first one reached wins. Both must be at least 1.
/// </summary>
/// <param name="Tenant">The tenant of the context.</param>
/// <param name="Owner">The caller. Tasks of other owners are never read.</param>
/// <param name="ContextId">The context.</param>
/// <param name="MaxTasks">How many tasks to read, newest first. Rejected tasks and <paramref name="ExcludeTaskId"/> do not count.</param>
/// <param name="MaxBytes">The size limit: the UTF-8 length of the JSON of each message, summed. Newest messages first; the read stops at the first message that does not fit.</param>
/// <param name="ExcludeTaskId">A task to leave out, usually the one that runs now. Null for none.</param>
public sealed record ContextHistoryQuery(string Tenant, string Owner, string ContextId, int MaxTasks, long MaxBytes, string? ExcludeTaskId = null);

/// <summary>A <see cref="TaskStore.ListContextsAsync"/> query, already scoped to the caller. <paramref name="PageSize"/> must be at least 1.</summary>
public sealed record ContextQuery(string Tenant, string Owner, int PageSize, string? PageToken);

/// <summary>One page of contexts. <c>NextPageToken</c> is empty on the last page.</summary>
public sealed record ContextPage(IReadOnlyList<ContextRecord> Records, string NextPageToken);

/// <summary>
/// Optional capability of a <see cref="TaskStore"/>: it can keep the rolling summary of a context (<see cref="ContextRecord.Summary"/>).
/// There is no safe fallback for a write, so a store that does not implement it cannot store summaries. <see cref="InMemoryTaskStore"/>
/// and the PostgreSQL store implement it.
/// </summary>
public interface IContextSummaryStore
{
    /// <summary>
    /// Sets the summary of the context, or clears it when <paramref name="summary"/> is null. Last write wins. Returns false, and
    /// changes nothing, when the context does not exist in <paramref name="tenant"/> or belongs to another owner.
    /// </summary>
    ValueTask<bool> SetContextSummaryAsync(string tenant, string owner, string contextId, string? summary, CancellationToken cancellationToken = default);
}

/// <summary>A ListTasks query, already scoped to the caller.</summary>
public sealed record TaskQuery(string Tenant, string Owner, string? ContextId, TaskState? Status, DateTimeOffset? StatusTimestampAfter, int PageSize, string? PageToken);

/// <summary>One page of ListTasks results. <c>NextPageToken</c> is empty on the last page; <c>TotalSize</c> counts all matching tasks.</summary>
public sealed record TaskPage(IReadOnlyList<TaskRecord> Records, string NextPageToken, int TotalSize);

/// <summary>
/// Task persistence. Reads are owner-scoped: a record owned by someone else looks like a missing one.
/// </summary>
/// <remarks>
/// Ownership: the store owns the instance it returns; callers never mutate it. The same holds for the task given to
/// <see cref="CreateAsync"/> and <see cref="UpdateAsync"/>: from that call on it belongs to the store, and the caller does
/// not change it again. To change a task, read it, build a new one (for example with <see cref="ModelCopies.CopyForUpdate"/>)
/// and pass that to <see cref="UpdateAsync"/>. The store does not change those instances either: the server may still hold
/// one (for example inside a published event). So an in-memory store can keep and return the instances it is given
/// without copying them, and a store that serializes may return a fresh instance on every read; both meet the contract.
/// </remarks>
public abstract class TaskStore
{
    /// <summary>Returns the task, or null when it does not exist or <see cref="TaskKey.Owner"/> is not its owner.</summary>
    public abstract ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new task. <see cref="TaskRecord.Version"/> must be 0 on input; the stored record has version 1.</summary>
    /// <exception cref="DuplicateTaskException">A task with this tenant and id exists.</exception>
    public abstract ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default);

    /// <summary>Replaces the task if its stored version equals <paramref name="expectedVersion"/>. Returns the stored record with version expected + 1.</summary>
    /// <exception cref="ConcurrencyException">The stored version differs, or the task does not exist for this owner.</exception>
    public abstract ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <see cref="TaskCommit.Record"/>, appends <see cref="TaskCommit.Event"/> to the task's event log with the next
    /// sequence, and stores <see cref="TaskCommit.Deliveries"/>, as one atomic unit: all of it or nothing. Commits to one
    /// task are serialized, so the log order equals the version order, also across instances that share the store.
    /// The event log is the <see cref="EventStore"/> and the deliveries go to the <see cref="Push.PushOutbox"/> that belong
    /// to this store (same backend, same data). The record and the event belong to the store from this call on (see the
    /// ownership rule above).
    /// </summary>
    /// <remarks>
    /// A create (<see cref="TaskCommit.ExpectedVersion"/> 0) inserts the task with version 1. Otherwise the stored version
    /// must equal <see cref="TaskCommit.ExpectedVersion"/>; the stored record gets version expected + 1 and keeps its creation time.
    /// The event's <see cref="Streaming.SequencedEvent.Json"/> bytes are kept as they are (never re-encoded), for the log and as
    /// the payload of each delivery. Each delivery is stored with the event's sequence (<see cref="PushDelivery.Sequence"/>),
    /// attempt 0, due at once.
    /// <see cref="TaskCommit.Idempotency"/>: a store that shares a transaction with the idempotency store writes the outcome in
    /// the same transaction, and only for the claim that still owns the entry (no match is not an error). Any other store
    /// ignores it; the engine records the outcome after the commit.
    /// </remarks>
    /// <returns>The stored record and the event's sequence.</returns>
    /// <exception cref="ConcurrencyException">The stored version differs, or the task does not exist for this owner. Nothing was written.</exception>
    /// <exception cref="DuplicateTaskException">A create, and a task with this tenant and id exists. Nothing was written.</exception>
    /// <exception cref="ArgumentException">A create whose record does not have version 0, or a delivery of another task (<see cref="ValidateDeliveries"/>).</exception>
    public abstract ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken cancellationToken = default);

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
    public abstract ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns the context, or null when it does not exist in the tenant.</summary>
    public abstract ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken cancellationToken = default);

    /// <summary>Atomic: stores <paramref name="record"/> unless the context exists, and returns whichever record is stored.</summary>
    public abstract ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the messages of the newest tasks of one context, oldest first: the tasks in reverse order (newest task last), each task's
    /// history in its own order. Tasks are ordered like <see cref="ListAsync"/>: status time descending, then task id ascending.
    /// Filters by tenant, owner and context, and leaves out <see cref="ContextHistoryQuery.ExcludeTaskId"/> and rejected tasks
    /// (the agent refused them). <see cref="ContextHistoryQuery.MaxTasks"/> counts only tasks that pass those filters. The size
    /// limit cuts from the newest message back and stops at the first message that does not fit, so the result has no holes.
    /// Artifacts are never part of the result.
    /// </summary>
    /// <remarks>
    /// The fallback pages through <see cref="ListAsync"/> until it has enough tasks. It loads whole tasks, so it holds their
    /// artifacts in memory for a moment, and the size limit cuts after the read, not in the store. Override it when your store can
    /// cut in the query: read only the newest tasks and only their message history. It only reads, so it is safe across instances.
    /// Throws <see cref="ArgumentOutOfRangeException"/> when <see cref="ContextHistoryQuery.MaxTasks"/> or <see cref="ContextHistoryQuery.MaxBytes"/> is below 1.
    /// </remarks>
    public virtual async ValueTask<IReadOnlyList<Message>> ReadContextMessagesAsync(ContextHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ValidateHistoryQuery(query);
        var tasks = new List<AgentTask>();
        var pageSize = query.MaxTasks < 50 ? query.MaxTasks + 1 : 50; // + 1 for the excluded task; loads as few whole tasks as it can
        string? token = null;
        do
        {
            var page = await ListAsync(new TaskQuery(query.Tenant, query.Owner, query.ContextId, null, null, pageSize, token), cancellationToken).ConfigureAwait(false);
            foreach (var r in page.Records)
            {
                if (r.Task.Status.State == TaskState.Rejected) continue;
                if (query.ExcludeTaskId is not null && string.Equals(r.Task.Id, query.ExcludeTaskId, StringComparison.Ordinal)) continue;
                tasks.Add(r.Task);
                if (tasks.Count >= query.MaxTasks) break;
            }
            token = tasks.Count >= query.MaxTasks || string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (token is not null);

        // Newest first, one message at a time, until one does not fit. Then flip to oldest first.
        var picked = new List<Message>();
        long total = 0;
        foreach (var task in tasks)
        {
            if (task.History is not { } history) continue;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                total += System.Text.Encoding.UTF8.GetByteCount(A2AJson.Serialize(history[i]));
                if (total > query.MaxBytes) goto done;
                picked.Add(history[i]);
            }
        }
        done:
        picked.Reverse();
        return picked;
    }

    /// <summary>
    /// Lists the contexts of one tenant and owner, by creation time descending, then context id ascending, with keyset paging.
    /// Throws <see cref="A2AException"/> (InvalidParams) for a bad page token, and <see cref="ArgumentOutOfRangeException"/> when
    /// <see cref="ContextQuery.PageSize"/> is below 1.
    /// </summary>
    /// <remarks>
    /// The fallback is slow and incomplete: it pages through all tasks of the owner, takes the distinct context ids and reads each
    /// context, so its cost grows with the number of tasks, and it misses a context that has no tasks. Override it with one indexed
    /// query. It only reads, so it is safe across instances. <see cref="InMemoryTaskStore"/> and the PostgreSQL store override it and list every context of the owner.
    /// </remarks>
    public virtual async ValueTask<ContextPage> ListContextsAsync(ContextQuery query, CancellationToken cancellationToken = default)
    {
        ValidateContextQuery(query);
        PageCursor.Decode(query.PageToken); // a bad token fails before any read
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await ListAsync(new TaskQuery(query.Tenant, query.Owner, null, null, null, 100, token), cancellationToken).ConfigureAwait(false);
            foreach (var r in page.Records)
                if (!string.IsNullOrEmpty(r.Task.ContextId)) ids.Add(r.Task.ContextId);
            token = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        }
        while (token is not null);

        var contexts = new List<ContextRecord>();
        foreach (var id in ids)
        {
            var c = await GetContextAsync(query.Tenant, id, cancellationToken).ConfigureAwait(false);
            if (c is not null && string.Equals(c.Owner, query.Owner, StringComparison.Ordinal)) contexts.Add(c);
        }
        return PageContexts(contexts, query);
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless both limits of <paramref name="query"/> are at least 1. Call it first in an override of <see cref="ReadContextMessagesAsync"/>.</summary>
    protected static void ValidateHistoryQuery(ContextHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxTasks, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxBytes, 1L, nameof(query));
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless the page size of <paramref name="query"/> is at least 1. Call it first in an override of <see cref="ListContextsAsync"/>.</summary>
    protected static void ValidateContextQuery(ContextQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1, nameof(query));
    }

    /// <summary>Sorts the contexts (created time descending, then id ascending), skips to the cursor and takes one page.</summary>
    internal static ContextPage PageContexts(IEnumerable<ContextRecord> contexts, ContextQuery query)
    {
        var cursor = PageCursor.Decode(query.PageToken);
        IEnumerable<ContextRecord> rest = contexts
            .OrderByDescending(c => c.CreatedAt.UtcTicks)
            .ThenBy(c => c.ContextId, StringComparer.Ordinal);
        if (cursor is { } cur)
            rest = rest.SkipWhile(c => c.CreatedAt.UtcTicks > cur.Ticks || (c.CreatedAt.UtcTicks == cur.Ticks && string.CompareOrdinal(c.ContextId, cur.Id) <= 0));
        var page = rest.Take(query.PageSize + 1).ToList();
        var hasMore = page.Count > query.PageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new ContextPage(page, hasMore ? PageCursor.Encode(page[^1]) : string.Empty);
    }

    // Retention. DeleteAsync is owner-scoped through its key; the List* members and DeleteContextAsync are maintenance
    // calls for the retention service and span all tenants and owners.

    /// <summary>
    /// Deletes the task row. Returns false when there is no such task for this tenant and owner (a foreign task looks missing).
    /// Only the task goes: its events, push configs and outbox rows belong to <see cref="EventStore.DeleteAsync"/>,
    /// <see cref="PushConfigStore.DeleteAllAsync"/> and the outbox, and the context stays (<see cref="DeleteContextAsync"/>).
    /// </summary>
    public abstract ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maintenance, all tenants and owners: keys of tasks in a terminal state (completed, failed, canceled, rejected) whose
    /// status timestamp (the creation time when there is none) is earlier than <paramref name="terminalBefore"/>, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maintenance, all tenants and owners: keys of tasks in INPUT_REQUIRED or AUTH_REQUIRED whose status timestamp (the
    /// creation time when there is none) is earlier than <paramref name="before"/>, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maintenance: reads a task by tenant and id, whoever owns it. Unlike <see cref="GetAsync"/> it never hides a task behind
    /// its owner, so a caller that decides by "is there a task?" (the orphan sweep of artifact objects) does not take a live
    /// task of another owner for a missing one. Not for request handling. Returns null when the task does not exist in
    /// <paramref name="tenant"/>.
    /// </summary>
    public abstract ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maintenance: atomically deletes the context row only if no task refers to it any more. Returns false when the context
    /// does not exist in <paramref name="tenant"/> or a task still uses it. Tasks are never touched.
    /// </summary>
    public abstract ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maintenance, all tenants and owners: contexts created before <paramref name="createdBefore"/> that no task refers to
    /// any more, oldest first, at most <paramref name="max"/>.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken cancellationToken = default);
}

/// <summary>Generates unguessable task ids.</summary>
public static class TaskIds
{
    /// <summary>16 random bytes, base64url without padding (22 characters).</summary>
    public static string New() => Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
}
