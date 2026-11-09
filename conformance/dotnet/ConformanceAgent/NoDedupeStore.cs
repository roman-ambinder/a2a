using A2A.Server.Idempotency;

namespace ConformanceAgent;

/// <summary>An idempotency store that remembers nothing: every request is new. Used only with SUT_DISABLE_MESSAGE_DEDUPE.</summary>
internal sealed class NoDedupeStore : IdempotencyStore
{
    public override ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IdempotencyResult>(new IdempotencyResult.New(Guid.NewGuid()));

    public override ValueTask CompleteAsync(IdempotencyScope scope, Guid claimId, IdempotencyOutcome outcome, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask AbandonAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask RenewAsync(IdempotencyScope scope, Guid claimId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
