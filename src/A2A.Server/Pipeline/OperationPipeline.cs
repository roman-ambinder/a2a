using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;

namespace A2A.Server.Pipeline;

/// <summary>
/// Runs an operation through the filters: the built-in <see cref="TelemetryFilter"/> (outermost), the
/// <see cref="A2AFilterStage.BeforeAuthorization"/> host filters, the built-in <see cref="AuthorizationFilter"/>, the
/// <see cref="A2AFilterStage.AfterAuthorization"/> host filters, then the operation.
/// </summary>
/// <remarks>
/// The chain of delegates is built once, here. A call allocates only its <see cref="A2AOperationContext"/>; the operation
/// delegate travels in the context and a shared <see cref="OperationInvoker{TRequest, TResponse}"/> runs it. Without host
/// filters there is no chain at all: telemetry, authorization, then the operation, with no context and no copies; and while
/// nobody listens to telemetry, just authorization and the operation.
/// With host filters, the result passes through <see cref="ResponseDetacher"/> before it reaches the innermost host filter,
/// so host filters get their own copy of anything that may share instances with the stores. Built-in steps inside that
/// point see the shared instances.
/// </remarks>
internal sealed class OperationPipeline
{
    private readonly AuthorizationFilter _authorization;
    private readonly A2AOperationDelegate? _chain;

    public OperationPipeline(AuthorizationFilter authorization, IEnumerable<A2AOperationFilterRegistration> registrations)
    {
        _authorization = authorization;
        var before = new List<IA2AOperationFilter>();
        var after = new List<IA2AOperationFilter>();
        foreach (var registration in registrations)
            (registration.Stage == A2AFilterStage.BeforeAuthorization ? before : after).Add(registration.Filter);
        if (before.Count + after.Count == 0) return;

        // Outermost first. The innermost host filter gets detached results from the steps inside it.
        var steps = new List<(IA2AOperationFilter Filter, bool Host)>(before.Count + after.Count + 2) { (TelemetryFilter.Instance, false) };
        steps.AddRange(before.Select(f => (f, true)));
        steps.Add((authorization, false));
        steps.AddRange(after.Select(f => (f, true)));
        var innermostHost = steps.FindLastIndex(s => s.Host);

        A2AOperationDelegate next = static (context, request, ct) => context.InvokeOperationAsync(request, ct);
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            if (i == innermostHost) next = Detaching(next);
            next = Link(steps[i].Filter, next);
        }
        _chain = next;
    }

    /// <summary>True when host filters are registered (and results are copied for them).</summary>
    public bool HasHostFilters => _chain is not null;

    /// <summary>
    /// Runs <paramref name="operation"/> for <paramref name="request"/> through the pipeline. The tenant must be bound already.
    /// </summary>
    /// <param name="descriptor">The operation's descriptor.</param>
    /// <param name="call">The caller.</param>
    /// <param name="request">The request.</param>
    /// <param name="operation">The operation: a delegate the caller caches, so a call creates no closure for it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask<TResponse> RunAsync<TRequest, TResponse>(A2AOperationDescriptor descriptor, ServerCallContext call, TRequest request,
        Func<TRequest, ServerCallContext, CancellationToken, ValueTask<TResponse>> operation, CancellationToken cancellationToken)
        where TRequest : class
    {
        if (_chain is null)
        {
            if (TelemetryFilter.IsEnabled) return TelemetryFilter.RunAsync(_authorization, descriptor, call, request, operation, cancellationToken);
            var authorized = _authorization.RequireAsync(descriptor, call, request, cancellationToken);
            return authorized.IsCompletedSuccessfully
                ? operation(request, call, cancellationToken)
                : AuthorizeThenRunAsync(authorized, operation, request, call, cancellationToken);
        }

        var context = new A2AOperationContext(descriptor, call, operation, OperationInvoker<TRequest, TResponse>.Instance);
        var result = _chain(context, request, cancellationToken);
        return result.IsCompletedSuccessfully ? new ValueTask<TResponse>(Cast<TResponse>(result.Result)) : CastAsync<TResponse>(result);
    }

    private static async ValueTask<TResponse> AuthorizeThenRunAsync<TRequest, TResponse>(Task authorized,
        Func<TRequest, ServerCallContext, CancellationToken, ValueTask<TResponse>> operation, TRequest request, ServerCallContext call, CancellationToken ct)
    {
        await authorized.ConfigureAwait(false);
        return await operation(request, call, ct).ConfigureAwait(false);
    }

    private static async ValueTask<TResponse> CastAsync<TResponse>(ValueTask<object?> result) => Cast<TResponse>(await result.ConfigureAwait(false));

    private static TResponse Cast<TResponse>(object? result)
    {
        if (result is TResponse typed) return typed;
        if (result is null && typeof(TResponse) == typeof(object)) return default!; // an operation without a result
        throw new InvalidOperationException(
            $"An operation filter returned {result?.GetType().Name ?? "null"} where {typeof(TResponse).Name} was expected.");
    }

    private static A2AOperationDelegate Link(IA2AOperationFilter filter, A2AOperationDelegate next) =>
        (context, request, ct) => filter.InvokeAsync(context, request, next, ct);

    private static A2AOperationDelegate Detaching(A2AOperationDelegate next) => (context, request, ct) =>
    {
        var result = next(context, request, ct);
        return result.IsCompletedSuccessfully ? new ValueTask<object?>(ResponseDetacher.Detach(result.Result)) : DetachAsync(result);

        static async ValueTask<object?> DetachAsync(ValueTask<object?> result) => ResponseDetacher.Detach(await result.ConfigureAwait(false));
    };
}

/// <summary>The innermost step for one operation shape: casts the request and runs the operation delegate the context carries.</summary>
internal static class OperationInvoker<TRequest, TResponse> where TRequest : class
{
    public static readonly Func<A2AOperationContext, object, CancellationToken, ValueTask<object?>> Instance = Invoke;

    private static ValueTask<object?> Invoke(A2AOperationContext context, object request, CancellationToken ct)
    {
        var operation = (Func<TRequest, ServerCallContext, CancellationToken, ValueTask<TResponse>>)context.OperationDelegate!;
        var typed = request as TRequest ?? throw new InvalidOperationException(
            $"An operation filter passed {request.GetType().Name} where {typeof(TRequest).Name} was expected.");
        var result = operation(typed, context.Call, ct);
        return result.IsCompletedSuccessfully ? new ValueTask<object?>(result.Result) : BoxAsync(result);

        static async ValueTask<object?> BoxAsync(ValueTask<TResponse> result) => await result.ConfigureAwait(false);
    }
}

/// <summary>
/// Copies results that may share instances with the stores before host filters see them: tasks, send results, task lists,
/// push configs, Agent Cards, and every event of a stream. Null passes unchanged.
/// </summary>
internal static class ResponseDetacher
{
    public static object? Detach(object? result) => result switch
    {
        AgentTask task => task.DeepCopy(),
        TaskResponse response => new TaskResponse(response.Task.DeepCopy()),
        MessageResponse response => new MessageResponse(response.Message.DeepCopy()),
        ListTasksResponse list => new ListTasksResponse
        {
            Tasks = list.Tasks.ConvertAll(t => t.DeepCopy()),
            NextPageToken = list.NextPageToken,
            PageSize = list.PageSize,
            TotalSize = list.TotalSize,
        },
        TaskPushNotificationConfig config => config.DeepCopy(),
        ListTaskPushNotificationConfigsResponse list => new ListTaskPushNotificationConfigsResponse
        {
            Configs = list.Configs.ConvertAll(c => c.DeepCopy()),
            NextPageToken = list.NextPageToken,
        },
        IAsyncEnumerable<SequencedEvent> events => DetachEvents(events),
        // Cold path (GetExtendedAgentCard, only with host filters): a JSON round trip, as cards have no structural copy.
        // The provider may return a cached card, even the public one, so a filter must never get that instance.
        AgentCard card => JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(card), A2AJson.GetTypeInfo<AgentCard>()),
        _ => result, // null (DeleteTaskPushNotificationConfig)
    };

    /// <summary>The same events, in the same order and with the same sequences, each <see cref="SequencedEvent.Event"/> copied.</summary>
    private static async IAsyncEnumerable<SequencedEvent> DetachEvents(IAsyncEnumerable<SequencedEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return e with { Event = e.Event.DeepCopy() };
    }
}
