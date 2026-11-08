namespace A2A.Server.Idempotency;

/// <summary>
/// Who an idempotency key belongs to. A key is never shared across tenants or principals: the same key from another caller
/// is a different entry.
/// </summary>
public readonly record struct IdempotencyScope(string Tenant, string Principal, string Key);

/// <summary>Result of <see cref="IdempotencyStore.TryBeginAsync"/>. A closed union; exactly one case.</summary>
public abstract record IdempotencyResult
{
    private protected IdempotencyResult() { }

    /// <summary>The key is new: the caller owns the request and must call <see cref="IdempotencyStore.CompleteAsync"/> or <see cref="IdempotencyStore.AbandonAsync"/>.</summary>
    public sealed record New : IdempotencyResult;

    /// <summary>
    /// The same request is running. <c>Completion</c> finishes with its outcome, or is canceled when the request is
    /// abandoned (then try again).
    /// </summary>
    public sealed record InProgress(Task<IdempotencyOutcome> Completion) : IdempotencyResult;

    /// <summary>The same request already has an outcome.</summary>
    public sealed record Completed(IdempotencyOutcome Outcome) : IdempotencyResult;

    /// <summary>The key was used with a different request.</summary>
    public sealed record Mismatch : IdempotencyResult;
}

/// <summary>What a SendMessage produced, so a retry can be answered without running it again. A closed union; exactly one case.</summary>
public abstract record IdempotencyOutcome
{
    private protected IdempotencyOutcome() { }

    /// <summary>
    /// The request created (or continued) the task <c>TaskId</c>. <c>AfterSequence</c> is the task's last event sequence when
    /// the request was accepted: 0 for a new task; for a follow-up, events up to it belong to earlier requests, so a blocking
    /// retry does not take an interrupted state from before as this request's outcome.
    /// </summary>
    public sealed record TaskCreated(string TaskId, long AfterSequence = 0) : IdempotencyOutcome;

    /// <summary>The agent answered with a message and no task.</summary>
    public sealed record DirectMessage(Message Message) : IdempotencyOutcome;
}

/// <summary>
/// Remembers SendMessage requests by idempotency key (spec design §7.3). The handler begins an entry after authorization and
/// before the message is accepted, records the outcome as soon as a task or a direct reply exists, and abandons the entry
/// when the request failed before that.
/// </summary>
public abstract class IdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="scope"/> for a request with hash <paramref name="requestHash"/>, or reports the earlier
    /// request with that key: <see cref="IdempotencyResult.InProgress"/>, <see cref="IdempotencyResult.Completed"/>, or
    /// <see cref="IdempotencyResult.Mismatch"/> when its hash differs.
    /// </summary>
    public abstract ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of the request that owns <paramref name="scope"/> and releases requests waiting on it.</summary>
    public abstract ValueTask CompleteAsync(IdempotencyScope scope, IdempotencyOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Forgets the claim of a request that failed before any task or message existed. Waiting requests try again.</summary>
    public abstract ValueTask AbandonAsync(IdempotencyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the timeout of a running claim (claimed, not completed) to start from now, so a live request keeps its key
    /// while a store that abandons stale claims still releases the key of a crashed one. Does nothing for a completed or
    /// missing scope. A store that never expires running claims does nothing at all.
    /// </summary>
    public abstract ValueTask RenewAsync(IdempotencyScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Settings for idempotent SendMessage.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>How long a finished request is remembered. Default 24 hours.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Most entries the in-memory store keeps. Beyond it the least recently used finished entry is dropped and the
    /// <c>a2a.idempotency.evictions</c> counter (meter <c>A2A</c>) goes up. Default 100,000.
    /// </summary>
    public int MaxEntries { get; set; } = 100_000;
}

/// <summary>
/// The handler's claim on one idempotency entry. Records an outcome or abandons the entry at most once, so the execution
/// engine can call it from every path without double bookkeeping. <see cref="None"/> is a claim that does nothing (no key,
/// or the request is a replay).
/// </summary>
public sealed class IdempotencyClaim
{
    private readonly IdempotencyStore? _store;
    private readonly IdempotencyScope _scope;
    private int _settled;

    /// <summary>A claim that does nothing: the request has no key, or it is a replay.</summary>
    public static IdempotencyClaim None { get; } = new(null, default);

    /// <summary>Creates the claim of the request that owns <paramref name="scope"/> in <paramref name="store"/>.</summary>
    /// <param name="store">The store that holds the entry; null for a claim that does nothing.</param>
    /// <param name="scope">The entry's owner and key.</param>
    internal IdempotencyClaim(IdempotencyStore? store, IdempotencyScope scope)
    {
        _store = store;
        _scope = scope;
    }

    /// <summary>Records <paramref name="outcome"/>, unless the claim was already completed or abandoned.</summary>
    /// <param name="outcome">What the request produced.</param>
    public ValueTask CompleteAsync(IdempotencyOutcome outcome) =>
        _store is not null && Interlocked.Exchange(ref _settled, 1) == 0 ? _store.CompleteAsync(_scope, outcome) : ValueTask.CompletedTask;

    /// <summary>Forgets the entry, unless the claim was already completed or abandoned.</summary>
    public ValueTask AbandonAsync() =>
        _store is not null && Interlocked.Exchange(ref _settled, 1) == 0 ? _store.AbandonAsync(_scope) : ValueTask.CompletedTask;

    /// <summary>Extends the timeout of the running claim (<see cref="IdempotencyStore.RenewAsync"/>). Does nothing once it is settled.</summary>
    public ValueTask RenewAsync() =>
        _store is not null && Volatile.Read(ref _settled) == 0 ? _store.RenewAsync(_scope) : ValueTask.CompletedTask;
}
