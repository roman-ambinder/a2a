using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;

namespace A2A.Client;

/// <summary>The client's <see cref="ActivitySource" /> (<see cref="A2ATelemetry.ClientSourceName" />) and span helpers.</summary>
internal static class ClientTelemetry
{
    public static readonly ActivitySource Source = new(A2ATelemetry.ClientSourceName,
        typeof(ClientTelemetry).Assembly.GetName().Version?.ToString());

    private static Activity? Start(string operation, A2AClient client, string? tenant, string? taskId, Message? message)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Client);
        A2ATelemetry.SetOperationTags(activity, operation, client.TelemetryBinding, tenant, taskId,
            protocolVersion: client.TelemetryProtocolVersion);
        A2ATelemetry.SetAgent(activity, client.TelemetryCard);
        A2ATelemetry.SetMessage(activity, message);
        return activity;
    }

    /// <summary>
    ///     Runs a unary call in its span. The span is the current activity while the transport sends, so the request carries
    ///     its <c>traceparent</c>.
    /// </summary>
    public static Task<T> TraceAsync<T>(string operation, A2AClient client, string? tenant, string? taskId,
        Func<Task<T>> body, CancellationToken ct, Message? message = null)
    {
        return Source.HasListeners() ? TraceCoreAsync(operation, client, tenant, taskId, body, message, ct) : body();
    }

    public static Task TraceAsync(string operation, A2AClient client, string? tenant, string? taskId, Func<Task> body,
        CancellationToken ct)
    {
        return Source.HasListeners()
            ? TraceCoreAsync<object?>(operation, client, tenant, taskId, async () =>
            {
                await body().ConfigureAwait(false);
                return null;
            }, null, ct)
            : body();
    }

    private static async Task<T> TraceCoreAsync<T>(string operation, A2AClient client, string? tenant, string? taskId,
        Func<Task<T>> body, Message? message, CancellationToken ct)
    {
        using var activity = Start(operation, client, tenant, taskId, message);
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
    ///     Runs a stream in one span that covers the whole stream, reconnects included; each event is a span event. The span
    ///     is
    ///     current while the source runs, never for the consumer. <paramref name="callerToken" /> is the token the caller
    ///     passed to the method: a cancellation through it or through the enumerator's token (<c>WithCancellation</c>) is
    ///     the caller's, not an error.
    /// </summary>
    public static IAsyncEnumerable<StreamResponse> TraceStream(string operation, A2AClient client, string? tenant,
        string? taskId, IAsyncEnumerable<StreamResponse> source, CancellationToken callerToken, Message? message = null)
    {
        return Source.HasListeners()
            ? TraceStreamCoreAsync(operation, client, tenant, taskId, source, message, callerToken)
            : source;
    }

    private static async IAsyncEnumerable<StreamResponse> TraceStreamCoreAsync(string operation, A2AClient client,
        string? tenant, string? taskId,
        IAsyncEnumerable<StreamResponse> source, Message? message, CancellationToken callerToken,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var activity = Start(operation, client, tenant, taskId, message);
        var count = 0;
        var events = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                if (activity is not null)
                    Activity.Current = activity; // each MoveNext of this iterator starts from the consumer's context
                bool more;
                try
                {
                    more = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    A2ATelemetry.RecordError(activity, ex,
                        ct.IsCancellationRequested || callerToken.IsCancellationRequested);
                    throw;
                }

                if (!more) break;
                count++;
                if (activity is not null)
                {
                    A2ATelemetry.SetStreamResult(activity, events.Current);
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

    /// <summary>Adds a <see cref="A2ATelemetry.Events.Retry" /> event to the current client span (a stream reconnect).</summary>
    public static void RecordRetry(int attempt)
    {
        if (Activity.Current is { } activity && ReferenceEquals(activity.Source, Source))
            activity.AddEvent(new ActivityEvent(A2ATelemetry.Events.Retry,
                tags: new ActivityTagsCollection { ["a2a.retry.attempt"] = attempt }));
    }
}