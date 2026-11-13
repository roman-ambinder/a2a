using System.Diagnostics;
using System.Runtime.CompilerServices;
using A2A.Diagnostics;
using A2A.Server.Pipeline;
using A2A.Server.Streaming;

namespace A2A.Server.Telemetry;

/// <summary>
///     The outermost built-in step of the operation pipeline: one span named after the operation (
///     <see cref="A2ATelemetry" />) and
///     one <c>a2a.server.requests</c> measurement per call, around the host filters, authorization and the operation. A
///     refused
///     call (unauthenticated, denied, or stopped by a filter) gets a span with its error.
/// </summary>
/// <remarks>
///     <para>
///         Two entry points with the same behavior: <see cref="InvokeAsync" /> when the pipeline has a chain of host
///         filters, and
///         <see cref="RunAsync{TRequest, TResponse}" /> for the pipeline's fast path without host filters, where it calls
///         authorization
///         and the operation directly (no <see cref="A2AOperationContext" />, no closure). Both are skipped entirely while
///         nobody
///         listens (<see cref="IsEnabled" />). Telemetry reads the request (operation, tenant, task id) and the outcome
///         only: it never
///         changes a request or a result.
///     </para>
///     <para>
///         A streaming result keeps the span open until the stream ends: each event is an
///         <see cref="A2ATelemetry.Events.StreamEvent" />
///         span event (not a child span), and the span is current while the stream produces each event, so the agent run
///         it starts
///         is its child. A caller's cancellation, or a reader that stops before the end, is
///         <see cref="A2ATelemetry.Outcomes.Canceled" />
///         (tag <see cref="A2ATelemetry.ErrorTypeCanceledTag" />), not an error.
///     </para>
/// </remarks>
internal sealed class TelemetryFilter : IA2AOperationFilter
{
    public static readonly TelemetryFilter Instance = new();

    private TelemetryFilter()
    {
    }

    /// <summary>
    ///     True when the span source or the requests counter has a listener. When false, calls run with no telemetry at
    ///     all.
    /// </summary>
    public static bool IsEnabled => ServerTelemetry.Source.HasListeners() || ServerTelemetry.Requests.Enabled;

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next,
        CancellationToken cancellationToken)
    {
        return IsEnabled
            ? InvokeObservedAsync(context, request, next, cancellationToken)
            : next(context, request, cancellationToken);
    }

    private static async ValueTask<object?> InvokeObservedAsync(A2AOperationContext context, object request,
        A2AOperationDelegate next, CancellationToken ct)
    {
        var activity = Start(context.Operation, context.Call, request);
        object? result;
        try
        {
            result = await next(context, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stop(activity, context.Operation, context.Call, ex, ct);
            throw;
        }

        if (result is IAsyncEnumerable<SequencedEvent> events)
            return Observe(events, activity, context.Operation, context.Call,
                CancellationToken.None); // the reader passes its token (WithCancellation)
        A2ATelemetry.SetResult(activity, result);
        Stop(activity, context.Operation, context.Call, null, ct);
        return result;
    }

    /// <summary>
    ///     The fast path with telemetry: authorization, then the operation, inside the span. The pipeline calls this only when
    ///     <see cref="IsEnabled" />; otherwise it runs authorization and the operation itself.
    /// </summary>
    public static async ValueTask<TResponse> RunAsync<TRequest, TResponse>(AuthorizationFilter authorization,
        A2AOperationDescriptor descriptor,
        ServerCallContext call, TRequest request,
        Func<TRequest, ServerCallContext, CancellationToken, ValueTask<TResponse>> operation,
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
            Stop(activity, descriptor, call, ex, cancellationToken);
            throw;
        }

        if (result is IAsyncEnumerable<SequencedEvent> events)
            return (TResponse)Observe(events, activity, descriptor, call,
                CancellationToken.None); // the reader passes its token (WithCancellation)
        A2ATelemetry.SetResult(activity, result);
        Stop(activity, descriptor, call, null, cancellationToken);
        return result;
    }

    /// <summary>
    ///     The same events. The span is current while the stream produces each event (so the work it starts runs inside it),
    ///     but
    ///     never for the reader. The span stops (and the call is counted) when the stream ends (<c>ok</c>), fails, or is
    ///     disposed
    ///     before its end (<c>canceled</c>: the reader stopped reading).
    /// </summary>
    private static async IAsyncEnumerable<SequencedEvent> Observe(IAsyncEnumerable<SequencedEvent> events,
        Activity? activity,
        A2AOperationDescriptor operation, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? error = null;
        var ended = false;
        var count = 0;
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    // Scoped to this iterator step: the reader's own Activity.Current is not changed.
                    if (activity is not null) Activity.Current = activity;
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex;
                    throw;
                }

                if (!moved)
                {
                    ended = true;
                    break;
                }

                var current = enumerator.Current;
                count++;
                if (activity is { IsAllDataRequested: true })
                {
                    A2ATelemetry.SetStreamResult(activity, current.Event);
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
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (activity is { IsAllDataRequested: true })
                    activity.SetTag(A2ATelemetry.Tags.StreamEventCount, count);
                if (error is null && !ended)
                    StopCanceled(activity, operation, call); // abandoned: the reader stopped first
                else Stop(activity, operation, call, error, cancellationToken);
            }
        }
    }

    /// <summary>Starts the span (null when nobody samples it) with the operation, binding, tenant and task id tags.</summary>
    private static Activity? Start(A2AOperationDescriptor operation, ServerCallContext call, object request)
    {
        return ServerTelemetry.Source.HasListeners()
            ? StartWithMessage(operation, call, request)
            : null;
    }

    private static Activity? StartWithMessage(A2AOperationDescriptor operation, ServerCallContext call, object request)
    {
        var message = (request as SendMessageRequest)?.Message;
        var activity = ServerTelemetry.StartOperation(operation.Name, operation.Name, call,
            AuthorizationFilter.TaskIdOf(request), message);
        A2ATelemetry.SetMessage(activity, message);
        return activity;
    }

    /// <summary>
    ///     Records the result (<paramref name="error" /> null) or the failure on the span and the requests counter, then
    ///     stops the span.
    /// </summary>
    private static void Stop(Activity? activity, A2AOperationDescriptor operation, ServerCallContext call,
        Exception? error, CancellationToken cancellationToken)
    {
        var outcome = ServerTelemetry.Outcome(error, cancellationToken);
        Count(operation.Name, outcome, error);
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            if (error is not null)
                A2ATelemetry.RecordError(activity, error, ReferenceEquals(outcome, A2ATelemetry.Outcomes.Canceled));
            if (call.Tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, call.Tenant);
            TagAgent(activity, call);
        }

        activity.Dispose();
    }

    /// <summary>
    ///     Tags the agent of the card resolved for this call's tenant (none when the call was refused before a card
    ///     resolved).
    /// </summary>
    private static void TagAgent(Activity activity, ServerCallContext call)
    {
        if (call.ResolvedCard is { } resolved && string.Equals(resolved.Tenant, call.Tenant, StringComparison.Ordinal))
            A2ATelemetry.SetAgent(activity, resolved.Card);
    }

    private static void StopCanceled(Activity? activity, A2AOperationDescriptor operation, ServerCallContext call)
    {
        Count(operation.Name, A2ATelemetry.Outcomes.Canceled, null);
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(A2ATelemetry.ErrorTypeCanceledTag, true);
            if (call.Tenant.Length > 0) activity.SetTag(A2ATelemetry.Tags.Tenant, call.Tenant);
            TagAgent(activity, call);
        }

        activity.Dispose();
    }

    /// <summary>One <c>a2a.server.requests</c> measurement: the operation, the outcome, and <c>error.type</c> for an error.</summary>
    internal static void Count(string operation, string outcome, Exception? error)
    {
        if (!ServerTelemetry.Requests.Enabled) return;
        if (ReferenceEquals(outcome, A2ATelemetry.Outcomes.Error) && error is not null)
        {
            ServerTelemetry.Requests.Add(1,
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.Operation, operation),
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.Outcome, outcome),
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.ErrorType, A2ATelemetry.ErrorTypeOf(error)));
            return;
        }

        ServerTelemetry.Requests.Add(1,
            new KeyValuePair<string, object?>(A2ATelemetry.Tags.Operation, operation),
            new KeyValuePair<string, object?>(A2ATelemetry.Tags.Outcome, outcome));
    }
}