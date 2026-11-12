using Microsoft.Extensions.Logging;

namespace A2A.Extensions.AgentFramework;

internal static partial class AgentFrameworkLog
{
    // A2A.Extensions.AgentFramework: 1400-1419
    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning,
        Message = "Task {TaskId}: could not save the agent session after the run failed or was canceled; the next turn starts from the last saved session")]
    public static partial void SessionSaveFailed(ILogger logger, Exception exception, string taskId);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning,
        Message = "Could not delete the agent session of deleted context {ContextId}")]
    public static partial void SessionDeleteFailed(ILogger logger, Exception exception, string contextId);
}
