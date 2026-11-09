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

    /// <summary>
    /// The key is new: the caller owns the request and must call <see cref="IdempotencyStore.CompleteAsync"/> or <see cref="IdempotencyStore.AbandonAsync"/>
    /// with <paramref name="ClaimId"/>. The id names this claim: a store that gave the key to a newer owner ignores the old owner's calls.
    /// </summary>
    /// <param name="ClaimId">Identifies this claim of the key. Pass it to every later call for the claim.</param>
    public sealed record New(Guid ClaimId) : IdempotencyResult;

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
    /// retry does not take an interrupted state from before as this request's outcome. <c>AcceptedStatusTimestamp</c> is, for a
    /// follow-up, the timestamp of the task's status when the request was accepted: when the event store no longer holds the
    /// events after <c>AfterSequence</c>, a blocking retry takes an interrupted state as the outcome only if the status changed
    /// since (null: the stored task decides alone).
    /// </summary>
    public sealed record TaskCreated(string TaskId, long AfterSequence = 0, DateTimeOffset? AcceptedStatusTimestamp = null) : IdempotencyOutcome;

    /// <summary>The agent answered with a message and no task.</summary>
    public sealed record DirectMessage(Message Message) : IdempotencyOutcome;
}

/// <summary>
/// Remembers SendMessage requests by idempotency key (spec design §7.3). The handler begins an entry after authorization and
/// before the message is accepted, records the outcome as soon as a task or a direct reply exists, and abandons the entry
/// when the request failed before that.
/// </summary>
/// <remarks>
/// Every claim has an id (<see cref="IdempotencyResult.New.ClaimId"/>). <see cref="CompleteAsync"/>, <see cref="AbandonAsync"/>
/// and <see cref="RenewAsync"/> change the entry only while it still belongs to that claim, so an owner whose claim went stale
/// cannot touch the entry of the owner that took the key over.
/// <para>
/// What a retry is guaranteed depends on the path. For a new task, the outcome is written in the same commit that creates the
/// task when the <see cref="Storage.TaskStore"/> shares a transaction with this store (the Postgres stores do), so a crash
/// cannot leave a task without its outcome. With other stores the outcome is recorded right after the commit, and a crash
/// in between lets a retry create a second task. A follow-up message is safe on its own: its message id is in the task history.
/// A direct reply has no commit to join, so its outcome is best effort. A crash before the first commit runs the agent again on
/// retry: delivery is at-least-once.
/// </para>
/// </remarks>
public abstract class IdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="scope"/> for a request with hash <paramref name="requestHash"/>, or reports the earlier
    /// request with that key: <see cref="IdempotencyResult.InProgress"/>, <see cref="IdempotencyResult.Completed"/>, or
    /// <see cref="IdempotencyResult.Mismatch"/> when its hash differs.
    /// </summary>
    public abstract ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of the request that owns <paramref name="scope"/> and releases requests waiting on it. Does nothing
    /// when the entry belongs to another claim than <paramref name="claimId"/>, or already has an outcome.
    /// </summary>
    public abstract ValueTask CompleteAsync(IdempotencyScope scope, Guid claimId, IdempotencyOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the claim of a request that failed before any task or message existed. Waiting requests try again. Does nothing
    /// when the entry belongs to another claim than <paramref name="claimId"/>, or already has an outcome.
    /// </summary>
    public abstract ValueTask AbandonAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the timeout of a running claim (claimed, not completed) to start from now, so a live request keeps its key
    /// while a store that abandons stale claims still releases the key of a crashed one. Does nothing for a completed or
    /// missing scope, or when the entry belongs to another claim than <paramref name="claimId"/>. A store that never expires
    /// running claims does nothing at all.
    /// </summary>
    public abstract ValueTask RenewAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default);
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
/// The outcome of a request, to be written in the same transaction as the task commit that makes it true
/// (<see cref="Storage.TaskCommit.Idempotency"/>).
/// </summary>
/// <param name="Scope">The entry's owner and key.</param>
/// <param name="ClaimId">The claim that must still own the entry for the outcome to be written.</param>
/// <param name="Outcome">What the request produced.</param>
/// <param name="Retention">How long the finished entry is kept (<see cref="IdempotencyOptions.Retention"/>).</param>
public sealed record IdempotencyCommit(IdempotencyScope Scope, Guid ClaimId, IdempotencyOutcome Outcome, TimeSpan Retention);

/// <summary>
/// The handler's claim on one idempotency entry. Records an outcome or abandons the entry at most once, so the execution
/// engine can call it from every path without double bookkeeping. <see cref="None"/> is a claim that does nothing (no key,
/// or the request is a replay). Claims are made by <see cref="IdempotencyCoordinator.BeginAsync"/>.
/// </summary>
public sealed class IdempotencyClaim
{
    private readonly IdempotencyStore? _store;
    private readonly IdempotencyScope _scope;
    private readonly Guid _claimId;
    private readonly TimeSpan _retention;
    private int _settled;

    /// <summary>A claim that does nothing: the request has no key, or it is a replay.</summary>
    public static IdempotencyClaim None { get; } = new(null, default, default, default);

    /// <summary>Creates the claim of the request that owns <paramref name="scope"/> in <paramref name="store"/>.</summary>
    /// <param name="store">The store that holds the entry; null for a claim that does nothing.</param>
    /// <param name="scope">The entry's owner and key.</param>
    /// <param name="claimId">The id the store gave the claim (<see cref="IdempotencyResult.New.ClaimId"/>).</param>
    /// <param name="retention">How long the finished entry is kept.</param>
    internal IdempotencyClaim(IdempotencyStore? store, IdempotencyScope scope, Guid claimId, TimeSpan retention)
    {
        _store = store;
        _scope = scope;
        _claimId = claimId;
        _retention = retention;
    }

    /// <summary>Records <paramref name="outcome"/>, unless the claim was already completed or abandoned.</summary>
    /// <param name="outcome">What the request produced.</param>
    public ValueTask CompleteAsync(IdempotencyOutcome outcome) =>
        _store is not null && Interlocked.Exchange(ref _settled, 1) == 0 ? _store.CompleteAsync(_scope, _claimId, outcome) : ValueTask.CompletedTask;

    /// <summary>Forgets the entry, unless the claim was already completed or abandoned.</summary>
    public ValueTask AbandonAsync() =>
        _store is not null && Interlocked.Exchange(ref _settled, 1) == 0 ? _store.AbandonAsync(_scope, _claimId) : ValueTask.CompletedTask;

    /// <summary>Extends the timeout of the running claim (<see cref="IdempotencyStore.RenewAsync"/>). Does nothing once it is settled.</summary>
    public ValueTask RenewAsync() =>
        _store is not null && Volatile.Read(ref _settled) == 0 ? _store.RenewAsync(_scope, _claimId) : ValueTask.CompletedTask;

    /// <summary>
    /// The commit part for <paramref name="outcome"/>, to join it to a task commit. Null for <see cref="None"/> and for a claim
    /// that is already settled. It does not settle the claim: the caller still records the outcome after the commit.
    /// </summary>
    /// <param name="outcome">What the request produced.</param>
    internal IdempotencyCommit? ForCommit(IdempotencyOutcome outcome) =>
        _store is not null && Volatile.Read(ref _settled) == 0 ? new IdempotencyCommit(_scope, _claimId, outcome, _retention) : null;
}
