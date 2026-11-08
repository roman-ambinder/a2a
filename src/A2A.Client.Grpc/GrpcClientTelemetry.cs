using System.Collections.Concurrent;
using System.Diagnostics;
using A2A.Client.Telemetry;
using Grpc.Core;

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

    /// <summary>
    /// Records the outcome and stops the span. Null passes. The outcome is <c>ok</c>; <c>canceled</c> when the caller canceled
    /// (<paramref name="cancellationToken"/>) and the call ended as canceled (an <see cref="OperationCanceledException"/>, or
    /// gRPC status <c>Cancelled</c> mapped to an <see cref="A2AException"/>); otherwise <c>error:{code}</c>, and
    /// <c>error:Internal</c> for an exception that is not an <see cref="A2AException"/>.
    /// </summary>
    public static void Stop(Activity? activity, Exception? error, CancellationToken cancellationToken)
    {
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            var outcome = error switch
            {
                null => "ok",
                OperationCanceledException or A2AException { InnerException: RpcException { StatusCode: StatusCode.Cancelled } }
                    when cancellationToken.IsCancellationRequested => "canceled",
                A2AException a2a => ErrorOutcomes.GetOrAdd(a2a.Code, static c => "error:" + c),
                _ => ErrorOutcomes.GetOrAdd(A2AErrorCode.Internal, static c => "error:" + c),
            };
            activity.SetTag("a2a.outcome", outcome);
            if (outcome is not ("ok" or "canceled")) activity.SetStatus(ActivityStatusCode.Error);
        }
        activity.Dispose();
    }
}
