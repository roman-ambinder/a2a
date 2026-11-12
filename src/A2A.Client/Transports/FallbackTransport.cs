using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Client.Streaming;

namespace A2A.Client.Transports;

/// <summary>
///     Interface fallback (spec §5.2, <see cref="A2AClientOptions.InterfaceFallback" />): holds the card's acceptable
///     interfaces in order, sends each call to the current one, and moves to the next only when the call failed before any
///     request reached the agent (<see cref="NotSent" />). The transports are created on first use. A
///     call that works makes its interface the current one for later calls.
/// </summary>
/// <remarks>
///     Each call starts at the current interface and tries every other one at most once, in card order, wrapping around
///     (so a client that moved away from the first interface returns to it when the one it uses goes down). When all
///     fail, the error of the first interface tried is thrown: it is the one the caller would have seen without fallback.
///     A stream falls back only while it has delivered no event.
/// </remarks>
internal sealed class FallbackTransport : IA2AClient, IResumableTransport, IA2AExtensionOperationClient
#pragma warning disable A2AEXP005
    , ILiveTransport
#pragma warning restore A2AEXP005
{
    private static readonly Meter Meter = new("A2A");

    private static readonly Counter<long> FallbackCounter = Meter.CreateCounter<long>(
        "a2a.client.interface.fallback",
        description: "Calls moved to the next interface of the Agent Card because the previous one could not be reached.");

    private readonly Candidate[] _candidates;
    private int _current;

    /// <param name="candidates">
    ///     The interfaces in card order, each with the function that creates its transport. At least two.
    /// </param>
    /// <param name="primary">The transport of the first candidate, already created (so creation errors surface at once).</param>
    public FallbackTransport(IReadOnlyList<(AgentInterface Interface, Func<IA2AClient> Create)> candidates,
        IA2AClient primary)
    {
        _candidates = new Candidate[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            _candidates[i] = new Candidate(candidates[i].Interface, candidates[i].Create, i == 0 ? primary : null);
    }

    /// <summary>The interface calls go to now (tests).</summary>
    internal AgentInterface CurrentInterface => _candidates[Volatile.Read(ref _current)].Interface;

    /// <summary>The binding of the current transport, or null when it names none (for <see cref="A2AClient" />'s spans). It changes on a fallback.</summary>
    internal string? CurrentBinding => (_candidates[Volatile.Read(ref _current)].Created as ITransportBinding)?.Binding;

    // ---- IA2AClient ----

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.SendMessageAsync(request, options, cancellationToken), cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync((t, ct) => t.SendStreamingMessageAsync(request, options, ct), cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.GetTaskAsync(request, options, cancellationToken), cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.ListTasksAsync(request, options, cancellationToken), cancellationToken);

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.CancelTaskAsync(request, options, cancellationToken), cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync((t, ct) => t.SubscribeToTaskAsync(request, options, ct), cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(t => t.CreateTaskPushNotificationConfigAsync(config, options, cancellationToken), cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.GetTaskPushNotificationConfigAsync(request, options, cancellationToken), cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(t => t.ListTaskPushNotificationConfigsAsync(request, options, cancellationToken), cancellationToken);

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(async t =>
        {
            await t.DeleteTaskPushNotificationConfigAsync(request, options, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(t => t.GetExtendedAgentCardAsync(request, options, cancellationToken), cancellationToken);

    // ---- IResumableTransport: frames of a transport without event ids carry none, as A2AClient reads them ----

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request,
        A2ARequestOptions? options, CancellationToken cancellationToken) =>
        StreamAsync((t, ct) => t is IResumableTransport rt
            ? rt.SendStreamingFramesAsync(request, options, ct)
            : Frames(t.SendStreamingMessageAsync(request, options, ct), ct), cancellationToken);

    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken) =>
        StreamAsync((t, ct) => t is IResumableTransport rt
            ? rt.SubscribeToTaskAsync(request, options, lastEventId, ct)
            : Frames(t.SubscribeToTaskAsync(request, options, ct), ct), cancellationToken);

    // ---- Custom operations: unary, so they fall back like the other unary calls ----

    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(t => t is IA2AExtensionOperationClient ext
            ? ext.InvokeExtensionOperationAsync(method, parameters, options, cancellationToken)
            : throw new A2AException(A2AErrorCode.UnsupportedOperation,
                "The transport of this client does not support custom operations."), cancellationToken);

    // ---- Live exchange: goes to the current interface. Opening may not send anything yet, so it does not fall back. ----

#pragma warning disable A2AEXP005
    public Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options,
        CancellationToken cancellationToken) =>
        _candidates[Volatile.Read(ref _current)].Get() is ILiveTransport live
            ? live.OpenLiveAsync(first, options, cancellationToken)
            : throw new NotSupportedException(
                "The transport of this client has no live exchange (A2A 1.1 SendLiveMessage).");
#pragma warning restore A2AEXP005

    // ---- Fallback ----

    private async Task<T> RunAsync<T>(Func<IA2AClient, Task<T>> call, CancellationToken ct)
    {
        var start = Volatile.Read(ref _current);
        ExceptionDispatchInfo? firstError = null;
        var failed = -1;
        for (var i = 0; i < _candidates.Length; i++)
        {
            var index = (start + i) % _candidates.Length;
            if (!TryGet(index, out var transport)) continue;
            if (failed >= 0) Count(failed, index);
            try
            {
                var result = await call(transport).ConfigureAwait(false);
                if (index != start) Interlocked.CompareExchange(ref _current, index, start);
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && NotSent(ex))
            {
                firstError ??= ExceptionDispatchInfo.Capture(ex);
                failed = index;
            }
        }

        firstError!.Throw();
        throw new InvalidOperationException(); // unreachable
    }

    private async IAsyncEnumerable<T> StreamAsync<T>(Func<IA2AClient, CancellationToken, IAsyncEnumerable<T>> open,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var start = Volatile.Read(ref _current);
        ExceptionDispatchInfo? firstError = null;
        var failed = -1;
        IAsyncEnumerator<T>? stream = null;
        var any = false;
        for (var i = 0; i < _candidates.Length && stream is null; i++)
        {
            var index = (start + i) % _candidates.Length;
            if (!TryGet(index, out var transport)) continue;
            if (failed >= 0) Count(failed, index);
            var enumerator = open(transport, ct).GetAsyncEnumerator(ct);
            try
            {
                // Only the wait for the first event may fall back: after it, the agent has the request.
                any = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && NotSent(ex))
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                firstError ??= ExceptionDispatchInfo.Capture(ex);
                failed = index;
                continue;
            }
            catch
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (index != start) Interlocked.CompareExchange(ref _current, index, start);
            stream = enumerator;
        }

        if (stream is null)
        {
            firstError!.Throw();
            yield break; // unreachable
        }

        try
        {
            if (!any) yield break;
            yield return stream.Current;
            while (await stream.MoveNextAsync().ConfigureAwait(false)) yield return stream.Current;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     True when the call provably sent nothing: the agent could not be reached
    ///     (<see cref="RetryPolicy.ProvablyUnsent" />), or <see cref="Credentials.A2ACredentialHandler" /> refused to send
    ///     credentials to this interface's origin (a client built over a credentialed <see cref="HttpClient" /> with
    ///     <see cref="A2AClientFactory.Create(AgentCard, HttpClient, A2AClientOptions?)" />). Such an interface is skipped; its
    ///     error is thrown only when it is the first one tried.
    /// </summary>
    private static bool NotSent(Exception error) =>
        RetryPolicy.ProvablyUnsent(error) ||
        error.Data[Credentials.A2ACredentialHandler.NotSentKey] is true;

    /// <summary>
    ///     The transport of candidate <paramref name="index" />. False when it cannot be created: that candidate is skipped
    ///     (the creation is tried again on a later call).
    /// </summary>
    private bool TryGet(int index, out IA2AClient transport)
    {
        try
        {
            transport = _candidates[index].Get();
            return true;
        }
        catch (Exception)
        {
            transport = null!;
            return false;
        }
    }

    private void Count(int from, int to) =>
        FallbackCounter.Add(1,
            new KeyValuePair<string, object?>("from", _candidates[from].Interface.ProtocolBinding),
            new KeyValuePair<string, object?>("to", _candidates[to].Interface.ProtocolBinding));

    private static async IAsyncEnumerable<StreamFrame> Frames(IAsyncEnumerable<StreamResponse> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            yield return new StreamFrame(item, null);
    }

    private sealed class Candidate(AgentInterface itf, Func<IA2AClient> create, IA2AClient? created)
    {
        private readonly object _lock = new();
        private IA2AClient? _transport = created;

        public AgentInterface Interface { get; } = itf;

        public IA2AClient? Created => Volatile.Read(ref _transport);

        public IA2AClient Get()
        {
            if (Volatile.Read(ref _transport) is { } t) return t;
            lock (_lock)
            {
                if (_transport is null) Volatile.Write(ref _transport, create());
                return _transport!;
            }
        }
    }
}
