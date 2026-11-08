using System.Collections.Concurrent;
using System.Diagnostics;

namespace A2A.Client.Telemetry;

/// <summary>
/// The name of the client's <see cref="ActivitySource"/>, for hosts that subscribe to it (for example OpenTelemetry's <c>AddSource</c>).
/// </summary>
/// <remarks>
/// One span per call, named <c>a2a.client.{operation}</c> (for example <c>a2a.client.GetTask</c>), kind
/// <see cref="ActivityKind.Client"/>, with the tags <c>a2a.operation</c> and <c>a2a.outcome</c> (<c>ok</c>, <c>canceled</c>, or
/// <c>error:{A2AErrorCode}</c>). A unary call's span covers its retries; a streaming call's span covers opening the stream.
/// While the span is current, <see cref="HttpClient"/> and Grpc.Net.Client send it to the server as <c>traceparent</c>.
/// </remarks>
public static class A2AClientTelemetry
{
    /// <summary>The name of the client's <see cref="ActivitySource"/>: <c>A2A.Client</c>.</summary>
    public const string ActivitySourceName = "A2A.Client";

    internal static readonly ActivitySource Source = new(ActivitySourceName, typeof(A2AClientTelemetry).Assembly.GetName().Version?.ToString());

    private static readonly ConcurrentDictionary<string, string> SpanNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<A2AErrorCode, string> ErrorOutcomes = new();

    /// <summary>
    /// Starts the span of a call to <paramref name="operation"/> and makes it current; null when nobody listens. Call it
    /// from an async method (or iterator), so the current activity does not leak to the caller.
    /// </summary>
    internal static Activity? Start(string operation)
    {
        if (!Source.HasListeners()) return null;
        var activity = Source.StartActivity(SpanNames.GetOrAdd(operation, static o => "a2a.client." + o), ActivityKind.Client);
        if (activity is { IsAllDataRequested: true }) activity.SetTag("a2a.operation", operation);
        return activity;
    }

    /// <summary>Records the outcome and stops the span. Null passes.</summary>
    internal static void Stop(Activity? activity, Exception? error)
    {
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            var outcome = Outcome(error);
            activity.SetTag("a2a.outcome", outcome);
            if (error is not null && error is not OperationCanceledException) activity.SetStatus(ActivityStatusCode.Error);
        }
        activity.Dispose();
    }

    /// <summary><c>ok</c>, <c>canceled</c>, or <c>error:{code}</c>; an exception that is not an <see cref="A2AException"/> (a network failure, for example) is <c>error:Internal</c>.</summary>
    internal static string Outcome(Exception? error) => error switch
    {
        null => "ok",
        A2AException a2a => ErrorOutcomes.GetOrAdd(a2a.Code, static c => "error:" + c),
        OperationCanceledException => "canceled",
        _ => ErrorOutcomes.GetOrAdd(A2AErrorCode.Internal, static c => "error:" + c),
    };
}
