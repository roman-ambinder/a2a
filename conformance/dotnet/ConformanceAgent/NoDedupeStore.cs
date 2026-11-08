using A2A.Server.Idempotency;

namespace ConformanceAgent;

/// <summary>An idempotency store that remembers nothing: every request is new. Used only with SUT_DISABLE_MESSAGE_DEDUPE.</summary>
internal sealed class NoDedupeStore : IdempotencyStore
{
    public override ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IdempotencyResult>(new IdempotencyResult.New());

    public override ValueTask CompleteAsync(IdempotencyScope scope, IdempotencyOutcome outcome, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask AbandonAsync(IdempotencyScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask RenewAsync(IdempotencyScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
