using System.Globalization;
using System.Runtime.CompilerServices;
using A2A.Client.Resilience;

namespace A2A.Client.Streaming;

/// <summary>One stream event together with the SSE <c>id:</c> of its own frame (null when the frame had none).</summary>
/// <param name="Response">The stream event.</param>
/// <param name="Id">
///     The server event id of the frame, or null when the frame carried none. Only an id of the form
///     <c>{taskId}:{sequence}</c> counts (see <see cref="IResumableTransport" />). A transport whose frames never carry a
///     usable id gets a full replay on every reconnect.
/// </param>
public readonly record struct StreamFrame(StreamResponse Response, string? Id);

/// <summary>
///     Optional contract for a transport whose stream frames carry server event ids and which can send the last one back
///     to
///     resume. Implement it, next to <see cref="IA2ATransport" />, on a transport returned by an
///     <see cref="IA2ATransportFactory" />:
///     <see cref="A2AClient" /> then reconnects dropped streams from the last cursor (<c>Last-Event-ID</c> on HTTP). A
///     transport
///     that does not implement it still works, but each reconnect subscribes again from the start.
/// </summary>
/// <remarks>
///     A frame id is a cursor only when it has the form <c>{taskId}:{sequence}</c>, with a sequence of 1 or more and a
///     task id
///     equal to the id of the followed task (this SDK's servers send such ids). Any other id is treated as no id: it is
///     never a
///     cursor and does not count as progress. The client drops a frame whose sequence is not above the last one it
///     yielded.
///     <c>lastEventId</c> is the id of the last frame that had a valid cursor; the server must continue strictly after it.
///     With opaque or missing ids there is no resume and no dedupe: the client reconnects with <c>lastEventId</c> null and
///     events may be yielded again, starting with a task snapshot. Consumers of such peers must tolerate repeats.
/// </remarks>
public interface IResumableTransport
{
    /// <summary>
    ///     Sends a message and streams the events as frames. Same as <see cref="IA2ATransport.SendStreamingMessageAsync" />,
    ///     with each frame's event id.
    /// </summary>
    /// <param name="request">The message request.</param>
    /// <param name="options">Per-call options; use the same options on every reconnect.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    IAsyncEnumerable<StreamFrame> SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Subscribes to a task and streams the events as frames, continuing after <paramref name="lastEventId" /> when
    ///     it is set.
    /// </summary>
    /// <param name="request">The subscribe request.</param>
    /// <param name="options">Per-call options; the client passes the same options on every reconnect.</param>
    /// <param name="lastEventId">
    ///     The id of the last frame the client received, or null on the first subscribe. Send it to the
    ///     server so it resumes after that event.
    /// </param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    IAsyncEnumerable<StreamFrame> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options,
        string? lastEventId, CancellationToken cancellationToken);
}

internal static class StreamFrameExtensions
{
    public static async IAsyncEnumerable<StreamResponse> Unframe(this IAsyncEnumerable<StreamFrame> frames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return frame.Response;
    }
}

/// <summary>
///     Client side of resumable streams (design §7.4). Follows a stream, remembers the last cursor, and when the
///     connection
///     fails reconnects with <c>Last-Event-ID</c> after a backoff. A cursor is only taken from frames that carry an id,
///     and
///     no sequence at or below the last one yielded is yielded again.
/// </summary>
/// <remarks>
///     A peer that sends no SSE event ids (any plain A2A v1.0 server; this SDK's servers send them) gives no cursor: a
///     resume
///     then subscribes again without <c>Last-Event-ID</c>, and events already yielded may be yielded again (starting with
///     a
///     task snapshot). Consumers of such peers must tolerate repeats.
/// </remarks>
internal static class ResumableStream
{
    /// <summary>
    ///     Waits before reconnect 1..5 (a longer server Retry-After wins). After the fifth failed reconnect without progress
    ///     the
    ///     error is rethrown.
    /// </summary>
    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    /// <summary>
    ///     Runs the stream. <paramref name="first" /> opens the first connection. <paramref name="resume" /> opens a later one
    ///     for
    ///     the task id and cursor (the cursor is null when no frame carried an id yet). <paramref name="getTask" /> is the
    ///     fallback
    ///     when the server refuses to subscribe to a task that has already finished. <paramref name="knownTaskId" /> is null
    ///     for a
    ///     send: reconnecting is only possible once an event has revealed the task id.
    /// </summary>
    public static async IAsyncEnumerable<StreamResponse> RunAsync(
        string? knownTaskId,
        Func<CancellationToken, IAsyncEnumerable<StreamFrame>> first,
        Func<string, string?, CancellationToken, IAsyncEnumerable<StreamFrame>> resume,
        Func<string, CancellationToken, Task<AgentTask>> getTask,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var taskId = knownTaskId;
        string? cursor = null;
        long lastSequence = 0;
        var failures = 0;
        var reconnecting = false;
        StreamResponse? lastYielded = null;

        while (true)
        {
            Exception? failure = null;
            AgentTask? finished = null;
            var completed = false;
            var progressed = false;
            var enumerator = (reconnecting ? resume(taskId!, cursor, cancellationToken) : first(cancellationToken))
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    StreamFrame frame;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            // Clean end. The server also closes streams at MaxStreamDuration or on re-auth: resume unless the
                            // stream is over (a final event was yielded) or there is no task id to resume with.
                            if (taskId is null || (lastYielded is not null && EndsStream(lastYielded, cursor)))
                                yield break;
                            failure = new IOException("The server closed the stream before the task finished.");
                            break;
                        }

                        frame = enumerator.Current;
                    }
                    catch (A2AException ex) when (ex.Code == A2AErrorCode.UnsupportedOperation && taskId is not null &&
                                                  !cancellationToken.IsCancellationRequested)
                    {
                        var task = await getTask(taskId, cancellationToken).ConfigureAwait(false);
                        if (!task.Status.State.IsTerminal()) throw;
                        finished = task;
                        break;
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex) && !cancellationToken.IsCancellationRequested &&
                                               taskId is not null)
                    {
                        // A reset while the server closes, after the final event, is not a failure.
                        if (lastYielded is not null && EndsStream(lastYielded, cursor)) completed = true;
                        else failure = ex;
                        break;
                    }

                    taskId ??= TaskIdOf(frame.Response);
                    if (taskId is not null && IsCursorOf(frame.Id, taskId, out var sequence))
                    {
                        // A task snapshot below the cursor means the server lost its event log and started over: take the
                        // snapshot and continue from its sequence. Any other frame at or below the cursor was delivered before the drop.
                        var serverReset = frame.Response is TaskEvent && sequence < lastSequence;
                        if (sequence <= lastSequence && !serverReset) continue;
                        lastSequence = sequence;
                        cursor = frame.Id;
                        progressed =
                            true; // only a new sequence is progress: frames without ids cannot be told from replays
                    }

                    lastYielded = frame.Response;
                    yield return frame.Response;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (completed) yield break;

            if (finished is not null)
            {
                yield return new TaskEvent(finished);
                yield break;
            }

            // The connection ended early. Progress (a new sequence) since the last reconnect means a fresh budget of attempts.
            if (progressed) failures = 0;
            if (failures >= Backoff.Length) throw failure!;
            ClientTelemetry.RecordRetry(failures + 1);
            // A throttled or unavailable server may ask for a longer wait than the backoff: wait for the longer one.
            var delay = Backoff[failures];
            if (failure is A2AException { RetryAfter: { } asked } && asked > delay) delay = asked;
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            failures++;
            reconnecting = true;
        }
    }

    /// <summary>
    ///     True when no more events follow: a direct message or a terminal status. An interrupted state (input or auth
    ///     required)
    ///     ends the stream only for a peer that sends no event ids (<paramref name="cursor" /> is null): it closes there. This
    ///     SDK's
    ///     server keeps the stream open, so a close at such a state is a MaxStreamDuration or re-auth close and must be
    ///     resumed.
    /// </summary>
    private static bool EndsStream(StreamResponse response, string? cursor)
    {
        var state = response switch
        {
            MessageEvent => (TaskState?)TaskState.Completed,
            TaskEvent t => t.Task.Status.State,
            StatusUpdateEvent s => s.StatusUpdate.Status.State,
            _ => null
        };
        if (state is not { } st) return false;
        return st.IsTerminal() || (cursor is null && st.IsInterrupted());
    }

    /// <summary>
    ///     A dropped connection, or the server saying it is busy or unavailable (HTTP 429, 502, 503, 504). A Retry-After
    ///     beyond
    ///     <see cref="Resilience.RetryPolicy.MaxRetryAfter" /> is not waited for: it stays an error, as for unary calls.
    /// </summary>
    private static bool IsConnectionFailure(Exception ex)
    {
        return ex switch
        {
            HttpRequestException or IOException => true,
            A2AException { HttpStatus: 429 or 502 or 503 or 504 } a2a => a2a.RetryAfter is not { } asked ||
                                                                         asked <= RetryPolicy.MaxRetryAfter,
            _ => false
        };
    }

    /// <summary>
    ///     True when <paramref name="id" /> is a cursor (<c>{taskId}:{sequence}</c>) of task <paramref name="taskId" />.
    ///     Compares in
    ///     place: no task id string per frame.
    /// </summary>
    internal static bool IsCursorOf(string? id, string taskId, out long sequence)
    {
        return TrySplitCursor(id, out var colon, out sequence) && id.AsSpan(0, colon).SequenceEqual(taskId.AsSpan());
    }

    /// <summary>Finds the colon of <c>{taskId}:{sequence}</c>. False for a missing or malformed id, or a sequence below 1.</summary>
    private static bool TrySplitCursor(string? id, out int colon, out long sequence)
    {
        colon = -1;
        sequence = 0;
        if (string.IsNullOrEmpty(id)) return false;
        colon = id!.LastIndexOf(':');
        if (colon <= 0) return false;
        return long.TryParse(id.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) &&
               sequence >= 1;
    }

    /// <summary>The task id an event carries, or null when it carries none.</summary>
    internal static string? TaskIdOf(StreamResponse response)
    {
        var id = response switch
        {
            TaskEvent t => t.Task.Id,
            StatusUpdateEvent s => s.StatusUpdate.TaskId,
            ArtifactUpdateEvent a => a.ArtifactUpdate.TaskId,
            MessageEvent m => m.Message.TaskId,
            MessageUpdateEvent u => u.MessageUpdate.TaskId,
            _ => null
        };
        return string.IsNullOrEmpty(id) ? null : id;
    }
}