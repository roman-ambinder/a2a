using System.Text.Json;
using A2A.Json;

namespace A2A.Client.Outbox;

/// <summary>One row of the client outbox, as a store returned it. Immutable: a store keeps its own copy.</summary>
public sealed class ClientOutboxEntry
{
    /// <summary>Creates an entry. Stores do this; an app reads entries.</summary>
    public ClientOutboxEntry(string id, string peer, ReadOnlyMemory<byte> request, ClientOutboxStatus status)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        Request = request;
        Status = status;
    }

    /// <summary>The entry id. It is the <c>Idempotency-Key</c> of every send.</summary>
    public string Id { get; }

    /// <summary>The logical name of the peer.</summary>
    public string Peer { get; }

    /// <summary>The label the app gave, or null.</summary>
    public string? Correlation { get; init; }

    /// <summary>The <see cref="SendMessageRequest"/> as JSON.</summary>
    public ReadOnlyMemory<byte> Request { get; }

    /// <summary>Where the entry is in its life.</summary>
    public ClientOutboxStatus Status { get; }

    /// <summary>Attempts of the current phase: sends while queued, polls while tracking. A lease counts as an attempt, so a leased entry shows the attempt it is in.</summary>
    public int Attempts { get; init; }

    /// <summary>
    /// True when an earlier send may have reached the peer without the SDK learning it. A queued entry becomes in doubt when it is
    /// leased, so a crash in the middle of a send leaves it set. The value returned by a lease is the one from before that lease.
    /// </summary>
    public bool InDoubt { get; init; }

    /// <summary>The task id the peer gave, or null.</summary>
    public string? RemoteTaskId { get; init; }

    /// <summary>The context id the peer gave, or null.</summary>
    public string? RemoteContextId { get; init; }

    /// <summary>The last known state of the peer's task, or null.</summary>
    public TaskState? RemoteState { get; init; }

    /// <summary>Why the last attempt failed, or why the entry is <see cref="ClientOutboxStatus.Failed"/>. Never holds secrets.</summary>
    public string? LastError { get; init; }

    /// <summary>When the entry was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the next attempt may start.</summary>
    public DateTimeOffset DueAt { get; init; }

    /// <summary>When the result was acknowledged, or null.</summary>
    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>How long ago the entry was enqueued, by the store's own clock (the database clock for PostgreSQL), as of the call that returned it.</summary>
    public TimeSpan Age { get; init; }

    /// <summary>The lease this entry was returned under, or null. Writes after a lease apply only while the entry still holds it.</summary>
    public Guid? LeaseToken { get; init; }

    /// <summary>The final answer as a <see cref="SendMessageResponse"/> in JSON, or null while there is none (and for most failures).</summary>
    public ReadOnlyMemory<byte>? ResultPayload { get; init; }

    /// <summary>Reads <see cref="Request"/>.</summary>
    public SendMessageRequest GetRequest() => Read<SendMessageRequest>(Request.Span);

    /// <summary>Reads <see cref="ResultPayload"/>: the task or message the peer answered with, or null when there is none.</summary>
    public SendMessageResponse? GetResult() => ResultPayload is { } p && !p.IsEmpty ? Read<SendMessageResponse>(p.Span) : null;

    internal static T Read<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, A2AJson.GetTypeInfo<T>()) ?? throw new JsonException($"Null {typeof(T).Name} payload.");
}
