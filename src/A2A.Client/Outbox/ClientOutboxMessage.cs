using A2A.Json;

namespace A2A.Client.Outbox;

/// <summary>
/// A message to send to a peer through the outbox. The request is stored as JSON bytes. Credentials are never stored: the
/// <see cref="Peer"/> is a logical name, and the app's client resolver (see <see cref="ClientOutboxDispatcher"/>) turns it into a client
/// that has its own credentials.
/// </summary>
public sealed class ClientOutboxMessage
{
    /// <summary>Creates a message from request bytes that are already serialized. Prefer <see cref="Create"/>.</summary>
    /// <param name="id">The entry id. It is also the <c>Idempotency-Key</c> of every send.</param>
    /// <param name="peer">The logical name of the peer.</param>
    /// <param name="request">A <see cref="SendMessageRequest"/> as JSON (<see cref="A2AJson"/>).</param>
    /// <param name="correlation">Your own label, to find your results again (see <see cref="ClientOutboxStore.LeaseResultsAsync"/>).</param>
    public ClientOutboxMessage(string id, string peer, ReadOnlyMemory<byte> request, string? correlation = null)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("The id is required.", nameof(id));
        if (string.IsNullOrEmpty(peer)) throw new ArgumentException("The peer is required.", nameof(peer));
        if (request.IsEmpty) throw new ArgumentException("The request is required.", nameof(request));
        Id = id;
        Peer = peer;
        Request = request;
        Correlation = correlation;
    }

    /// <summary>The entry id. Unique in the store. It is the <c>Idempotency-Key</c> of every send.</summary>
    public string Id { get; }

    /// <summary>The logical name of the peer. Not a URL and not a credential.</summary>
    public string Peer { get; }

    /// <summary>Your own label, or null.</summary>
    public string? Correlation { get; }

    /// <summary>The <see cref="SendMessageRequest"/> as JSON.</summary>
    public ReadOnlyMemory<byte> Request { get; }

    /// <summary>
    /// Creates a message from <paramref name="request"/>. The request is copied, so your object is not changed. A missing
    /// message id is stamped, and <c>ReturnImmediately</c> is set, so a send returns as soon as the peer has a task instead of waiting for the whole task.
    /// </summary>
    /// <param name="peer">The logical name of the peer.</param>
    /// <param name="request">What to send.</param>
    /// <param name="correlation">Your own label, or null.</param>
    /// <param name="id">The entry id. Default: the message id.</param>
    public static ClientOutboxMessage Create(string peer, SendMessageRequest request, string? correlation = null, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is null) throw new ArgumentException("The request has no message.", nameof(request));

        var message = request.Message.DeepCopy();
        if (string.IsNullOrEmpty(message.MessageId)) message.MessageId = Message.NewId();
        var source = request.Configuration;
        var copy = new SendMessageRequest
        {
            Tenant = request.Tenant,
            Message = message,
            Metadata = request.Metadata is null ? null : new Dictionary<string, System.Text.Json.JsonElement>(request.Metadata),
            Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = source?.AcceptedOutputModes is null ? null : new List<string>(source.AcceptedOutputModes),
                TaskPushNotificationConfig = source?.TaskPushNotificationConfig,
                HistoryLength = source?.HistoryLength,
                ReturnImmediately = true,
            },
        };
        return new ClientOutboxMessage(id ?? message.MessageId, peer, A2AJson.SerializeToUtf8Bytes(copy), correlation);
    }
}
