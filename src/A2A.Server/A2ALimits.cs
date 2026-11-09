using A2A.Server.Authorization;

namespace A2A.Server;

/// <summary>
/// Size, count and time limits that protect the server. Every limit is on by default; raise or lower them through
/// <see cref="A2AServerOptions.Limits"/>.
/// </summary>
public sealed class A2ALimits
{
    /// <summary>Largest accepted HTTP request body in bytes. Larger bodies get HTTP 413 (PAYLOAD_TOO_LARGE). Default 10 MB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Most parts in one message. More is InvalidRequest. Default 100.</summary>
    public int MaxPartsPerMessage { get; set; } = 100;

    /// <summary>
    /// Most hops (entries in <c>actors</c>) in the actor chain of <c>metadata["actorChain"]</c>. More is InvalidRequest, before the
    /// agent runs. This is a brake against loops between agents, not a security boundary: the chain is claimed by the caller.
    /// Default 8.
    /// </summary>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP002", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public int MaxActorChainHops { get; set; } = 8;

    /// <summary>Largest decoded <c>raw</c> part in bytes. Larger parts get HTTP 413 (PAYLOAD_TOO_LARGE). Default 8 MB.</summary>
    public int MaxRawPartBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Deepest JSON nesting accepted in a request body. Default 32. Only request bodies are checked against it; the shared
    /// serializer options allow 64 so that responses built from accepted requests always serialize. Values above 64 have no effect.
    /// </summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    /// Most history messages returned for a task, also when the caller sends no <c>historyLength</c> or a larger one
    /// (the newest messages are kept). Default 100.
    /// </summary>
    public int MaxHistoryLengthReturned { get; set; } = 100;

    /// <summary>Upper bound on the ListTasks page size. Default 100.</summary>
    public int MaxListPageSize { get; set; } = 100;

    /// <summary>
    /// Most concurrent streams (SubscribeToTask and SendStreamingMessage) per task and caller. More is RATE_LIMITED. Default 10.
    /// </summary>
    public int MaxConcurrentStreamsPerTask { get; set; } = 10;

    /// <summary>
    /// Most concurrent streams per caller, counted across all tenants (the principal; for anonymous callers, the remote IP
    /// address, see <see cref="ServerCallContext.LimitPartition"/>). More is RATE_LIMITED. Default 50.
    /// </summary>
    public int MaxConcurrentStreamsPerPrincipal { get; set; } = 50;

    /// <summary>
    /// Largest single agent event in bytes, measured as serialized JSON. A larger event is never stored or sent: the task
    /// fails and the agent is stopped. This includes a direct reply (a message instead of a task): an oversized reply is not
    /// sent, and the request gets a FAILED task instead. Split large artifacts into chunks. Default 4 MB.
    /// </summary>
    public int MaxEventBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Longest a stream may stay open; then it ends normally and clients resume with <c>Last-Event-ID</c>. Zero or
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns it off. Default 1 hour.
    /// </summary>
    public TimeSpan MaxStreamDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often an open stream asks <see cref="A2AAuthorizationHandler"/> again; on denial the stream ends. Zero or
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns it off. Default 5 minutes.
    /// </summary>
    public TimeSpan StreamReauthInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most push notification configs per task. More is InvalidRequest. Default 10.</summary>
    public int MaxPushConfigsPerTask { get; set; } = 10;

    /// <summary>
    /// Largest stored task in bytes, counted over the content of the history and artifact parts (text as UTF-8, <c>raw</c> as
    /// base64, URLs and data as their text length), plus metadata (key names and JSON values), artifact names and descriptions, and file names. An agent event that would take a running task past it is never stored:
    /// the task fails and the agent is stopped. A follow-up message that would take the task past it is refused with
    /// UnsupportedOperation and the task stays as it is. Default 64 MiB.
    /// </summary>
    public long MaxTaskBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Most artifacts in one task. An event that would add one more is never stored: the task fails and the agent is stopped.
    /// Default 1024.
    /// </summary>
    public int MaxArtifactsPerTask { get; set; } = 1024;
}
