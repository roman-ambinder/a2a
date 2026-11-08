using A2A.Server.Coordination;
using A2A.Server.Idempotency;
using A2A.Server.Push;

namespace A2A.Server;

/// <summary>Settings of the A2A server. Configure them with <c>AddA2AServer(options =&gt; ...)</c>.</summary>
public sealed class A2AServerOptions
{
    /// <summary>
    /// The public Agent Card. Required, unless a <see cref="Agents.TenantCardProvider"/> supplies each tenant's card; without one,
    /// this card serves every tenant.
    /// </summary>
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
    /// <c>AgentCard.Capabilities.ExtendedAgentCard = true</c>. Return null when no card is configured. The second argument is the
    /// completed public card (<see cref="IAgentCardProvider.Get"/>, with its interfaces and extension entries): copy it, do not change it.
    /// Used when one agent serves every tenant; with a <see cref="Agents.TenantCardProvider"/>, extended cards come from
    /// <see cref="Agents.TenantCardProvider.GetExtendedCardAsync"/> instead, and this delegate is not called.
    /// </summary>
    public Func<ServerCallContext, AgentCard, CancellationToken, ValueTask<AgentCard?>>? ExtendedAgentCardProvider { get; set; }

    /// <summary>Webhook delivery and URL policy for push notifications.</summary>
    public PushNotificationOptions PushNotifications { get; } = new();

    /// <summary>Retry, lease and polling settings of the push outbox (<see cref="PushDeliveryService"/>).</summary>
    public PushDeliveryOptions PushDelivery { get; } = new();

    /// <summary>
    /// Lease, heartbeat and polling settings of the <see cref="ExecutionCoordinator"/>, which tracks running executions across
    /// instances. Checked when the request handler is created.
    /// </summary>
    public CoordinationOptions Coordination { get; } = new();

    /// <summary>How long CancelTask waits for a running execution to stop. Default 5 seconds.</summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest one execution of the agent (one incoming message) may run. When it runs longer, the task moves to
    /// <see cref="TaskState.Failed"/> with the status text "Task exceeded its time limit" (sent to streams and webhooks like
    /// any final event), and the execution's cancellation token fires. Events the agent sends after that are dropped.
    /// Time is measured per execution, so a task waiting in <c>INPUT_REQUIRED</c> or <c>AUTH_REQUIRED</c> with no agent
    /// running is not affected, and each follow-up message gets the full time again. Null, zero, negative, or too large for a
    /// timer (<see cref="int.MaxValue"/> milliseconds, about 24.8 days, or more) turns the limit off. Default null (off). The clock is the <see cref="TimeProvider"/> registered in DI, else <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeSpan? MaxTaskDuration { get; set; }

    /// <summary>
    /// Longest a task may wait in <c>INPUT_REQUIRED</c> or <c>AUTH_REQUIRED</c> with no agent running. After that the retention
    /// sweep (<c>AddA2ARetention</c>) moves it to <see cref="TaskState.Canceled"/> with the status text "The task waited too
    /// long for input." (sent to streams and webhooks like any final event). Measured from the task's status timestamp, on the
    /// sweep's clock, so a task is cancelled up to one sweep interval late. Separate from <see cref="MaxTaskDuration"/>, which
    /// limits running executions. Null turns the timeout off; otherwise it must be positive (checked when the sweep starts).
    /// Default 24 hours. This timeout, dead-execution recovery and the retention purges run only when
    /// <c>AddA2ARetention</c> is registered; without it this setting has no effect.
    /// </summary>
    public TimeSpan? InterruptedTimeout { get; set; } = TimeSpan.FromHours(24);

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
}

/// <summary>Settings for the per-task event log and for SSE streams.</summary>
public sealed class StreamingOptions
{
    /// <summary>
    /// Events kept per task for replay. Default 1000. <see cref="Storage.InMemoryEventStore"/> keeps each retained event as the
    /// model plus its UTF-8 bytes, so memory per event is roughly double the model alone.
    /// </summary>
    public int ReplayBufferSize { get; set; } = 1000;

    /// <summary>How long a terminal task's events are kept after it finishes. Default 10 minutes.</summary>
    public TimeSpan TerminalRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Interval for SSE keep-alive comments while a stream is idle. Default 15 seconds. Zero disables.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Capacity of each per-task bounded event channel. Default 256; values below 1 count as 1. When a channel is full:
    /// <list type="bullet">
    /// <item>the agent's next <see cref="TaskUpdater"/> write waits until the engine catches up (a fast agent is throttled);</item>
    /// <item>a stream or subscription that falls behind drops queued events and fills the gap from the event store, so it still
    /// sends the task's events in order (a snapshot replaces events the store no longer has);</item>
    /// <item>the push delivery queue of a task keeps every event: later events wait, in order, until the sender catches up.</item>
    /// </list>
    /// No channel write waits while the task's lock is held.
    /// </summary>
    public int ChannelCapacity { get; set; } = 256;
}
