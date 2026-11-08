using Microsoft.Extensions.Logging;

namespace A2A.Server;

/// <summary>
/// Source-generated log messages of A2A.Server. Event ids are unique across the SDK; each class has its own range:
/// <list type="bullet">
/// <item><description>200-299: <see cref="Execution.TaskExecutionEngine"/>.</description></item>
/// <item><description>300-399: <see cref="Push.PushDeliveryService"/>.</description></item>
/// <item><description>400-499: <see cref="Idempotency.IdempotencyCoordinator"/>.</description></item>
/// </list>
/// The other assemblies use 100-199 (A2A.Client), 500-899 (A2A.Server.AspNetCore) and 900-999 (A2A.Server.Grpc).
/// </summary>
internal static partial class ServerLog
{
    // TaskExecutionEngine: 200-299
    [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "Agent executor failed for task {TaskId}")]
    public static partial void ExecutorFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Task {TaskId}: the agent produced an event larger than {Limit} bytes; failing the task")]
    public static partial void EventTooLarge(ILogger logger, string taskId, int limit);

    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "Task {TaskId} disappeared from the store; dropping event")]
    public static partial void TaskDisappeared(ILogger logger, string taskId);

    [LoggerMessage(EventId = 203, Level = LogLevel.Error, Message = "Failed to apply events for task {TaskId}")]
    public static partial void ApplyEventsFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Could not mark task {TaskId} as failed after a storage error")]
    public static partial void MarkFailedFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 223, Level = LogLevel.Error, Message = "Could not mark task {TaskId} as failed after its follow-up could not start")]
    public static partial void MarkUnstartedFailedFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 205, Level = LogLevel.Debug, Message = "Dropped event for task {TaskId} in state {State}")]
    public static partial void EventDropped(ILogger logger, string taskId, TaskState state);

    [LoggerMessage(EventId = 206, Level = LogLevel.Debug, Message = "Agent for task {TaskId} stopped: its events could no longer be stored")]
    public static partial void ExecutorStoppedAfterStoreFailure(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 207, Level = LogLevel.Warning, Message = "Task {TaskId}: the agent ran longer than {Limit}; failing the task")]
    public static partial void TaskTimedOut(ILogger logger, string taskId, TimeSpan? limit);

    [LoggerMessage(EventId = 208, Level = LogLevel.Error, Message = "The execution of task {TaskId} ended with an error")]
    public static partial void ExecutionEndedWithError(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 209, Level = LogLevel.Warning, Message = "Could not resolve the Agent Card of task {TaskId}; its status change sends no push notifications")]
    public static partial void TenantCardFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 210, Level = LogLevel.Warning, Message = "Could not register the execution of task {TaskId} with the execution coordinator")]
    public static partial void LeaseRegisterFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 211, Level = LogLevel.Warning, Message = "Could not unregister the execution of task {TaskId} from the execution coordinator")]
    public static partial void LeaseUnregisterFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 212, Level = LogLevel.Warning, Message = "Could not renew the execution lease of task {TaskId}")]
    public static partial void LeaseRenewFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 213, Level = LogLevel.Warning, Message = "Could not renew the idempotency claim of task {TaskId}")]
    public static partial void ClaimRenewFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 214, Level = LogLevel.Warning, Message = "Task {TaskId}: the execution lost its lease and the task has ended; cancelling the agent")]
    public static partial void LeaseLostTaskEnded(ILogger logger, string taskId);

    [LoggerMessage(EventId = 215, Level = LogLevel.Warning, Message = "Task {TaskId}: the execution lost its lease (its heartbeats lapsed); registering it again")]
    public static partial void LeaseLost(ILogger logger, string taskId);

    [LoggerMessage(EventId = 216, Level = LogLevel.Debug, Message = "Could not dispose the cancel request reader")]
    public static partial void CancelReaderDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 217, Level = LogLevel.Warning, Message = "Could not read cancel requests from the execution coordinator; trying again every {Interval}")]
    public static partial void CancelReadFailed(ILogger logger, Exception exception, TimeSpan interval);

    [LoggerMessage(EventId = 218, Level = LogLevel.Debug, Message = "Still cannot read cancel requests from the execution coordinator ({Failures} failures in a row)")]
    public static partial void CancelReadStillFailing(ILogger logger, Exception exception, int failures);

    [LoggerMessage(EventId = 219, Level = LogLevel.Warning, Message = "A cancellation callback of the agent for task {TaskId} failed")]
    public static partial void CancelCallbackFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 220, Level = LogLevel.Warning, Message = "Could not ask other instances to cancel task {TaskId}")]
    public static partial void RequestCancelFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 221, Level = LogLevel.Warning, Message = "Could not check whether task {TaskId} still runs on other instances")]
    public static partial void RunningCheckFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 222, Level = LogLevel.Error, Message = "Could not remove push config {ConfigId} of task {TaskId} that was not created or committed")]
    public static partial void PushConfigCleanupFailed(ILogger logger, Exception exception, string configId, string taskId);

    // Push delivery (PushDeliveryService, PushWebhookSender): 300-399
    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "Cannot unprotect secrets of webhook {ConfigId} for task {TaskId}; dead-lettering the delivery")]
    public static partial void PushUnprotectFailed(ILogger logger, Exception exception, string? configId, string taskId);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Skipping webhook {ConfigId} for task {TaskId}: {Reason}")]
    public static partial void PushWebhookSkipped(ILogger logger, string? configId, string? taskId, string reason);

    [LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "Webhook {ConfigId} for task {TaskId} returned {Status} (attempt {Attempt})")]
    public static partial void PushWebhookStatus(ILogger logger, string? configId, string? taskId, int status, int attempt);

    [LoggerMessage(EventId = 305, Level = LogLevel.Warning, Message = "Skipping webhook {ConfigId} for task {TaskId}: target address is not allowed")]
    public static partial void PushWebhookBlocked(ILogger logger, string? configId, string? taskId);

    [LoggerMessage(EventId = 306, Level = LogLevel.Warning, Message = "Webhook {ConfigId} for task {TaskId} failed (attempt {Attempt})")]
    public static partial void PushWebhookAttemptFailed(ILogger logger, Exception exception, string? configId, string? taskId, int attempt);

    [LoggerMessage(EventId = 307, Level = LogLevel.Error, Message = "Giving up on webhook {ConfigId} for task {TaskId} after {Attempts} attempts")]
    public static partial void PushWebhookGaveUp(ILogger logger, string? configId, string? taskId, int attempts);

    [LoggerMessage(EventId = 308, Level = LogLevel.Error, Message = "Push outbox poll failed")]
    public static partial void PushOutboxPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 309, Level = LogLevel.Debug, Message = "Push config {ConfigId} of task {TaskId} was deleted; dropping delivery {Sequence}")]
    public static partial void PushConfigGone(ILogger logger, string configId, string taskId, long sequence);

    [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "Sent push delivery {Sequence} of task {TaskId} to webhook {ConfigId}, but could not record it; it is sent again when its lease ends")]
    public static partial void PushSentNotRecorded(ILogger logger, Exception exception, long sequence, string taskId, string configId);

    [LoggerMessage(EventId = 311, Level = LogLevel.Error, Message = "Push delivery {Sequence} of task {TaskId} to webhook {ConfigId} failed")]
    public static partial void PushDeliveryFailed(ILogger logger, Exception exception, long sequence, string taskId, string configId);

    [LoggerMessage(EventId = 312, Level = LogLevel.Error, Message = "Could not record the failed push delivery {Sequence} of task {TaskId}")]
    public static partial void PushRecordFailed(ILogger logger, Exception exception, long sequence, string taskId);

    [LoggerMessage(EventId = 313, Level = LogLevel.Debug, Message = "Push delivery {Sequence} of task {TaskId} to webhook {ConfigId} lost its lease; result not recorded")]
    public static partial void PushLeaseLost(ILogger logger, long sequence, string taskId, string configId);

    // A2ARetentionService: 230-249; A2AStartupDiagnostics: 250-259
    [LoggerMessage(EventId = 230, Level = LogLevel.Warning, Message = "Retention sweep failed; the next sweep tries again")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 231, Level = LogLevel.Warning, Message = "Retention step '{Step}' failed; the next sweep tries again")]
    public static partial void SweepStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(EventId = 232, Level = LogLevel.Warning, Message = "Retention could not handle {What} {Item}; the next sweep tries again")]
    public static partial void SweepItemFailed(ILogger logger, Exception exception, string what, string item);

    [LoggerMessage(EventId = 233, Level = LogLevel.Warning, Message = "Task {TaskId}: an execution on this instance missed its heartbeats for longer than the lease; it registers again")]
    public static partial void SweepReclaimedLocal(ILogger logger, string taskId);

    [LoggerMessage(EventId = 234, Level = LogLevel.Warning, Message = "Task {TaskId}: its execution stopped without ending (its instance died); the task failed")]
    public static partial void SweepFailedDeadExecution(ILogger logger, string taskId);

    [LoggerMessage(EventId = 235, Level = LogLevel.Information, Message = "Task {TaskId} waited for input longer than {Timeout}; cancelled")]
    public static partial void SweepCancelledInterrupted(ILogger logger, string taskId, TimeSpan? timeout);

    [LoggerMessage(EventId = 250, Level = LogLevel.Debug, Message = "Skipped the A2A startup diagnostics")]
    public static partial void StartupDiagnosticsSkipped(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 251, Level = LogLevel.Error, Message = A2AStartupDiagnostics.EphemeralProtectorMessage)]
    public static partial void EphemeralProtectorWithDurableOutbox(ILogger logger, string outbox);

    [LoggerMessage(EventId = 252, Level = LogLevel.Warning, Message = A2AStartupDiagnostics.LocalCoordinatorMessage)]
    public static partial void LocalCoordinatorWithDurableStore(ILogger logger, string taskStore);

    [LoggerMessage(EventId = 253, Level = LogLevel.Warning, Message = A2AStartupDiagnostics.RetentionOffMessage)]
    public static partial void RetentionOffWithDurableStore(ILogger logger, string taskStore);

    // IdempotencyCoordinator: 400-499
    [LoggerMessage(EventId = 400, Level = LogLevel.Error, Message = "Failed to record the idempotency outcome for task {TaskId}")]
    public static partial void IdempotencyRecordFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 401, Level = LogLevel.Error, Message = "Failed to abandon an idempotency entry")]
    public static partial void IdempotencyAbandonFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "Failed to abandon the idempotency entry for task {TaskId}")]
    public static partial void IdempotencyAbandonFailedForTask(ILogger logger, Exception exception, string taskId);
}
