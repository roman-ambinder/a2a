using System.Diagnostics;

namespace A2A.Diagnostics;

/// <summary>
/// Names used by the SDK's tracing. The SDK traces with <see cref="ActivitySource"/> only, so it works with OpenTelemetry
/// (<c>AddSource("A2A.Server", "A2A.Client")</c>) or any other <see cref="ActivityListener"/>, and costs almost nothing when
/// nobody listens. Tags carry ids and outcomes only: never message content, tokens, credentials or webhook URLs.
/// </summary>
public static class A2ATelemetry
{
    /// <summary>
    /// Source of the server spans: one per operation (<see cref="SpanNames.Operation"/>), one per agent run
    /// (<see cref="SpanNames.AgentExecution"/>) and one per webhook delivery (<see cref="SpanNames.PushDelivery"/>).
    /// </summary>
    public const string ServerSourceName = "A2A.Server";

    /// <summary>Source of the client spans: one per <c>A2AClient</c> operation, covering its retries and stream reconnects.</summary>
    public const string ClientSourceName = "A2A.Client";

    /// <summary>W3C trace context header with the caller's trace and span id.</summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>W3C trace context header with vendor trace state.</summary>
    public const string TraceStateHeader = "tracestate";

    /// <summary>Span names.</summary>
    public static class SpanNames
    {
        /// <summary>Prefix of operation spans: <c>a2a.SendMessage</c>, <c>a2a.GetTask</c>, ... (client and server use the same names; the kind tells them apart).</summary>
        public const string OperationPrefix = "a2a.";

        /// <summary>The name of the span of <paramref name="operation"/> (an <see cref="A2AProtocol.Methods"/> name).</summary>
        public static string Operation(string operation) => OperationPrefix + operation;

        /// <summary>One run of the agent executor for a task (execute or cancel).</summary>
        public const string AgentExecution = "a2a.agent.execute";

        /// <summary>Delivery of one event to one webhook, including retries.</summary>
        public const string PushDelivery = "a2a.push.deliver";
    }

    /// <summary>Span tag names.</summary>
    public static class Tags
    {
        /// <summary>Always <c>a2a</c>.</summary>
        public const string RpcSystem = "rpc.system";

        /// <summary>The operation (an <see cref="A2AProtocol.Methods"/> name).</summary>
        public const string Operation = "rpc.method";

        /// <summary>The protocol binding: <c>JSONRPC</c>, <c>HTTP+JSON</c> or <c>GRPC</c> (see <see cref="ProtocolBindings"/>).</summary>
        public const string Binding = "a2a.binding";

        public const string TaskId = "a2a.task.id";
        public const string ContextId = "a2a.context.id";

        /// <summary>The tenant; set only when not the default (empty) tenant.</summary>
        public const string Tenant = "a2a.tenant";

        /// <summary>The task state after the operation or run (for example <c>Completed</c>).</summary>
        public const string TaskState = "a2a.task.state";

        /// <summary>The <see cref="A2AErrorCode"/> name of a failed operation (for example <c>TaskNotFound</c>).</summary>
        public const string ErrorCode = "a2a.error.code";

        /// <summary>OpenTelemetry <c>error.type</c>: the error code name, or the exception type for a non-A2A error.</summary>
        public const string ErrorType = "error.type";

        /// <summary>Number of events a stream sent or received.</summary>
        public const string StreamEventCount = "a2a.stream.event_count";

        /// <summary>Agent run kind: <c>execute</c> or <c>cancel</c>.</summary>
        public const string ExecutionKind = "a2a.execution.kind";

        /// <summary>True when the agent run hit <c>MaxTaskDuration</c>.</summary>
        public const string TimedOut = "a2a.execution.timed_out";

        public const string PushConfigId = "a2a.push.config.id";

        /// <summary>Event store sequence of the delivered event.</summary>
        public const string PushSequence = "a2a.push.sequence";

        /// <summary>Delivery attempts made.</summary>
        public const string PushAttempts = "a2a.push.attempts";

        /// <summary>The webhook host (never the full URL, which may carry secrets).</summary>
        public const string ServerAddress = "server.address";

        public const string HttpStatusCode = "http.response.status_code";
    }

    /// <summary>Span event names. Stream events are span events on the stream's span, not child spans.</summary>
    public static class Events
    {
        /// <summary>One stream event. Tags: <c>a2a.event.kind</c>, <c>a2a.event.sequence</c> (server), <c>a2a.task.state</c> (status updates).</summary>
        public const string StreamEvent = "a2a.stream.event";

        /// <summary>A client stream reconnected (or a webhook delivery attempt failed and is retried).</summary>
        public const string Retry = "a2a.retry";

        public const string EventKind = "a2a.event.kind";
        public const string EventSequence = "a2a.event.sequence";
    }

    /// <summary>
    /// Writes the W3C <c>traceparent</c> (and <c>tracestate</c>, if any) of <paramref name="context"/> through
    /// <paramref name="setHeader"/>. Does nothing for an empty context.
    /// </summary>
    public static void InjectTraceContext(ActivityContext context, Action<string, string> setHeader)
    {
        ArgumentNullException.ThrowIfNull(setHeader);
        if (context.TraceId == default || context.SpanId == default) return;
        var flags = (context.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
        setHeader(TraceParentHeader, $"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{flags}");
        if (!string.IsNullOrEmpty(context.TraceState)) setHeader(TraceStateHeader, context.TraceState);
    }

    /// <summary>Writes the trace context of <paramref name="activity"/> (see <see cref="InjectTraceContext(ActivityContext, Action{string, string})"/>).</summary>
    public static void InjectTraceContext(Activity? activity, Action<string, string> setHeader)
    {
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C) return;
        InjectTraceContext(activity.Context, setHeader);
    }

    /// <summary>
    /// Parses W3C <c>traceparent</c> and <c>tracestate</c> header values. Returns false (and an empty context) when
    /// <paramref name="traceParent"/> is missing or invalid.
    /// </summary>
    public static bool TryExtractTraceContext(string? traceParent, string? traceState, out ActivityContext context)
    {
        context = default;
        if (string.IsNullOrWhiteSpace(traceParent)) return false;
        return ActivityContext.TryParse(traceParent.Trim(), string.IsNullOrWhiteSpace(traceState) ? null : traceState.Trim(), isRemote: true, out context);
    }

    // ---- helpers shared by the SDK packages ----

    /// <summary>Stream event kind for <see cref="Events.EventKind"/>.</summary>
    public static string EventKindOf(StreamResponse evt) => evt switch
    {
        TaskEvent => "task",
        MessageEvent => "message",
        StatusUpdateEvent => "status_update",
        ArtifactUpdateEvent => "artifact_update",
        _ => "unknown",
    };

    /// <summary>
    /// Records a failure on <paramref name="activity"/>: error status, <see cref="Tags.ErrorType"/>, and for an
    /// <see cref="A2AException"/> its <see cref="Tags.ErrorCode"/>. The exception message is not recorded (it may echo
    /// request content). A cancellation the caller asked for (<paramref name="callerCanceled"/>) is not an error.
    /// </summary>
    public static void RecordError(Activity? activity, Exception error, bool callerCanceled = false)
    {
        if (activity is null) return;
        if (error is OperationCanceledException && callerCanceled)
        {
            activity.SetTag(ErrorTypeCanceledTag, true);
            return;
        }
        var type = error is A2AException a2a ? a2a.Code.ToString() : error.GetType().FullName ?? error.GetType().Name;
        if (error is A2AException e) activity.SetTag(Tags.ErrorCode, e.Code.ToString());
        activity.SetTag(Tags.ErrorType, type);
        activity.SetStatus(ActivityStatusCode.Error, type);
    }

    /// <summary>Set (true) on a span whose operation the caller canceled. Not an error.</summary>
    public const string ErrorTypeCanceledTag = "a2a.canceled";

    /// <summary>Sets the common operation tags.</summary>
    public static void SetOperationTags(Activity? activity, string operation, string? binding, string? tenant, string? taskId = null, string? contextId = null)
    {
        if (activity is null) return;
        activity.SetTag(Tags.RpcSystem, "a2a");
        activity.SetTag(Tags.Operation, operation);
        if (!string.IsNullOrEmpty(binding)) activity.SetTag(Tags.Binding, binding);
        if (!string.IsNullOrEmpty(tenant)) activity.SetTag(Tags.Tenant, tenant);
        SetTask(activity, taskId, contextId);
    }

    /// <summary>Sets <see cref="Tags.TaskId"/> and <see cref="Tags.ContextId"/> when known.</summary>
    public static void SetTask(Activity? activity, string? taskId, string? contextId = null)
    {
        if (activity is null) return;
        if (!string.IsNullOrEmpty(taskId)) activity.SetTag(Tags.TaskId, taskId);
        if (!string.IsNullOrEmpty(contextId)) activity.SetTag(Tags.ContextId, contextId);
    }

    /// <summary>Sets the task tags from an operation result (a task, a message, a stream event).</summary>
    public static void SetResult(Activity? activity, object? result)
    {
        switch (result)
        {
            case AgentTask t:
                SetTask(activity, t.Id, t.ContextId);
                activity?.SetTag(Tags.TaskState, t.Status?.State.ToString());
                break;
            case Message m:
                SetTask(activity, m.TaskId, m.ContextId);
                break;
            case TaskResponse r: SetResult(activity, r.Task); break;
            case MessageResponse r: SetResult(activity, r.Message); break;
            case TaskEvent e: SetResult(activity, e.Task); break;
            case MessageEvent e: SetResult(activity, e.Message); break;
            case StatusUpdateEvent { StatusUpdate: var su }:
                SetTask(activity, su.TaskId, su.ContextId);
                activity?.SetTag(Tags.TaskState, su.Status?.State.ToString());
                break;
            case ArtifactUpdateEvent { ArtifactUpdate: var au }:
                SetTask(activity, au.TaskId, au.ContextId);
                break;
            case TaskPushNotificationConfig c:
                SetTask(activity, c.TaskId);
                activity?.SetTag(Tags.PushConfigId, c.Id);
                break;
        }
    }

    /// <summary>Adds a <see cref="Events.StreamEvent"/> span event for <paramref name="evt"/>.</summary>
    public static void AddStreamEvent(Activity? activity, StreamResponse evt, long? sequence = null)
    {
        if (activity is null || !activity.IsAllDataRequested) return;
        var tags = new ActivityTagsCollection { [Events.EventKind] = EventKindOf(evt) };
        if (sequence is { } s) tags[Events.EventSequence] = s;
        if (evt is StatusUpdateEvent { StatusUpdate.Status.State: var state }) tags[Tags.TaskState] = state.ToString();
        activity.AddEvent(new ActivityEvent(Events.StreamEvent, tags: tags));
    }
}
