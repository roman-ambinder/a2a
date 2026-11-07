using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;
using A2A.Server.Streaming;

namespace A2A.Server;

/// <summary>The server's <see cref="ActivitySource"/> (<see cref="A2ATelemetry.ServerSourceName"/>) and span helpers.</summary>
internal static class ServerTelemetry
{
    public static readonly ActivitySource Source = new(A2ATelemetry.ServerSourceName,
        typeof(ServerTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// Starts the span of an operation. Its parent is the current activity (for example ASP.NET Core's request activity),
    /// or the caller's trace context from <see cref="ServerCallContext.TraceParent"/> when no activity of that trace is current.
    /// </summary>
    public static Activity? StartOperation(string operation, ServerCallContext call, string? taskId)
    {
        if (!Source.HasListeners()) return null;
        var current = Activity.Current;
        var remote = call.TraceParent;
        Activity? activity;
        if (remote.TraceId != default && (current is null || current.TraceId != remote.TraceId))
            activity = Source.StartActivity(A2ATelemetry.SpanNames.Operation(operation), ActivityKind.Server, remote);
        else
            activity = Source.StartActivity(A2ATelemetry.SpanNames.Operation(operation), current is null ? ActivityKind.Server : ActivityKind.Internal);
        A2ATelemetry.SetOperationTags(activity, operation, call.Binding, call.Tenant, taskId);
        return activity;
    }

    /// <summary>Runs a unary operation in its span.</summary>
    public static Task<T> TraceAsync<T>(string operation, ServerCallContext call, string? taskId, Func<Task<T>> body, CancellationToken ct) =>
        Source.HasListeners() ? TraceCoreAsync(operation, call, taskId, body, ct) : body();

    public static Task TraceAsync(string operation, ServerCallContext call, string? taskId, Func<Task> body, CancellationToken ct) =>
        Source.HasListeners()
            ? TraceCoreAsync<object?>(operation, call, taskId, async () => { await body().ConfigureAwait(false); return null; }, ct)
            : body();

    private static async Task<T> TraceCoreAsync<T>(string operation, ServerCallContext call, string? taskId, Func<Task<T>> body, CancellationToken ct)
    {
        using var activity = StartOperation(operation, call, taskId);
        try
        {
            var result = await body().ConfigureAwait(false);
            A2ATelemetry.SetResult(activity, result);
            return result;
        }
        catch (Exception ex)
        {
            A2ATelemetry.RecordError(activity, ex, ct.IsCancellationRequested);
            throw;
        }
        finally
        {
            if (activity is not null && call.Tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, call.Tenant);
        }
    }

    /// <summary>
    /// Runs a stream in one span: the span covers the whole stream, and each event is a span event. The span is the current
    /// activity while the source runs (so agent runs it starts are its children), but never leaks to the consumer.
    /// </summary>
    public static IAsyncEnumerable<SequencedEvent> TraceStream(string operation, ServerCallContext call, string? taskId, IAsyncEnumerable<SequencedEvent> source) =>
        Source.HasListeners() ? TraceStreamCoreAsync(operation, call, taskId, source) : source;

    private static async IAsyncEnumerable<SequencedEvent> TraceStreamCoreAsync(string operation, ServerCallContext call, string? taskId,
        IAsyncEnumerable<SequencedEvent> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activity = StartOperation(operation, call, taskId);
        var count = 0;
        var events = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                if (activity is not null) Activity.Current = activity; // each MoveNext of this iterator starts from the consumer's context
                bool more;
                try
                {
                    more = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    A2ATelemetry.RecordError(activity, ex, ct.IsCancellationRequested);
                    throw;
                }
                if (!more) break;
                var current = events.Current;
                count++;
                if (activity is not null)
                {
                    A2ATelemetry.SetResult(activity, current.Event);
                    A2ATelemetry.AddStreamEvent(activity, current.Event, current.Sequence);
                }
                yield return current;
            }
        }
        finally
        {
            if (activity is not null) Activity.Current = activity;
            try
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (activity is not null)
                {
                    activity.SetTag(A2ATelemetry.Tags.StreamEventCount, count);
                    if (call.Tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, call.Tenant);
                    activity.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Starts the span of an agent run as a child of the current activity, without making it current here: the run's
    /// background tasks make it current themselves.
    /// </summary>
    public static Activity? StartExecution(string kind, ServerCallContext call, string taskId, string contextId)
    {
        if (!Source.HasListeners()) return null;
        var previous = Activity.Current;
        var activity = Source.StartActivity(A2ATelemetry.SpanNames.AgentExecution, ActivityKind.Internal);
        Activity.Current = previous;
        if (activity is null) return null;
        activity.SetTag(A2ATelemetry.Tags.ExecutionKind, kind);
        A2ATelemetry.SetTask(activity, taskId, contextId);
        if (call.Tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, call.Tenant);
        return activity;
    }
}
