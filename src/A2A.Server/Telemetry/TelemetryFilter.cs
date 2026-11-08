using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Streaming;

namespace A2A.Server.Telemetry;

/// <summary>
/// The outermost built-in step of the operation pipeline: one span (<see cref="A2AServerTelemetry"/>) and one
/// <c>a2a.server.requests</c> measurement per call, around the host filters, authorization and the operation. A refused
/// call (unauthenticated, denied, or stopped by a filter) gets a span with its error outcome.
/// </summary>
/// <remarks>
/// Two entry points with the same behavior: <see cref="InvokeAsync"/> when the pipeline has a chain of host filters, and
/// <see cref="RunAsync{TRequest, TResponse}"/> for the pipeline's fast path without host filters, where it calls authorization
/// and the operation directly (no <see cref="A2AOperationContext"/>, no closure). Both are skipped entirely while nobody
/// listens (<see cref="IsEnabled"/>). A streaming result keeps the span open until the stream ends. Telemetry reads the request
/// (operation, tenant, task id) and the outcome only: it never changes a request or a result.
/// </remarks>
internal sealed class TelemetryFilter : IA2AOperationFilter
{
    public static readonly TelemetryFilter Instance = new();

    private static readonly string[] SpanNames = CreateSpanNames();

    private TelemetryFilter() { }

    /// <summary>True when the span source or the requests counter has a listener. When false, calls run with no telemetry at all.</summary>
    public static bool IsEnabled => A2AServerTelemetry.Source.HasListeners() || A2AServerTelemetry.Requests.Enabled;

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken) =>
        IsEnabled ? InvokeObservedAsync(context, request, next, cancellationToken) : next(context, request, cancellationToken);

    private static async ValueTask<object?> InvokeObservedAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken ct)
    {
        var activity = Start(context.Operation, context.Call, request);
        object? result;
        try
        {
            result = await next(context, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stop(activity, context.Operation, ex);
            throw;
        }
        if (result is IAsyncEnumerable<SequencedEvent> events) return Observe(events, activity, context.Operation, CancellationToken.None); // the reader passes its token (WithCancellation)
        Stop(activity, context.Operation, null);
        return result;
    }

    /// <summary>
    /// The fast path with telemetry: authorization, then the operation, inside the span. The pipeline calls this only when
    /// <see cref="IsEnabled"/>; otherwise it runs authorization and the operation itself.
    /// </summary>
    public static async ValueTask<TResponse> RunAsync<TRequest, TResponse>(AuthorizationFilter authorization, A2AOperationDescriptor descriptor,
        ServerCallContext call, TRequest request, Func<TRequest, ServerCallContext, CancellationToken, ValueTask<TResponse>> operation,
        CancellationToken cancellationToken) where TRequest : class
    {
        var activity = Start(descriptor, call, request);
        TResponse result;
        try
        {
            await authorization.RequireAsync(descriptor, call, request, cancellationToken).ConfigureAwait(false);
            result = await operation(request, call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stop(activity, descriptor, ex);
            throw;
        }
        if (result is IAsyncEnumerable<SequencedEvent> events) return (TResponse)Observe(events, activity, descriptor, CancellationToken.None); // the reader passes its token (WithCancellation)
        Stop(activity, descriptor, null);
        return result;
    }

    /// <summary>The same events; the span stops (and the call is counted) when the stream ends, fails or is disposed.</summary>
    private static async IAsyncEnumerable<SequencedEvent> Observe(IAsyncEnumerable<SequencedEvent> events, Activity? activity,
        A2AOperationDescriptor operation, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? error = null;
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex;
                    throw;
                }
                if (!moved) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            finally { Stop(activity, operation, error); }
        }
    }

    /// <summary>Starts the span (null when nobody samples it) with the operation, tenant and task id hash tags.</summary>
    private static Activity? Start(A2AOperationDescriptor operation, ServerCallContext call, object request)
    {
        var activity = A2AServerTelemetry.Source.StartActivity(SpanName(operation), ActivityKind.Server);
        if (activity is null || !activity.IsAllDataRequested) return activity;
        activity.SetTag(A2AServerTelemetry.OperationTag, operation.Name);
        activity.SetTag(A2AServerTelemetry.TenantTag, call.Tenant);
        if (AuthorizationFilter.TaskIdOf(request) is { Length: > 0 } taskId) activity.SetTag(A2AServerTelemetry.TaskIdHashTag, HashTaskId(taskId));
        return activity;
    }

    /// <summary>Records the outcome on the span and the requests counter, then stops the span.</summary>
    private static void Stop(Activity? activity, A2AOperationDescriptor operation, Exception? error)
    {
        var outcome = A2AServerTelemetry.Outcome(error);
        if (A2AServerTelemetry.Requests.Enabled)
        {
            A2AServerTelemetry.Requests.Add(1,
                new KeyValuePair<string, object?>(A2AServerTelemetry.OperationTag, operation.Name),
                new KeyValuePair<string, object?>(A2AServerTelemetry.OutcomeTag, outcome));
        }
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(A2AServerTelemetry.OutcomeTag, outcome);
            if (error is not null && !ReferenceEquals(outcome, A2AServerTelemetry.Canceled)) activity.SetStatus(ActivityStatusCode.Error);
        }
        activity.Dispose();
    }

    private static string SpanName(A2AOperationDescriptor operation) =>
        operation.Kind != A2AOperation.Extension && (uint)operation.Kind < (uint)SpanNames.Length
            ? SpanNames[(int)operation.Kind]
            : "a2a.server." + operation.Name;

    private static string[] CreateSpanNames()
    {
        var names = new string[(int)A2AOperation.Extension];
        for (var i = 0; i < names.Length; i++) names[i] = "a2a.server." + A2AOperationDescriptor.For((A2AOperation)i).Name;
        return names;
    }

    /// <summary>The first 16 hex characters (lowercase) of the SHA-256 of the UTF-8 task id: correlates calls without exposing the id.</summary>
    internal static string HashTaskId(string taskId)
    {
        var length = Encoding.UTF8.GetMaxByteCount(taskId.Length);
        byte[]? rented = null;
        var utf8 = length <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var written = Encoding.UTF8.GetBytes(taskId, utf8);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8[..written], hash);
            return Convert.ToHexString(hash[..8]).ToLowerInvariant();
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
