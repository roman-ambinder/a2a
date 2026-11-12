using Microsoft.Extensions.Logging;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Source-generated log messages of A2A.Server.AspNetCore. Event ids are unique across the SDK; each class has its own
///     range:
///     <list type="bullet">
///         <item>
///             <description>500-599: <c>HttpPlumbing</c>.</description>
///         </item>
///         <item>
///             <description>600-699: <c>JsonRpcEndpoint</c>.</description>
///         </item>
///         <item>
///             <description>700-799: <c>A2AStartupValidator</c>.</description>
///         </item>
///         <item>
///             <description>800-899: <c>AgentCardEndpoint</c>.</description>
///         </item>
///         <item>
///             <description>1300-1399: API keys.</description>
///         </item>
///         <item>
///             <description>1400-1499: health checks.</description>
///         </item>
///     </list>
///     The other assemblies use 100-199 (A2A.Client), 200-499, 680-699 and 1200-1299 (A2A.Server), 900-999
///     (A2A.Server.Grpc), 1000-1099 (A2A.Server.Postgres), 1100-1199 (A2A.Server.Redis), 1500-1599 (A2A.Mcp), 1600-1699
///     (A2A.V0_3Compat) and 1700-1799 (A2A.Server.V0_3Compat). <c>LogEventIdTests</c> checks that no two log messages
///     share an id.
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

    [LoggerMessage(EventId = 702, Level = LogLevel.Warning,
        Message = "The Agent Card lists the {Binding} interface at {Url}, a local address other agents cannot reach (spec 8.3.1). Set A2AHttpOptions.PublicBaseUrl to the public address, or list the interfaces in the card.")]
    public static partial void CardInterfaceIsLocal(ILogger logger, string binding, string url);

    [LoggerMessage(EventId = 703, Level = LogLevel.Warning,
        Message = "The {Binding} binding is mapped at '{Pattern}' but the Agent Card does not list it (spec 5.2: agents must declare all supported protocols). Add it to SupportedInterfaces, or leave SupportedInterfaces empty to have them listed.")]
    public static partial void MappedBindingNotOnCard(ILogger logger, string binding, string pattern);

    [LoggerMessage(EventId = 704, Level = LogLevel.Warning,
        Message = "The {Binding} binding is mapped at the tenant pattern '{Pattern}', which is never listed automatically. List it, with the tenant filled in, in each tenant's card (spec 5.2).")]
    public static partial void TenantBindingNotListed(ILogger logger, string binding, string pattern);

    [LoggerMessage(EventId = 705, Level = LogLevel.Warning,
        Message = "The Agent Card lists the {Binding} interface at {Url}, which is not encrypted (spec 13.4: production deployments must use HTTPS or TLS).")]
    public static partial void CardInterfaceNotEncrypted(ILogger logger, string binding, string url);

    [LoggerMessage(EventId = 706, Level = LogLevel.Warning,
        Message = "The server listens only on plain HTTP ({Addresses}) and A2AHttpOptions.PublicBaseUrl is not https (spec 13.4: production deployments must use HTTPS or TLS). Ignore this when a proxy in front terminates TLS, and set PublicBaseUrl to its https address.")]
    public static partial void NoEncryptedEndpoint(ILogger logger, string addresses);

    [LoggerMessage(EventId = 707, Level = LogLevel.Debug, Message = "The Agent Card interface check could not run")]
    public static partial void CardCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 708, Level = LogLevel.Warning,
        Message = "The Agent Card's {Field} is empty, but the spec makes it REQUIRED, and required arrays must have at least one element (spec 5.7). Clients that check this may refuse the card.")]
    public static partial void CardRequiredArrayEmpty(ILogger logger, string field);

    [LoggerMessage(EventId = 709, Level = LogLevel.Warning,
        Message = "A2AServerOptions.TreatMissingVersionAsCurrent is on outside Development: a call without the A2A-Version header is served as A2A 1.0, but the spec says an empty version means 0.3 (spec 3.6.2: agents MUST interpret empty value as 0.3 version). Turn it off outside Development; serve v0.3 clients with EnableV03Compat.")]
    public static partial void MissingVersionTreatedAsCurrent(ILogger logger);

    // AgentCardEndpoint: 800-899
    [LoggerMessage(EventId = 800, Level = LogLevel.Warning,
        Message =
            "Agent Card signature does not cover these empty values, which a2a-python/a2a-js canonicalization removes: {PrunedPaths}")]
    public static partial void CardSignatureSkipsPrunedValues(ILogger logger, string prunedPaths);

    // Health checks: 1400-1499 (1200-1299 are the security events of A2A.Server)
    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "Health check {Check} failed")]
    public static partial void HealthCheckFailed(ILogger logger, Exception exception, string check);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Error,
        Message = "A2A API keys were not reloaded; the previous keys stay in use: {Problems}")]
    public static partial void ApiKeysNotReloaded(ILogger logger, Exception exception, string problems);
}
