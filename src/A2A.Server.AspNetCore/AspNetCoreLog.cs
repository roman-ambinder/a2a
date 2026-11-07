using Microsoft.Extensions.Logging;

namespace A2A.Server.AspNetCore;

/// <summary>
/// Source-generated log messages of A2A.Server.AspNetCore. Event ids are unique across the SDK; each class has its own range:
/// <list type="bullet">
/// <item><description>500-599: <c>HttpPlumbing</c>.</description></item>
/// <item><description>600-699: <c>JsonRpcEndpoint</c>.</description></item>
/// <item><description>700-799: <c>A2AStartupValidator</c>.</description></item>
/// <item><description>800-899: <c>AgentCardEndpoint</c>.</description></item>
/// </list>
/// The other assemblies use 100-199 (A2A.Client), 200-499 (A2A.Server) and 900-999 (A2A.Server.Grpc).
/// </summary>
internal static partial class AspNetCoreLog
{
    // HttpPlumbing: 500-599
    [LoggerMessage(EventId = 500, Level = LogLevel.Error, Message = "Unhandled error in A2A endpoint")]
    public static partial void UnhandledError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 501, Level = LogLevel.Debug, Message = "Could not write stream error event")]
    public static partial void StreamErrorWriteFailed(ILogger logger, Exception exception);

    // JsonRpcEndpoint: 600-699
    [LoggerMessage(EventId = 600, Level = LogLevel.Warning, Message = "Error after JSON-RPC response started")]
    public static partial void ErrorAfterResponseStarted(ILogger logger, Exception exception);

    // A2AStartupValidator: 700-799
    [LoggerMessage(EventId = 700, Level = LogLevel.Warning, Message = A2AStartupValidator.AnonymousWarning)]
    public static partial void AnonymousCallersAllowed(ILogger logger);

    [LoggerMessage(EventId = 701, Level = LogLevel.Critical, Message = "Stopping the application: {Message}")]
    public static partial void StoppingApplication(ILogger logger, Exception exception, string message);

    // AgentCardEndpoint: 800-899
    [LoggerMessage(EventId = 800, Level = LogLevel.Warning, Message = "Agent Card signature does not cover these empty values, which a2a-python/a2a-js canonicalization removes: {PrunedPaths}")]
    public static partial void CardSignatureSkipsPrunedValues(ILogger logger, string prunedPaths);
}
