using System.Collections.Concurrent;
using A2A.Server.Push;
using A2A.Server.Streaming;

namespace A2A.Server.Storage;

/// <summary>
///     Process-local task store. For development and tests; data is lost on restart. It keeps the instances it is given
///     and
///     returns them without copying (see the ownership rule on <see cref="TaskStore" />). <see cref="CommitAsync" />
///     appends to the
///     given <see cref="InMemoryEventStore" /> and queues push deliveries in the given <see cref="InMemoryPushOutbox" />,
///     which must be
///     the server's <see cref="EventStore" /> and <see cref="PushOutbox" />. Without an outbox, a commit with deliveries
///     throws
///     <see cref="NotSupportedException" />.
/// </summary>
/// <param name="events">The event store that <see cref="CommitAsync" /> appends to.</param>
/// <param name="outbox">
///     The push outbox that <see cref="CommitAsync" /> queues deliveries in, or null when no push is
///     used.
/// </param>
public sealed class InMemoryTaskStore(InMemoryEventStore events, InMemoryPushOutbox? outbox = null)
    : TaskStore, IContextSummaryStore, IBatchTaskCommit, ITaskCommitLookup
{
    /// <summary>How many recent mutation ids are kept per task for <see cref="FindCommitAsync" />.</summary>
    internal const int RecentMutationCount = 16;

    private readonly ConcurrentDictionary<(string Tenant, string TaskId), RecentMutations> _mutations = new();
    private readonly object _contextLock = new(); // DeleteContextAsync checks for tasks and removes under it
    private readonly ConcurrentDictionary<(string Tenant, string ContextId), ContextRecord> _contexts = new();
    private readonly InMemoryEventStore _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ConcurrentDictionary<(string Tenant, string TaskId), TaskRecord> _tasks = new();

    /// <inheritdoc />
    public ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit,
        CancellationToken cancellationToken = default)
    {
        ValidateBatch(commit);
        var events = new SequencedEvent[commit.Events.Count];
        var anyDeliveries = false;
        for (var i = 0; i < events.Length; i++)
        {
            var e = commit.Events[i];
            anyDeliveries |= e.Deliveries.Count > 0;
            // The webhook body is the event's bytes, encoded once by the engine.
            events[i] = e.Deliveries.Count > 0 ? EventEncoder.EnsureEncoded(e.Event) : e.Event;
        }

        if (anyDeliveries && outbox is null) throw new NotSupportedException("This store has no push outbox.");
        Action<long>? enqueue = null;
        if (anyDeliveries)
            enqueue = first =>
            {
                for (var i = 0; i < events.Length; i++)
                {
                    var deliveries = commit.Events[i].Deliveries;
                    if (deliveries.Count == 0) continue;
                    var seq = first + i;
                    outbox!.Add(deliveries.Select(d => d with { Sequence = seq }), events[i].Json);
                }
            };
        TaskRecord? stored = null;
        var firstSeq = _events.AppendManyAfter(commit.Record.Key, events,
            () => stored = Replace(commit.Record, commit.ExpectedVersion), enqueue);
        Remember(commit.Record.Key, commit.MutationId, firstSeq);
        return ValueTask.FromResult(new BatchCommitResult(stored!, firstSeq));
    }

    /// <inheritdoc />
    public ValueTask<bool> SetContextSummaryAsync(string tenant, string owner, string contextId, string? summary,
        CancellationToken cancellationToken = default)
    {
        lock (_contextLock)
        {
            if (!_contexts.TryGetValue((tenant, contextId), out var c) ||
                !string.Equals(c.Owner, owner, StringComparison.Ordinal)) return ValueTask.FromResult(false);
            _contexts[(tenant, contextId)] = c with { Summary = summary };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            _tasks.TryGetValue((key.Tenant, key.TaskId), out var r) &&
            string.Equals(r.Owner, key.Owner, StringComparison.Ordinal)
                ? r
                : null);
    }

    /// <inheritdoc />
    public override ValueTask<TaskRecord?> GetForMaintenanceAsync(string tenant, string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(taskId);
        return ValueTask.FromResult(_tasks.TryGetValue((tenant, taskId), out var r) ? r : null);
    }

    /// <inheritdoc />
    public override ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(record));
        var stored = record with { Version = 1 };
        if (!_tasks.TryAdd((stored.Tenant, stored.Task.Id), stored)) throw new DuplicateTaskException(record.Key);
        return ValueTask.FromResult(stored);
    }

    /// <inheritdoc />
    public override ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(Replace(record, expectedVersion));
    }

    /// <inheritdoc />
    public override ValueTask<CommitResult> CommitAsync(TaskCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var record = commit.Record;
        ValidateDeliveries(commit);
        if (commit.Deliveries.Count > 0 && outbox is null)
            throw new NotSupportedException("This store has no push outbox.");
        var evt = commit.Event;
        Action<long>? enqueue = null;
        if (commit.Deliveries.Count > 0)
        {
            // The webhook body: the event's bytes, encoded once by the engine (shared with the log, never changed).
            evt = EventEncoder.EnsureEncoded(evt);
            var payload = evt.Json;
            enqueue = seq => outbox!.Add(commit.Deliveries.Select(d => d with { Sequence = seq }), payload);
        }

        TaskRecord? stored = null;
        long seq;
        if (commit.IsCreate)
        {
            if (record.Version != 0) throw new ArgumentException("A new record must have Version 0.", nameof(commit));
            var created = record with { Version = 1 };
            seq = _events.AppendAfter(record.Key, evt, () =>
            {
                if (!_tasks.TryAdd((created.Tenant, created.Task.Id), created))
                    throw new DuplicateTaskException(record.Key);
                stored = created;
            }, enqueue);
        }
        else
        {
            seq = _events.AppendAfter(record.Key, evt, () => stored = Replace(record, commit.ExpectedVersion), enqueue);
        }

        Remember(record.Key, commit.MutationId, seq);
        return ValueTask.FromResult(new CommitResult(stored!, seq));
    }

    /// <inheritdoc />
    public ValueTask<long?> FindCommitAsync(TaskKey task, string mutationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationId);
        return ValueTask.FromResult(
            _tasks.TryGetValue((task.Tenant, task.TaskId), out var r) &&
            string.Equals(r.Owner, task.Owner, StringComparison.Ordinal) &&
            _mutations.TryGetValue((task.Tenant, task.TaskId), out var recent)
                ? recent.Find(mutationId)
                : null);
    }

    private void Remember(TaskKey task, string? mutationId, long sequence)
    {
        if (mutationId is null) return;
        _mutations.GetOrAdd((task.Tenant, task.TaskId), _ => new RecentMutations()).Add(mutationId, sequence);
    }

    /// <summary>The last <see cref="RecentMutationCount" /> mutation ids of one task, with their sequences.</summary>
    private sealed class RecentMutations
    {
        private readonly (string Id, long Sequence)[] _ring = new (string, long)[RecentMutationCount];
        private int _next;

        public void Add(string id, long sequence)
        {
            lock (_ring)
            {
                _ring[_next] = (id, sequence);
                _next = (_next + 1) % _ring.Length;
            }
        }

        public long? Find(string id)
        {
            lock (_ring)
            {
                foreach (var (stored, sequence) in _ring)
                    if (string.Equals(stored, id, StringComparison.Ordinal)) return sequence;
                return null;
            }
        }
    }

    /// <summary>Compare-and-set: stores <paramref name="record" /> as version expected + 1.</summary>
    private TaskRecord Replace(TaskRecord record, long expectedVersion)
    {
        var id = (record.Tenant, record.Task.Id);
        if (!_tasks.TryGetValue(id, out var current)
            || !string.Equals(current.Owner, record.Owner, StringComparison.Ordinal)
            || current.Version != expectedVersion)
            throw new ConcurrencyException(record.Key, expectedVersion);

        var next = record with
        {
            Version = expectedVersion + 1, CreatedAt = current.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow
        };
        if (!_tasks.TryUpdate(id, next, current)) throw new ConcurrencyException(record.Key, expectedVersion);
        return next;
    }

    /// <inheritdoc />
    public override ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var cursor = PageCursor.Decode(query.PageToken);
        var matching = _tasks.Values
            .Where(r => string.Equals(r.Owner, query.Owner, StringComparison.Ordinal) &&
                        string.Equals(r.Tenant, query.Tenant, StringComparison.Ordinal))
            .Where(r => query.ContextId is null || r.Task.ContextId == query.ContextId)
            .Where(r => query.Status is null || r.Task.Status.State == query.Status)
            .Where(r => query.StatusTimestampAfter is null ||
                        (r.Task.Status.Timestamp ?? r.CreatedAt) >= query.StatusTimestampAfter)
            .ToList();

        // The order is total (ids are unique within a tenant), so the tasks after the cursor are a filter, and only the
        // page (plus one, to tell whether more follow) is sorted out of them, not every match.
        IEnumerable<TaskRecord> rest = matching;
        if (cursor is { } c)
            rest = matching.Where(r =>
            {
                var ticks = (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks;
                return !(ticks > c.Ticks || (ticks == c.Ticks && string.CompareOrdinal(r.Task.Id, c.Id) <= 0));
            });

        var page = rest
            .OrderByDescending(r => (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks)
            .ThenBy(r => r.Task.Id, StringComparer.Ordinal)
            .Take(query.PageSize + 1)
            .ToList();
        var hasMore = page.Count > query.PageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore ? PageCursor.Encode(page[^1]) : string.Empty;
        return ValueTask.FromResult(new TaskPage(page, next, matching.Count));
    }

    /// <inheritdoc />
    public override ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_contexts.TryGetValue((tenant, contextId), out var c) ? c : null);
    }

    /// <inheritdoc />
    public override ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record,
        CancellationToken cancellationToken = default)
    {
        lock (_contextLock)
        {
            return ValueTask.FromResult(_contexts.GetOrAdd((record.Tenant, record.ContextId), record));
        }
    }

    /// <inheritdoc />
    public override ValueTask<ContextPage> ListContextsAsync(ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateContextQuery(query);
        return ValueTask.FromResult(PageContexts(
            _contexts.Values.Where(c =>
                string.Equals(c.Tenant, query.Tenant, StringComparison.Ordinal) &&
                string.Equals(c.Owner, query.Owner, StringComparison.Ordinal)),
            query));
    }

    /// <inheritdoc />
    public override ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken cancellationToken = default)
    {
        // Remove only the exact record that is owned by the caller (a concurrent update may replace the entry).
        while (_tasks.TryGetValue((key.Tenant, key.TaskId), out var current))
        {
            if (!string.Equals(current.Owner, key.Owner, StringComparison.Ordinal)) break;
            if (_tasks.TryRemove(new KeyValuePair<(string, string), TaskRecord>((key.Tenant, key.TaskId), current)))
            {
                _mutations.TryRemove((key.Tenant, key.TaskId), out _);
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TaskKey>>(ListByStatusTime(r => r.Task.Status.State.IsTerminal(),
            terminalBefore, max));
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TaskKey>>(ListByStatusTime(r => r.Task.Status.State.IsInterrupted(),
            before, max));
    }

    private List<TaskKey> ListByStatusTime(Func<TaskRecord, bool> state, DateTimeOffset before, int max)
    {
        return _tasks.Values
            .Where(state)
            .Select(r => (Ticks: (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks, r.Key))
            .Where(x => x.Ticks < before.UtcTicks)
            .OrderBy(x => x.Ticks).ThenBy(x => x.Key.Tenant, StringComparer.Ordinal)
            .ThenBy(x => x.Key.TaskId, StringComparer.Ordinal)
            .Take(Math.Max(0, max))
            .Select(x => x.Key)
            .ToList();
    }

    /// <inheritdoc />
    public override ValueTask<bool> DeleteContextAsync(string tenant, string contextId,
        CancellationToken cancellationToken = default)
    {
        lock (_contextLock)
        {
            if (_tasks.Values.Any(r => r.Tenant == tenant && r.Task.ContextId == contextId))
                return ValueTask.FromResult(false);
            return ValueTask.FromResult(_contexts.TryRemove((tenant, contextId), out _));
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore,
        int max, CancellationToken cancellationToken = default)
    {
        var used = _tasks.Values
            .Where(r => !string.IsNullOrEmpty(r.Task.ContextId))
            .Select(r => (r.Tenant, r.Task.ContextId!))
            .ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<ContextRecord>>(_contexts.Values
            .Where(c => c.CreatedAt.UtcTicks < createdBefore.UtcTicks && !used.Contains((c.Tenant, c.ContextId)))
            .OrderBy(c => c.CreatedAt.UtcTicks).ThenBy(c => c.Tenant, StringComparer.Ordinal)
            .ThenBy(c => c.ContextId, StringComparer.Ordinal)
            .Take(Math.Max(0, max))
            .ToList());
    }
}