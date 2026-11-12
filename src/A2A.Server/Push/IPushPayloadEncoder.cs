using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
///     Writes the webhook body for push configs of a protocol version whose body is not the stored event (v0.3 compat: the
///     task as the v0.3 JSON-RPC <c>Task</c>). Set on <see cref="A2AServerOptions.PushPayloadEncoder" /> by the compat
///     package before the host starts.
/// </summary>
internal interface IPushPayloadEncoder
{
    /// <summary>The push config <c>protocolVersion</c> (Major.Minor) this encoder writes bodies for.</summary>
    string ProtocolVersion { get; }

    /// <summary>The <c>Content-Type</c> of the body.</summary>
    string ContentType { get; }

    /// <summary>
    ///     The body for <paramref name="delivery" />, or null when there is nothing to send any more (the task is gone): the
    ///     delivery then fails for good.
    /// </summary>
    /// <param name="delivery">The delivery: its task and the sequence of the event.</param>
    /// <param name="eventBody">The stored event (a v1.0 <c>StreamResponse</c>).</param>
    /// <param name="cancellationToken">Cancels the encoding.</param>
    ValueTask<ReadOnlyMemory<byte>?> EncodeAsync(PushDelivery delivery, ReadOnlyMemory<byte> eventBody,
        CancellationToken cancellationToken);
}
