using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// Operator API for push deliveries that failed for good: lists the dead letters of a tenant and sends one again.
/// Registered by <c>AddA2AServer</c>. Not exposed over A2A; wire it to your own admin endpoint and protect that.
/// </summary>
public sealed class A2APushAdmin(PushOutbox outbox)
{
    private readonly PushOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

    /// <summary>The dead letters of <paramref name="tenant"/>, oldest first, at most <paramref name="max"/>.</summary>
    public ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default) =>
        _outbox.ListDeadLettersAsync(tenant, max, ct);

    /// <summary>
    /// Queues a dead letter for delivery again (attempt 0, same delivery id, due now). Returns false when it is not a dead
    /// letter. The event is read from the event store when it is sent, so it must still be retained.
    /// </summary>
    public async ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default)
    {
        var moved = await _outbox.RedriveAsync(d, ct).ConfigureAwait(false);
        if (moved) _outbox.NotifyPending();
        return moved;
    }
}
