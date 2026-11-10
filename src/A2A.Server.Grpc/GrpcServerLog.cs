using Microsoft.Extensions.Logging;

namespace A2A.Server.Grpc;

/// <summary>
///     Source-generated log messages of A2A.Server.Grpc. Event ids are unique across the SDK; each class has its own
///     range:
///     <list type="bullet">
///         <item>
///             <description>900-999: <see cref="A2AGrpcService" />.</description>
///         </item>
///     </list>
///     The other assemblies use 100-199 (A2A.Client), 200-499 (A2A.Server) and 500-899 (A2A.Server.AspNetCore).
/// </summary>
internal static partial class GrpcServerLog
{
    // A2AGrpcService: 900-999
    [LoggerMessage(EventId = 900, Level = LogLevel.Error, Message = "Unhandled error in A2A gRPC call {Method}")]
    public static partial void UnhandledError(ILogger logger, Exception exception, string method);
}