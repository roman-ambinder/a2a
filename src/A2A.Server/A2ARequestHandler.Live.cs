using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Server;

/// <summary>A2A 1.1 SendLiveMessage (spec §3.1.12): a live exchange bound to one task.</summary>
public sealed partial class A2ARequestHandler
{
    /// <summary>The open live downlinks of each task (scope key), so an HTTP uplink call can report a refusal on them too.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ChannelWriter<SequencedEvent>>> _liveDownlinks = new(StringComparer.Ordinal);
    private long _nextDownlink;

    /// <summary>
    /// A2A 1.1 SendLiveMessage (§3.1.12): reads the client's requests from <paramref name="uplink"/> and returns the downlink.
    /// The first request binds the exchange to one task: <c>sendMessage</c> works as SendStreamingMessage (authorized as one),
    /// <c>subscribe</c> as SubscribeToTask (with <c>lastSeenGeneration</c>). Each later <c>sendMessage</c> is processed as a
    /// SendMessage that returns at once (authorized as one, with every follow-up rule) and its events reach the downlink with
    /// the task's other events. A later request that cannot be taken (another task, a late <c>subscribe</c>, a kind this SDK
    /// does not know, or any refusal of the SendMessage) becomes a <see cref="StreamErrorEvent"/> on the downlink, and the
    /// exchange stays open. The downlink ends when the task does (or as every stream ends: limits, cancellation). Only for 1.1
    /// callers; the agent must declare streaming.
    /// </summary>
    /// <param name="uplink">The client's requests, in order. On HTTP this is the one request of the downlink call.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation; ends the exchange.</param>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async IAsyncEnumerable<SequencedEvent> SendLiveMessageAsync(IAsyncEnumerable<StreamRequest> uplink, ServerCallContext call,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uplink);
        ArgumentNullException.ThrowIfNull(call);
        RequireLive(call);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requests = uplink.GetAsyncEnumerator(stop.Token);
        try
        {
            // The first request that can open the exchange. Requests of a kind this SDK does not know are refused, one by one.
            IAsyncEnumerable<SequencedEvent>? opened = null;
            string? taskId = null;
            while (opened is null)
            {
                if (!await requests.MoveNextAsync().ConfigureAwait(false)) yield break;
                var first = requests.Current;
                if (first?.Subscribe is { } subscribe)
                {
                    subscribe.Tenant ??= first.Tenant;
                    taskId = subscribe.Id;
                    opened = SubscribeToTaskAsync(subscribe, call, cancellationToken: stop.Token);
                }
                else if (first?.SendMessage is { Message: not null } send)
                {
                    send.Tenant ??= first.Tenant;
                    opened = SendStreamingMessageAsync(send, call, cancellationToken: stop.Token);
                }
                else if (first is not null && MalformedLiveRequest.Of(first) is { } malformed)
                    yield return RefusedEvent(null, malformed.Error, null, malformed.ReferenceId);
                else yield return RefusedEvent(null, first?.SendMessage is null
                    ? new A2AException(A2AErrorCode.InvalidParams, "The request has a kind this server does not know, or none.")
                    : A2AException.InvalidParams("The first request of a live exchange needs a message or a subscribe."), first?.SendMessage);
            }

            var events = opened.GetAsyncEnumerator(stop.Token);
            try
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false)) yield break;
                var head = events.Current;
                yield return head;
                if (head.Event is MessageEvent) yield break; // a direct reply: no task, nothing to stay open for
                taskId ??= head.Event.TaskId;
                if (string.IsNullOrEmpty(taskId)) yield break;

                // From here on the downlink merges the task's events with the refusals of later requests.
                var output = Channel.CreateUnbounded<SequencedEvent>(new UnboundedChannelOptions { SingleReader = true });
                var scope = ScopeKey(call.Tenant, taskId);
                var downlinkId = Interlocked.Increment(ref _nextDownlink);
                var downlinks = _liveDownlinks.GetOrAdd(scope, static _ => new());
                downlinks[downlinkId] = output.Writer;
                var pumpEvents = PumpEventsAsync(events, output.Writer);
                var pumpRequests = PumpRequestsAsync(requests, taskId, call, output.Writer, stop.Token);
                try
                {
                    await foreach (var e in output.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) yield return e;
                    await pumpEvents.ConfigureAwait(false); // its error, if any, ends the exchange as a stream error
                }
                finally
                {
                    downlinks.TryRemove(downlinkId, out _);
                    if (downlinks.IsEmpty) _liveDownlinks.TryRemove(new KeyValuePair<string, ConcurrentDictionary<long, ChannelWriter<SequencedEvent>>>(scope, downlinks));
                    await stop.CancelAsync().ConfigureAwait(false);
                    try { await pumpRequests.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await requests.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Copies the task's events to the downlink, then completes it (with the stream's error, if it failed).</summary>
    private static async Task PumpEventsAsync(IAsyncEnumerator<SequencedEvent> events, ChannelWriter<SequencedEvent> output)
    {
        try
        {
            while (await events.MoveNextAsync().ConfigureAwait(false)) await output.WriteAsync(events.Current).ConfigureAwait(false);
            output.TryComplete();
        }
        catch (Exception ex)
        {
            output.TryComplete(ex);
            throw;
        }
    }

    /// <summary>Processes the later requests of a live exchange; a refusal goes to the downlink as a stream error.</summary>
    private async Task PumpRequestsAsync(IAsyncEnumerator<StreamRequest> requests, string taskId, ServerCallContext call,
        ChannelWriter<SequencedEvent> output, CancellationToken ct)
    {
        while (await requests.MoveNextAsync().ConfigureAwait(false))
        {
            var request = requests.Current;
            try
            {
                await ApplyLaterRequestAsync(request, taskId, call, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                output.TryWrite(RefusedEvent(taskId, ex, request?.SendMessage, request is null ? null : MalformedLiveRequest.Of(request)?.ReferenceId));
            }
        }
    }

    /// <summary>
    /// One later request of a live exchange bound to <paramref name="taskId"/>: a SendMessage for that task that returns at once.
    /// Throws for anything the exchange cannot take.
    /// </summary>
    private async Task<SendMessageResponse> ApplyLaterRequestAsync(StreamRequest? request, string taskId, ServerCallContext call, CancellationToken ct)
    {
        if (request is not null && MalformedLiveRequest.Of(request) is { } malformed) throw malformed.Error;
        if (request is null || request.IsUnknownKind)
            throw new A2AException(A2AErrorCode.InvalidParams, "The request has a kind this server does not know, or none.");
        if (request.Subscribe is not null)
            throw new A2AException(A2AErrorCode.InvalidParams, "subscribe is only valid as the first request of a live exchange.");
        var send = ForTask(request.SendMessage!, request.Tenant, taskId);
        return await SendMessageAsync(send, call, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A copy of <paramref name="send"/> for the exchange's task: a message or update without a task id gets it, one naming
    /// another task is refused (§3.1.12). <c>returnImmediately</c> is set: the exchange is already streaming.
    /// </summary>
    private static SendMessageRequest ForTask(SendMessageRequest send, string? tenant, string taskId)
    {
        var message = send.Message?.DeepCopy();
        if (message is not null)
        {
            if (string.IsNullOrEmpty(message.TaskId)) message.TaskId = taskId;
            else if (!string.Equals(message.TaskId, taskId, StringComparison.Ordinal))
                throw A2AException.InvalidParams("A request of a live exchange must be for the task the exchange is bound to.");
        }
        List<TaskArtifactUpdateEvent>? updates = null;
        if (send.ArtifactUpdates is { } list)
        {
            updates = new List<TaskArtifactUpdateEvent>(list.Count);
            foreach (var u in list)
            {
                if (u is null) { updates.Add(null!); continue; } // refused by the artifact checks
                if (!string.IsNullOrEmpty(u.TaskId) && !string.Equals(u.TaskId, taskId, StringComparison.Ordinal))
                    throw A2AException.InvalidParams("A request of a live exchange must be for the task the exchange is bound to.");
                updates.Add(new TaskArtifactUpdateEvent
                {
                    TaskId = taskId, ContextId = u.ContextId, Artifact = u.Artifact, Append = u.Append, LastChunk = u.LastChunk, Metadata = u.Metadata,
                });
            }
        }
        var configuration = send.Configuration;
        return new SendMessageRequest
        {
            Tenant = send.Tenant ?? tenant,
            Message = message,
            Metadata = send.Metadata,
            ArtifactUpdates = updates,
            Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = configuration?.AcceptedOutputModes,
                TaskPushNotificationConfig = configuration?.TaskPushNotificationConfig,
                HistoryLength = configuration?.HistoryLength,
                TimelineLength = configuration?.TimelineLength,
                IfGenerationMatch = configuration?.IfGenerationMatch,
                ReturnImmediately = true,
            },
        };
    }

    /// <summary>
    /// A2A 1.1 SendLiveMessage over HTTP, a call that is not the downlink (§3.1.12): applies one later request and returns only
    /// an acknowledgement, the task as it is now; its events go to the downlinks. The request must name its task. A refusal is
    /// thrown to the caller and also sent as a <see cref="StreamErrorEvent"/> on this instance's open downlinks of the task.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async Task<AgentTask> SendLiveUplinkAsync(StreamRequest request, ServerCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        RequireLive(call);
        if (request.IsUnknownKind) throw A2AException.InvalidParams("The request has a kind this server does not know, or none.");
        if (request.Subscribe is not null)
            throw A2AException.InvalidParams("subscribe opens a live exchange: send it on the downlink call (Accept: text/event-stream).");
        var send = request.SendMessage!;
        var taskId = !string.IsNullOrEmpty(send.Message?.TaskId) ? send.Message.TaskId : send.ArtifactUpdates?.FirstOrDefault(u => !string.IsNullOrEmpty(u?.TaskId))?.TaskId;
        if (string.IsNullOrEmpty(taskId))
            throw A2AException.InvalidParams("An uplink call must name its task; a new task is opened on the downlink call (Accept: text/event-stream).");
        call.BindTenant(send.Tenant ?? request.Tenant);
        Agents.RequestCards.RequireStreaming(await _requestCards.RequireAgentAsync(call, cancellationToken).ConfigureAwait(false));
        try
        {
            var response = await ApplyLaterRequestAsync(request, taskId, call, cancellationToken).ConfigureAwait(false);
            if (response is TaskResponse { Task: var task }) return task;
            return (await _tasks.GetAsync(Key(call, taskId), cancellationToken).ConfigureAwait(false))?.Task ?? throw A2AException.TaskNotFound(taskId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (_liveDownlinks.TryGetValue(ScopeKey(call.Tenant, taskId), out var downlinks))
            {
                var refused = RefusedEvent(taskId, ex, send);
                foreach (var writer in downlinks.Values) writer.TryWrite(refused);
            }
            throw;
        }
    }

    /// <summary>The live exchange is A2A 1.1, and needs an agent that declares streaming (§3.3.4).</summary>
    private static void RequireLive(ServerCallContext call)
    {
        if (!A2AProtocol.IsVersion11OrLater(call.ProtocolVersion))
            throw new A2AException(A2AErrorCode.UnsupportedOperation, "SendLiveMessage is A2A 1.1: send A2A-Version: 1.1.");
    }

    /// <summary>
    /// A <see cref="StreamErrorEvent"/> for a refused request: the code of the unary operation (§4.2.4), its message, and the id
    /// of the refused message (or of its first artifact update).
    /// </summary>
    private SequencedEvent RefusedEvent(string? taskId, Exception ex, SendMessageRequest? send, string? referenceId = null)
    {
        var a2a = ex as A2AException ?? new A2AException(A2AErrorCode.Internal, _options.IncludeExceptionDetails == true ? ex.Message : null);
        return new SequencedEvent(0, StreamResponse.Of(new StreamError
        {
            TaskId = taskId,
            Code = A2AErrors.GrpcStatus(a2a.Code),
            Message = a2a.Message,
            ReferenceId = referenceId ?? send?.Message?.MessageId ?? send?.ArtifactUpdates?.FirstOrDefault()?.Artifact?.ArtifactId,
        }));
    }
}

/// <summary>
/// A live request a binding could not map to the model (for example a gRPC message without parts, which the model requires):
/// it is passed on as an empty <see cref="StreamRequest"/> marked here, and refused with this error and reference id, so the
/// client sees the same refusal on every binding.
/// </summary>
internal sealed class MalformedLiveRequest(A2AException error, string? referenceId)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StreamRequest, MalformedLiveRequest> Marks = new();

    public A2AException Error { get; } = error;
    public string? ReferenceId { get; } = referenceId;

    /// <summary>An empty request that stands for one that could not be mapped.</summary>
    public static StreamRequest Create(A2AException error, string? referenceId)
    {
        var request = new StreamRequest();
        Marks.Add(request, new MalformedLiveRequest(error, referenceId));
        return request;
    }

    public static MalformedLiveRequest? Of(StreamRequest request) => Marks.TryGetValue(request, out var mark) ? mark : null;
}
