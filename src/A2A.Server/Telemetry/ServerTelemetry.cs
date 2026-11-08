using System.Diagnostics;
using System.Diagnostics.Metrics;
using A2A.Diagnostics;

namespace A2A.Server.Telemetry;

/// <summary>
/// The server's <see cref="ActivitySource"/> and <see cref="Meter"/> (both named <see cref="A2ATelemetry.ServerSourceName"/>),
/// its counters, and the span helpers the pipeline, the execution engine and the push sender share. Names and tags are the
/// public constants of <see cref="A2ATelemetry"/>.
/// </summary>
/// <remarks>
/// Spans: one per operation (named after the method, by <see cref="TelemetryFilter"/>), one per agent run
/// (<see cref="A2ATelemetry.SpanNames.AgentExecution"/>) and one per webhook delivery (<see cref="A2ATelemetry.SpanNames.PushDelivery"/>).
/// Counters (<see cref="A2ATelemetry.Metrics"/>): requests by operation and outcome, events stored, streams opened, push
/// delivery attempts by outcome, dead letters. Nothing here allocates while nobody listens.
/// </remarks>
internal static class ServerTelemetry
{
    private static readonly string? Version = typeof(ServerTelemetry).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource Source = new(A2ATelemetry.ServerSourceName, Version);
    public static readonly Meter Meter = new(A2ATelemetry.ServerMeterName, Version);

    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>(A2ATelemetry.Metrics.Requests, description: "A2A operations handled, by operation and outcome.");
    public static readonly Counter<long> Events =
        Meter.CreateCounter<long>(A2ATelemetry.Metrics.Events, description: "Task events stored.");
    public static readonly Counter<long> StreamsOpened =
        Meter.CreateCounter<long>(A2ATelemetry.Metrics.StreamsOpened, description: "Event streams admitted (SendStreamingMessage and SubscribeToTask).");
    public static readonly Counter<long> PushDeliveries =
        Meter.CreateCounter<long>(A2ATelemetry.Metrics.PushDeliveries, description: "Push delivery attempts by outcome: sent, retried or dead_lettered.");
    public static readonly Counter<long> PushDeadLetters =
        Meter.CreateCounter<long>(A2ATelemetry.Metrics.PushDeadLetters, description: "Push deliveries that failed for good and were moved to the dead letters.");

    /// <summary>
    /// The outcome of a call: <see cref="A2ATelemetry.Outcomes.Ok"/>; <see cref="A2ATelemetry.Outcomes.Canceled"/> for an
    /// <see cref="OperationCanceledException"/> when the caller canceled (<paramref name="cancellationToken"/>); otherwise
    /// <see cref="A2ATelemetry.Outcomes.Error"/> (a cancellation the caller did not ask for, such as a server-side timeout, is an error).
    /// </summary>
    public static string Outcome(Exception? error, CancellationToken cancellationToken) => error switch
    {
        null => A2ATelemetry.Outcomes.Ok,
        OperationCanceledException when cancellationToken.IsCancellationRequested => A2ATelemetry.Outcomes.Canceled,
        _ => A2ATelemetry.Outcomes.Error,
    };

    /// <summary>
    /// Starts the span of an operation, or returns null when nobody samples it. Its parent is the current activity (for example
    /// ASP.NET Core's request activity), or the caller's trace context from <see cref="ServerCallContext.TraceParent"/> when no
    /// activity of that trace is current; it is a server span unless it nests under a local one.
    /// </summary>
    public static Activity? StartOperation(string spanName, string operation, ServerCallContext call, string? taskId)
    {
        var current = Activity.Current;
        var remote = call.TraceParent;
        var activity = remote.TraceId != default && (current is null || current.TraceId != remote.TraceId)
            ? Source.StartActivity(spanName, ActivityKind.Server, remote)
            : Source.StartActivity(spanName, current is null ? ActivityKind.Server : ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true }) A2ATelemetry.SetOperationTags(activity, operation, call.Binding, call.Tenant, taskId);
        return activity;
    }

    /// <summary>
    /// Starts the span of an agent run (<paramref name="kind"/>: <c>execute</c> or <c>cancel</c>) as a child of the current
    /// activity, without making it current here: the run's background tasks make it current themselves. Null when nobody listens.
    /// </summary>
    public static Activity? StartExecution(string kind, string tenant, string taskId, string contextId)
    {
        if (!Source.HasListeners()) return null;
        var previous = Activity.Current;
        var activity = Source.StartActivity(A2ATelemetry.SpanNames.AgentExecution, ActivityKind.Internal);
        Activity.Current = previous;
        if (activity is null) return null;
        activity.SetTag(A2ATelemetry.Tags.ExecutionKind, kind);
        A2ATelemetry.SetTask(activity, taskId, contextId);
        if (tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, tenant);
        return activity;
    }
}
