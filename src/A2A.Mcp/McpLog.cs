using Microsoft.Extensions.Logging;

namespace A2A.Mcp;

/// <summary>Source-generated log messages of A2A.Mcp. Event ids 1000 to 1099.</summary>
internal static partial class McpLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "MCP tool {Tool} returned an error: {Detail}")]
    public static partial void ToolReturnedError(ILogger logger, string tool, string detail);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "MCP tool {Tool} rejected the arguments")]
    public static partial void ToolRejectedArguments(ILogger logger, Exception exception, string tool);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "MCP tool {Tool} needs an authorization step outside this bridge. The URL is not forwarded")]
    public static partial void ToolNeedsUrlElicitation(ILogger logger, string tool);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "MCP tool {Tool} did not finish within {Timeout}")]
    public static partial void ToolTimedOut(ILogger logger, string tool, TimeSpan timeout);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Sending MCP progress to the task failed; progress forwarding stops")]
    public static partial void ProgressFailed(ILogger logger, Exception exception);
}