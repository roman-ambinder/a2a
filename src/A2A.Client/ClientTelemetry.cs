using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;

namespace A2A.Client;

/// <summary>The client's <see cref="ActivitySource"/> (<see cref="A2ATelemetry.ClientSourceName"/>) and span helpers.</summary>
internal static class ClientTelemetry
{
    public static readonly ActivitySource Source = new(A2ATelemetry.ClientSourceName,
        typeof(ClientTelemetry).Assembly.GetName().Version?.ToString());

    private static Activity? Start(string operation, string? binding, string? tenant, string? taskId)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Client);
        A2ATelemetry.SetOperationTags(activity, operation, binding, tenant, taskId);
        return activity;
    }

    /// <summary>
    /// Runs a unary call in its span. The span is the current activity while the transport sends, so the request carries
    /// its <c>traceparent</c>.
    /// </summary>
    public static Task<T> TraceAsync<T>(string operation, string? binding, string? tenant, string? taskId, Func<Task<T>> body, CancellationToken ct) =>
        Source.HasListeners() ? TraceCoreAsync(operation, binding, tenant, taskId, body, ct) : body();

    public static Task TraceAsync(string operation, string? binding, string? tenant, string? taskId, Func<Task> body, CancellationToken ct) =>
        Source.HasListeners()
            ? TraceCoreAsync<object?>(operation, binding, tenant, taskId, async () => { await body().ConfigureAwait(false); return null; }, ct)
            : body();

    private static async Task<T> TraceCoreAsync<T>(string operation, string? binding, string? tenant, string? taskId, Func<Task<T>> body, CancellationToken ct)
    {
        using var activity = Start(operation, binding, tenant, taskId);
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
    }

    /// <summary>
    /// Runs a stream in one span that covers the whole stream, reconnects included; each event is a span event. The span is
    /// current while the source runs, never for the consumer.
    /// </summary>
    public static IAsyncEnumerable<StreamResponse> TraceStream(string operation, string? binding, string? tenant, string? taskId, IAsyncEnumerable<StreamResponse> source) =>
        Source.HasListeners() ? TraceStreamCoreAsync(operation, binding, tenant, taskId, source) : source;

    private static async IAsyncEnumerable<StreamResponse> TraceStreamCoreAsync(string operation, string? binding, string? tenant, string? taskId,
        IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activity = Start(operation, binding, tenant, taskId);
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
                count++;
                if (activity is not null)
                {
                    A2ATelemetry.SetResult(activity, events.Current);
                    A2ATelemetry.AddStreamEvent(activity, events.Current);
                }
                yield return events.Current;
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
                activity?.SetTag(A2ATelemetry.Tags.StreamEventCount, count);
                activity?.Dispose();
            }
        }
    }

    /// <summary>Adds a <see cref="A2ATelemetry.Events.Retry"/> event to the current client span (a stream reconnect).</summary>
    public static void RecordRetry(int attempt)
    {
        if (Activity.Current is { } activity && ReferenceEquals(activity.Source, Source))
            activity.AddEvent(new ActivityEvent(A2ATelemetry.Events.Retry, tags: new ActivityTagsCollection { ["a2a.retry.attempt"] = attempt }));
    }
}
