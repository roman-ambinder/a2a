using A2A.Server.Idempotency;
using A2A.Server.Push;

namespace A2A.Server;

public sealed class A2AServerOptions
{
    /// <summary>The public Agent Card. Required.</summary>
    public AgentCard AgentCard { get; set; } = new()
    {
        Name = string.Empty, Description = string.Empty, SupportedInterfaces = [], Version = string.Empty,
        Capabilities = new(), DefaultInputModes = [], DefaultOutputModes = [], Skills = [],
    };

    /// <summary>Accepted protocol versions (Major.Minor). Default {"1.0"}.</summary>
    public ISet<string> SupportedVersions { get; } = new HashSet<string>(StringComparer.Ordinal) { A2AProtocol.Version };

    /// <summary>
    /// The spec says a missing A2A-Version header means 0.3 (§3.6.2), which this server rejects unless listed in
    /// <see cref="SupportedVersions"/>. Set true to treat a missing header as <see cref="A2AProtocol.Version"/> instead
    /// (handy for curl and older tooling, but not strictly compliant). Default false.
    /// </summary>
    public bool TreatMissingVersionAsCurrent { get; set; }

    /// <summary>
    /// Allow callers that are not authenticated. Default false: at startup the ASP.NET Core layer fails unless every A2A
    /// endpoint has an authorization policy (<c>MapA2A(...).RequireAuthorization()</c>). Set true to opt in to anonymous
    /// access. All anonymous callers then share one owner (<see cref="ServerCallContext.AnonymousPrincipal"/>) and can
    /// read each other's tasks. A warning is logged once at startup.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Returns the extended card for an authenticated caller (spec §3.1.11). Requires
    /// <c>AgentCard.Capabilities.ExtendedAgentCard = true</c>. Return null when no card is configured.
    /// </summary>
    public Func<ServerCallContext, CancellationToken, ValueTask<AgentCard?>>? ExtendedAgentCardProvider { get; set; }

    public PushNotificationOptions PushNotifications { get; } = new();

    /// <summary>How long CancelTask waits for a running execution to stop. Default 5 seconds.</summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest one execution of the agent (one incoming message) may run. When it runs longer, the task moves to
    /// <see cref="TaskState.Failed"/> with the status text "Task exceeded its time limit" (sent to streams and webhooks like
    /// any final event), and the execution's cancellation token fires. Events the agent sends after that are dropped.
    /// Time is measured per execution, so a task waiting in <c>INPUT_REQUIRED</c> or <c>AUTH_REQUIRED</c> with no agent
    /// running is not affected, and each follow-up message gets the full time again. Null, zero or negative turns the limit
    /// off. Default null (off). The clock is the <see cref="TimeProvider"/> registered in DI, else <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeSpan? MaxTaskDuration { get; set; }

    /// <summary>
    /// Most incoming messages (SendMessage or SendStreamingMessage calls) one task accepts, counting the message that
    /// created it. A follow-up that would go over the cap is refused with <see cref="A2AErrorCode.UnsupportedOperation"/>
    /// before the agent runs, and the task is not changed. A defense against multi-turn "agent session smuggling", where a
    /// peer agent steers a long conversation step by step. A retry of an accepted message (same idempotency key) is not a
    /// new turn. Turns are counted from the user messages in the stored task history, so a custom
    /// <see cref="Storage.TaskStore"/> must keep the full history for the cap to hold. Null (default) turns the cap off;
    /// 1 means no follow-ups at all.
    /// </summary>
    public int? MaxTurnsPerTask { get; set; }

    /// <summary>
    /// Append the exception type and message to the text of unexpected (non-A2A) errors sent to callers.
    /// Null (default) means true only when the host environment is Development. The ASP.NET Core layer resolves the null.
    /// </summary>
    public bool? IncludeExceptionDetails { get; set; }

    /// <summary>Which task states accept follow-up messages. Default <see cref="Server.FollowUpPolicy.InterruptedOnly"/>.</summary>
    public FollowUpPolicy FollowUpPolicy { get; set; } = FollowUpPolicy.InterruptedOnly;

    /// <summary>How to treat an unknown client-supplied contextId on a new task. Default <see cref="Server.ClientContextIdPolicy.Reject"/>.</summary>
    public ClientContextIdPolicy ClientContextIdPolicy { get; set; } = ClientContextIdPolicy.Reject;

    /// <summary>Size, count and time limits (request body, parts, JSON depth, history, page size, streams, push configs).</summary>
    public A2ALimits Limits { get; } = new();

    /// <summary>Event log retention and SSE keep-alive settings.</summary>
    public StreamingOptions Streaming { get; } = new();

    /// <summary>
    /// Idempotent SendMessage (retention and size of the in-memory store). A retried SendMessage with the same
    /// <c>Idempotency-Key</c> header, or without one the same message id, from the same tenant and principal is answered with
    /// the first request's task or reply instead of running again.
    /// </summary>
    public IdempotencyOptions Idempotency { get; } = new();

    /// <summary>
    /// Rate limit callers per tenant and principal (ASP.NET Core layer, policy <c>"a2a"</c>). Default true. When true the host
    /// must call <c>app.UseA2A()</c> (it adds the rate limiter middleware) or startup fails. Set false to turn rate limiting off.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;
}

/// <summary>Settings for the per-task event log and for SSE streams.</summary>
public sealed class StreamingOptions
{
    /// <summary>Events kept per task for replay. Default 1000.</summary>
    public int ReplayBufferSize { get; set; } = 1000;

    /// <summary>How long a terminal task's events are kept after it finishes. Default 10 minutes.</summary>
    public TimeSpan TerminalRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Interval for SSE keep-alive comments while a stream is idle. Default 15 seconds. Zero disables.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}
