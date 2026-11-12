using System.Diagnostics.CodeAnalysis;
using A2A.Server.Authorization;

namespace A2A.Server;

/// <summary>
///     Size, count and time limits that protect the server. Every limit is on by default; raise or lower them through
///     <see cref="A2AServerOptions.Limits" />.
/// </summary>
public sealed class A2ALimits
{
    /// <summary>Largest accepted HTTP request body in bytes. Larger bodies get HTTP 413 (PAYLOAD_TOO_LARGE). Default 10 MB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Most parts in one message. More is InvalidRequest. Default 100.</summary>
    public int MaxPartsPerMessage { get; set; } = 100;

    /// <summary>
    ///     Most hops (entries in <c>actors</c>) in the actor chain of <c>metadata["actorChain"]</c>. More is InvalidRequest,
    ///     before the
    ///     agent runs. This is a brake against loops between agents, not a security boundary: the chain is claimed by the
    ///     caller.
    ///     Default 8.
    /// </summary>
    [Experimental("A2AEXP002",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public int MaxActorChainHops { get; set; } = 8;

    /// <summary>Largest decoded <c>raw</c> part in bytes. Larger parts get HTTP 413 (PAYLOAD_TOO_LARGE). Default 8 MB.</summary>
    public int MaxRawPartBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    ///     Deepest JSON nesting accepted in a request body. Default 32. Only request bodies are checked against it; the shared
    ///     serializer options allow 64 so that responses built from accepted requests always serialize. Values above 64 have
    ///     no effect.
    /// </summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    ///     Most history messages returned for a task, also when the caller sends no <c>historyLength</c> or a larger one
    ///     (the newest messages are kept). Default 100.
    /// </summary>
    public int MaxHistoryLengthReturned { get; set; } = 100;

    /// <summary>
    ///     A2A 1.1: most artifact updates a client may send in one SendMessage request (
    ///     <see cref="SendMessageRequest.ArtifactUpdates" />).
    ///     Each one is also held to the message limits (parts, size). Default 16.
    /// </summary>
    public int MaxUserArtifactsPerRequest { get; set; } = 16;

    /// <summary>
    ///     A2A 1.1: most client inputs waiting for a running agent that reads <see cref="AgentRequestContext.LiveInput" />.
    ///     When it is
    ///     full, a further message is refused before it is stored (on a live exchange as a stream error). Default 32.
    /// </summary>
    public int LiveInputCapacity { get; set; } = 32;

    /// <summary>
    ///     A2A 1.1: most timeline entries returned for a task, also when the caller sends no <c>timelineLength</c> or a larger
    ///     one
    ///     (the newest entries are kept). Default 100.
    /// </summary>
    public int MaxTimelineLengthReturned { get; set; } = 100;

    /// <summary>Upper bound on the ListTasks page size. Default 100.</summary>
    public int MaxListPageSize { get; set; } = 100;

    /// <summary>
    ///     Most concurrent streams (SubscribeToTask and SendStreamingMessage) per task and caller. More is RATE_LIMITED.
    ///     Default 10.
    /// </summary>
    public int MaxConcurrentStreamsPerTask { get; set; } = 10;

    /// <summary>
    ///     Most concurrent streams per caller, counted across all tenants (the principal; for anonymous callers, the remote IP
    ///     address, see <see cref="ServerCallContext.LimitPartition" />). More is RATE_LIMITED. Default 50.
    /// </summary>
    public int MaxConcurrentStreamsPerPrincipal { get; set; } = 50;

    /// <summary>
    ///     Largest single agent event in bytes, measured as serialized JSON. A larger event is never stored or sent: the task
    ///     fails and the agent is stopped. This includes a direct reply (a message instead of a task): an oversized reply is
    ///     not
    ///     sent, and the request gets a FAILED task instead. Split large artifacts into chunks. Default 4 MB.
    /// </summary>
    public int MaxEventBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    ///     Longest a stream may stay open; then it ends normally and clients resume with <c>Last-Event-ID</c>. Zero or
    ///     <see cref="Timeout.InfiniteTimeSpan" /> turns it off. Default 1 hour.
    /// </summary>
    public TimeSpan MaxStreamDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     How often an open stream asks <see cref="A2AAuthorizationHandler" /> again; on denial the stream ends. Zero or
    ///     <see cref="Timeout.InfiniteTimeSpan" /> turns it off. Default 5 minutes. The clock is the
    ///     <see cref="TimeProvider" /> registered in DI, else <see cref="TimeProvider.System" />.
    /// </summary>
    public TimeSpan StreamReauthInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a stream may wait for the task's next event; then it ends normally, as at
    ///     <see cref="MaxStreamDuration" />, and clients resume with <c>Last-Event-ID</c>. The wait starts when the stream
    ///     asks for the next event, so time the client takes to read what was already sent does not count. Keep-alive comments do not count as events. The task goes on. Zero or
    ///     <see cref="Timeout.InfiniteTimeSpan" /> turns it off. Default off. The clock is the <see cref="TimeProvider" />
    ///     registered in DI, else <see cref="TimeProvider.System" />.
    /// </summary>
    public TimeSpan StreamIdleTimeout { get; set; }

    /// <summary>Most push notification configs per task. More is InvalidRequest. Default 10.</summary>
    public int MaxPushConfigsPerTask { get; set; } = 10;

    /// <summary>
    ///     Largest stored task in bytes, counted over the content of the history and artifact parts (text as UTF-8, <c>raw</c>
    ///     as
    ///     base64, URLs and data as their text length), plus metadata (key names and JSON values), artifact names and
    ///     descriptions, and file names. An agent event that would take a running task past it is never stored:
    ///     the task fails and the agent is stopped. A follow-up message that would take the task past it is refused with
    ///     UnsupportedOperation and the task stays as it is. Default 64 MiB.
    /// </summary>
    public long MaxTaskBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    ///     Most artifacts in one task. An event that would add one more is never stored: the task fails and the agent is
    ///     stopped.
    ///     Default 1024.
    /// </summary>
    public int MaxArtifactsPerTask { get; set; } = 1024;

    /// <summary>
    ///     Most agents running at once on this instance (SendMessage, SendStreamingMessage and
    ///     <see cref="Execution.TaskExecutionEngine.ContinueTaskAsync" />; a CancelTask run is never counted). A start over
    ///     the limit
    ///     waits in a queue (<see cref="MaxQueuedExecutions" />, <see cref="MaxExecutionQueueWait" />), then is refused with
    ///     RATE_LIMITED. The limit is per instance: N instances allow N times this. Zero or negative turns it off. Default
    ///     1024.
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = 1024;

    /// <summary>
    ///     Most agents running at once per tenant on this instance. Counted like <see cref="MaxConcurrentExecutions" />. Zero
    ///     or
    ///     negative turns it off (default).
    /// </summary>
    public int MaxConcurrentExecutionsPerTenant { get; set; }

    /// <summary>
    ///     Most starts that may wait for a free slot. With <see cref="MaxConcurrentExecutionsPerTenant" /> set, one tenant may
    ///     queue
    ///     at most that many, so one tenant cannot fill the queue. Zero or negative: no queue, a start over the limit is
    ///     refused at
    ///     once. Default 256.
    /// </summary>
    public int MaxQueuedExecutions { get; set; } = 256;

    /// <summary>
    ///     Longest a start waits for a free slot. Zero, negative, or too large for a timer: no queue (the wait is never
    ///     unbounded).
    ///     Default 10 seconds.
    /// </summary>
    public TimeSpan MaxExecutionQueueWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Most tokens one task may use, over all its runs and instances, as reported with
    ///     <see cref="TaskUpdater.ReportUsageAsync" />. The report that takes the total past the budget fails the task and
    ///     stops the
    ///     agent. Usage exactly at the budget is allowed. Null, zero or negative turns it off (default).
    /// </summary>
    public long? MaxTaskTokens { get; set; }

    /// <summary>
    ///     Most cost units one task may use, over all its runs and instances, as reported with
    ///     <see cref="TaskUpdater.ReportUsageAsync" />. Works like <see cref="MaxTaskTokens" />. Null, zero or negative turns
    ///     it off
    ///     (default).
    /// </summary>
    public decimal? MaxTaskCostUnits { get; set; }
}