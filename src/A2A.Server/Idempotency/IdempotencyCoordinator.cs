using System.Diagnostics;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Idempotency;

/// <summary>
/// Idempotent SendMessage (spec design §7.3): claims the idempotency key, answers a retry from the earlier outcome, records
/// the outcome and abandons the claim on failure paths.
/// </summary>
public sealed class IdempotencyCoordinator(IdempotencyStore store, TaskStore tasks, A2AServerOptions options, ILogger<IdempotencyCoordinator>? logger = null)
{
    /// <summary>
    /// Longest idempotency key (the <c>Idempotency-Key</c> header, else the message id), in characters. Keys are held in
    /// memory per caller, so a longer one is InvalidParams.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 256;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// Begins idempotency for a SendMessage, after authorization and before the message is accepted. The key is the
    /// <c>Idempotency-Key</c> header, else the message id, scoped by tenant and principal. Returns the claim of a new request,
    /// or the outcome of the earlier request with the same key, waiting (up to the request's cancellation) while it runs. A key
    /// reused with a different request is <see cref="A2AException.IdempotencyKeyMismatch"/>; a key longer than
    /// <see cref="MaxIdempotencyKeyLength"/> characters is InvalidParams.
    /// </summary>
    public async ValueTask<(IdempotencyClaim Claim, IdempotencyOutcome? Replay)> BeginAsync(SendMessageRequest request,
        ServerCallContext call, CancellationToken cancellationToken)
    {
        var key = call.IdempotencyKey ?? request.Message?.MessageId;
        if (string.IsNullOrEmpty(key) || request.Message is null) return (IdempotencyClaim.None, null); // validation rejects it later
        if (key.Length > MaxIdempotencyKeyLength) throw A2AException.InvalidParams("Idempotency key too long.");
        var scope = new IdempotencyScope(call.Tenant, call.Principal, key);
        var hash = RequestHasher.Hash(request);
        while (true)
        {
            switch (await store.TryBeginAsync(scope, hash, cancellationToken).ConfigureAwait(false))
            {
                case IdempotencyResult.New:
                    return (new IdempotencyClaim(store, scope), null);
                case IdempotencyResult.Completed done:
                    return (IdempotencyClaim.None, done.Outcome);
                case IdempotencyResult.InProgress running:
                    try
                    {
                        return (IdempotencyClaim.None, await running.Completion.WaitAsync(cancellationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break; // the first request failed before creating anything: try again, possibly as the new owner
                    }
                case IdempotencyResult.Mismatch:
                    throw A2AException.IdempotencyKeyMismatch();
                default:
                    throw new UnreachableException();
            }
        }
    }

    /// <summary>
    /// The answer to a retried SendMessage: the same direct reply, or the task. Like the first request, a blocking retry
    /// returns the task once it is terminal or interrupted (or the request is cancelled); a <c>returnImmediately</c> retry
    /// returns it as it is now. <paramref name="waitUntilSettled"/> waits for a blocking task (tenant/principal/task key,
    /// sequence after which a status event counts) and returns the task.
    /// </summary>
    public async ValueTask<SendMessageResponse> ReplayAsync(IdempotencyOutcome outcome, SendMessageRequest request, ServerCallContext call,
        Func<TaskKey, long, CancellationToken, ValueTask<AgentTask>> waitUntilSettled, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case IdempotencyOutcome.DirectMessage direct:
                return new MessageResponse(direct.Message.DeepCopy()); // the stored outcome stays with the idempotency store
            case IdempotencyOutcome.TaskCreated created:
                var key = new TaskKey(call.Tenant, call.Principal, created.TaskId);
                var task = request.Configuration?.ReturnImmediately == true
                    ? (await tasks.GetAsync(key, cancellationToken).ConfigureAwait(false))?.Task ?? throw A2AException.TaskNotFound(created.TaskId)
                    : await waitUntilSettled(key, created.AfterSequence, cancellationToken).ConfigureAwait(false);
                return new TaskResponse(TaskShaper.Shape(task, request.Configuration?.HistoryLength, options.Limits));
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Records an idempotency outcome. A failing idempotency store is logged; it never stops the task.</summary>
    /// <param name="claim">The claim of the request.</param>
    /// <param name="outcome">What the request produced.</param>
    /// <param name="taskId">The task the outcome belongs to, for the log.</param>
    public async ValueTask RecordAsync(IdempotencyClaim claim, IdempotencyOutcome outcome, string taskId)
    {
        try { await claim.CompleteAsync(outcome).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to record the idempotency outcome for task {TaskId}", taskId); }
    }

    /// <summary>Abandons an idempotency claim on a failure path. A failing idempotency store is logged; the original error wins.</summary>
    /// <param name="claim">The claim of the request.</param>
    /// <param name="taskId">The task the request ran for, for the log; null when none exists yet.</param>
    public async ValueTask AbandonAsync(IdempotencyClaim claim, string? taskId = null)
    {
        try { await claim.AbandonAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (taskId is null) _logger.LogError(ex, "Failed to abandon an idempotency entry");
            else _logger.LogError(ex, "Failed to abandon the idempotency entry for task {TaskId}", taskId);
        }
    }
}
