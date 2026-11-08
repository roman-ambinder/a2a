using Microsoft.Extensions.Logging;

namespace A2A.Server;

/// <summary>
/// Source-generated log messages of A2A.Server. Event ids are unique across the SDK; each class has its own range:
/// <list type="bullet">
/// <item><description>200-299: <see cref="Execution.TaskExecutionEngine"/>.</description></item>
/// <item><description>300-399: <see cref="Push.HttpPushNotificationSender"/>.</description></item>
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

    [LoggerMessage(EventId = 205, Level = LogLevel.Debug, Message = "Dropped event for task {TaskId} in state {State}")]
    public static partial void EventDropped(ILogger logger, string taskId, TaskState state);

    [LoggerMessage(EventId = 206, Level = LogLevel.Debug, Message = "Agent for task {TaskId} stopped: its events could no longer be stored")]
    public static partial void ExecutorStoppedAfterStoreFailure(ILogger logger, Exception exception, string taskId);

    // HttpPushNotificationSender: 300-399
    [LoggerMessage(EventId = 300, Level = LogLevel.Error, Message = "Push notification worker for task {TaskId} failed")]
    public static partial void PushWorkerFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 301, Level = LogLevel.Error, Message = "Push notification processing failed for task {TaskId}")]
    public static partial void PushProcessingFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "Cannot unprotect secrets of webhook {ConfigId} for task {TaskId}; skipping")]
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

    // IdempotencyCoordinator: 400-499
    [LoggerMessage(EventId = 400, Level = LogLevel.Error, Message = "Failed to record the idempotency outcome for task {TaskId}")]
    public static partial void IdempotencyRecordFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 401, Level = LogLevel.Error, Message = "Failed to abandon an idempotency entry")]
    public static partial void IdempotencyAbandonFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "Failed to abandon the idempotency entry for task {TaskId}")]
    public static partial void IdempotencyAbandonFailedForTask(ILogger logger, Exception exception, string taskId);
}
