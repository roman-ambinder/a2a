using System.Collections.Concurrent;
using System.Diagnostics;
using A2A.Client.Telemetry;

namespace A2A.Client.Grpc;

/// <summary>
/// The gRPC transport's spans: the same source name, span names and tags as <see cref="A2AClientTelemetry"/> (whose helpers are
/// internal to A2A.Client), so hosts subscribe to <see cref="A2AClientTelemetry.ActivitySourceName"/> once for every binding.
/// </summary>
internal static class GrpcClientTelemetry
{
    private static readonly ActivitySource Source = new(A2AClientTelemetry.ActivitySourceName, typeof(GrpcClientTelemetry).Assembly.GetName().Version?.ToString());
    private static readonly ConcurrentDictionary<string, string> SpanNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<A2AErrorCode, string> ErrorOutcomes = new();

    /// <summary>Starts the span of a call and makes it current (Grpc.Net.Client sends it as <c>traceparent</c>); null when nobody listens. Call it from an async method.</summary>
    public static Activity? Start(string operation)
    {
        if (!Source.HasListeners()) return null;
        var activity = Source.StartActivity(SpanNames.GetOrAdd(operation, static o => "a2a.client." + o), ActivityKind.Client);
        if (activity is { IsAllDataRequested: true }) activity.SetTag("a2a.operation", operation);
        return activity;
    }

    /// <summary>Records the outcome (<c>ok</c>, <c>canceled</c>, or <c>error:{code}</c>) and stops the span. Null passes.</summary>
    public static void Stop(Activity? activity, Exception? error)
    {
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            activity.SetTag("a2a.outcome", error switch
            {
                null => "ok",
                A2AException a2a => ErrorOutcomes.GetOrAdd(a2a.Code, static c => "error:" + c),
                OperationCanceledException => "canceled",
                _ => ErrorOutcomes.GetOrAdd(A2AErrorCode.Internal, static c => "error:" + c),
            });
            if (error is not null && error is not OperationCanceledException) activity.SetStatus(ActivityStatusCode.Error);
        }
        activity.Dispose();
    }
}
