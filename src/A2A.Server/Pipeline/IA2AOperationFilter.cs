using A2A.Server.Streaming;

namespace A2A.Server.Pipeline;

/// <summary>The rest of the pipeline after a filter: the next filter, or the operation itself.</summary>
/// <param name="context">The call.</param>
/// <param name="request">The request object; see <see cref="IA2AOperationFilter.InvokeAsync"/>.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>The operation result; see <see cref="IA2AOperationFilter.InvokeAsync"/>.</returns>
public delegate ValueTask<object?> A2AOperationDelegate(A2AOperationContext context, object request, CancellationToken cancellationToken);

/// <summary>
/// Observes or wraps every A2A operation, on every binding. Register filters with
/// <see cref="A2AServerBuilder.AddOperationFilter{T}"/>; they run in registration order within their <see cref="A2AFilterStage"/>.
/// The built-in authorization filter always runs between the <see cref="A2AFilterStage.BeforeAuthorization"/> filters and the
/// <see cref="A2AFilterStage.AfterAuthorization"/> ones, and the operation is the innermost step. The built-in telemetry step
/// (spans and counters named in <see cref="A2A.Diagnostics.A2ATelemetry"/>) wraps them all, so a call refused by a filter still has its span. The protocol version, the
/// extensions and the tenant are checked before the pipeline, so every filter sees a tenant-bound call.
/// </summary>
/// <remarks>
/// <para>
/// The request is the operation's request object (for example a <see cref="GetTaskRequest"/>, or a
/// <see cref="TaskPushNotificationConfig"/> for CreateTaskPushNotificationConfig). Pass it on unchanged, or a replacement of the
/// same type. The result is the operation's response object (for example an <see cref="AgentTask"/>, a
/// <see cref="SendMessageResponse"/>, or null for DeleteTaskPushNotificationConfig); a replacement must have the same type.
/// Throw an <see cref="A2AException"/> to refuse a call: the binding sends it to the client as an error.
/// </para>
/// <para>
/// Streaming operations (SendStreamingMessage and SubscribeToTask) return an <see cref="IAsyncEnumerable{T}"/> of
/// <see cref="SequencedEvent"/>. The pipeline runs when the stream is first read, before any event is sent, so an exception
/// still becomes an error response. A filter that wants to see the events returns a wrapping enumerable; it must keep the
/// order and the <see cref="SequencedEvent.Sequence"/> of every event it passes on. A filter that builds a new
/// <see cref="SequencedEvent"/> uses the two-argument constructor <see cref="SequencedEvent(long, StreamResponse)"/> or
/// <see cref="EventEncoder.Encode"/>: bindings write <see cref="SequencedEvent.Json"/> as it is, without checking that it
/// matches <see cref="SequencedEvent.Event"/>.
/// </para>
/// <para>
/// On an open stream, the periodic re-authorization (every <see cref="A2ALimits.StreamReauthInterval"/>) calls only the
/// <see cref="Authorization.A2AAuthorizationHandler"/> (its operation check, and for a <c>SendStreamingMessage</c> stream that was
/// authorized for a skill, its skill check); host filters are not run again.
/// </para>
/// <para>
/// Results and events reach host filters as their own copies: a filter may change them, and they belong to the caller once
/// the filter returns. Do not keep a result or a streamed event beyond the call.
/// </para>
/// <para>
/// Authorization runs once, on the request and call it receives. A <see cref="A2AFilterStage.BeforeAuthorization"/> filter
/// that returns without calling <c>next</c> skips authorization: its result goes to the client unauthorized.
/// A replacement request, or a change to <see cref="ServerCallContext.Tenant"/>, made after authorization (by an
/// <see cref="A2AFilterStage.AfterAuthorization"/> filter) is not authorized again: the operation runs with it as it is.
/// </para>
/// <para>
/// An artifact download (<see cref="A2ARequestHandler.OpenArtifactAsync"/>) runs as a GetTask operation, and its result is an
/// <see cref="ArtifactDownload"/>, not an <see cref="AgentTask"/>. A filter that casts a GetTask result must check the type first.
/// The result is not copied: it holds an open stream, so a filter that replaces it must dispose it.
/// </para>
/// <para>One filter instance serves every call, concurrently: keep per-call state in <see cref="A2AOperationContext.Items"/>.</para>
/// </remarks>
public interface IA2AOperationFilter
{
    /// <summary>Runs the filter. Call <paramref name="next"/> to continue, or return or throw without calling it to stop the call.</summary>
    /// <param name="context">The operation and the caller.</param>
    /// <param name="request">The request object.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result to send back (see the remarks on <see cref="IA2AOperationFilter"/>).</returns>
    ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken);
}
