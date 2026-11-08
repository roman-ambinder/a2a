using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace A2A.Server.Telemetry;

/// <summary>
/// Names of the server's <see cref="ActivitySource"/> and <see cref="Meter"/>, for hosts that subscribe to them (for example
/// OpenTelemetry's <c>AddSource</c> and <c>AddMeter</c>).
/// </summary>
/// <remarks>
/// <para>
/// Spans: one per operation, named <c>a2a.server.{operation}</c> (for example <c>a2a.server.SendMessage</c>), kind
/// <see cref="ActivityKind.Server"/>, with the tags <c>a2a.operation</c>, <c>a2a.tenant</c>, <c>a2a.task_id_hash</c> (the first
/// 16 hex characters of the SHA-256 of the task id, when the request names a task) and <c>a2a.outcome</c> (<c>ok</c>,
/// <c>canceled</c>, or <c>error:{A2AErrorCode}</c>). A streaming operation's span lasts until its stream ends. Spans carry no
/// request or response bodies and no principal.
/// </para>
/// <para>
/// Counters: <c>a2a.server.requests</c> (tags <c>a2a.operation</c>, <c>a2a.outcome</c>), <c>a2a.server.events</c> (events
/// stored), <c>a2a.server.streams.opened</c> (streams admitted) and <c>a2a.server.push.deliveries</c> (tag
/// <c>a2a.outcome</c>: <c>delivered</c> or <c>failed</c>, one per webhook delivery after its last attempt).
/// </para>
/// </remarks>
public static class A2AServerTelemetry
{
    /// <summary>The name of the server's <see cref="ActivitySource"/>: <c>A2A.Server</c>.</summary>
    public const string ActivitySourceName = "A2A.Server";

    /// <summary>The name of the server's <see cref="Meter"/>: <c>A2A.Server</c>.</summary>
    public const string MeterName = "A2A.Server";

    internal const string OperationTag = "a2a.operation";
    internal const string TenantTag = "a2a.tenant";
    internal const string TaskIdHashTag = "a2a.task_id_hash";
    internal const string OutcomeTag = "a2a.outcome";
    internal const string Ok = "ok";
    internal const string Canceled = "canceled";

    private static readonly string? Version = typeof(A2AServerTelemetry).Assembly.GetName().Version?.ToString();

    internal static readonly ActivitySource Source = new(ActivitySourceName, Version);
    internal static readonly Meter Meter = new(MeterName, Version);

    internal static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("a2a.server.requests", description: "A2A operations handled, by operation and outcome.");
    internal static readonly Counter<long> Events =
        Meter.CreateCounter<long>("a2a.server.events", description: "Task events stored.");
    internal static readonly Counter<long> StreamsOpened =
        Meter.CreateCounter<long>("a2a.server.streams.opened", description: "Event streams admitted (SendStreamingMessage and SubscribeToTask).");
    internal static readonly Counter<long> PushDeliveries =
        Meter.CreateCounter<long>("a2a.server.push.deliveries", description: "Webhook deliveries after their last attempt, by outcome (delivered or failed).");

    private static readonly ConcurrentDictionary<A2AErrorCode, string> ErrorOutcomes = new();

    /// <summary><c>ok</c>, <c>canceled</c>, or <c>error:{code}</c>; an exception that is not an <see cref="A2AException"/> is <c>error:Internal</c>, as the bindings report it.</summary>
    internal static string Outcome(Exception? error) => error switch
    {
        null => Ok,
        A2AException a2a => ErrorOutcome(a2a.Code),
        OperationCanceledException => Canceled,
        _ => ErrorOutcome(A2AErrorCode.Internal),
    };

    private static string ErrorOutcome(A2AErrorCode code) => ErrorOutcomes.GetOrAdd(code, static c => "error:" + c);
}
