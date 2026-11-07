using Microsoft.Extensions.Logging;

namespace A2A.Client;

/// <summary>
/// Source-generated log messages of A2A.Client. Event ids are unique across the SDK; each class has its own range:
/// <list type="bullet">
/// <item><description>100-199: <see cref="A2ACardResolver"/>.</description></item>
/// </list>
/// The other assemblies use 200-499 (A2A.Server), 500-899 (A2A.Server.AspNetCore) and 900-999 (A2A.Server.Grpc).
/// </summary>
internal static partial class ClientLog
{
    // A2ACardResolver: 100-199
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Agent Card from {Origin} is now signed with key {KeyId} (was {PreviousKeyId}); content unchanged")]
    public static partial void CardKeyChanged(ILogger logger, string origin, string keyId, string? previousKeyId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Agent Card from {Origin} changed: pinned {OldHash}, received {NewHash}")]
    public static partial void CardChanged(ILogger logger, string origin, string oldHash, string newHash);
}
