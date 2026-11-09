using A2A.Server.Audit;
using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// Operator API for push deliveries that failed for good: lists the dead letters of a tenant and sends one again.
/// Registered by <c>AddA2AServer</c>. Not exposed over A2A; wire it to your own admin endpoint and protect that. Both calls
/// are written to the audit trail; the overloads with an <c>actor</c> say who made them.
/// </summary>
public sealed class A2APushAdmin
{
    private readonly PushOutbox _outbox;
    private readonly A2AAuditor _audit;

    /// <summary>Creates the admin API. In an app the DI container does this.</summary>
    /// <param name="outbox">The push outbox.</param>
    /// <param name="audit">Writes the audit trail. Without it nothing is audited.</param>
    public A2APushAdmin(PushOutbox outbox, A2AAuditor? audit = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _audit = audit ?? A2AAuditor.None;
    }

    /// <summary>
    /// The dead letters of <paramref name="tenant"/>, oldest first, at most <paramref name="max"/>. Audited with the actor
    /// <see cref="A2AAuditEvent.UnknownOperator"/>.
    /// </summary>
    public ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken cancellationToken = default) =>
        ListDeadLettersAsync(tenant, max, A2AAuditEvent.UnknownOperator, cancellationToken);

    /// <summary>
    /// The dead letters of <paramref name="tenant"/>, oldest first, at most <paramref name="max"/>, and an audit event
    /// (<see cref="A2AAuditAction.AdminDeadLettersListed"/>) for <paramref name="actor"/>. With <see cref="A2AAuditOptions.FailClosed"/>
    /// nothing is listed when the <see cref="A2AAuditOutcome.Attempted"/> row cannot be written.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="max">Most entries to return.</param>
    /// <param name="actor">Who lists: the operator's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var e = _audit.For(A2AAuditAction.AdminDeadLettersListed, actor, tenant);
        return await _audit.AuditedAsync(e, () => _outbox.ListDeadLettersAsync(tenant, max, cancellationToken), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a dead letter for delivery again (attempt 0, same delivery id, same payload, due now). Returns false when it is
    /// not a dead letter. Audited with the actor <see cref="A2AAuditEvent.UnknownOperator"/>.
    /// </summary>
    public ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken cancellationToken = default) =>
        RedriveAsync(d, A2AAuditEvent.UnknownOperator, cancellationToken);

    /// <summary>
    /// Queues a dead letter for delivery again, as <see cref="RedriveAsync(PushDelivery, CancellationToken)"/> does, and writes
    /// the audit trail (<see cref="A2AAuditAction.AdminDeadLetterRedriven"/>, with the delivery id as the resource) for
    /// <paramref name="actor"/>. A delivery that is not a dead letter is recorded as <see cref="A2AAuditOutcome.Failed"/> with the
    /// reason <c>NotFound</c>. With <see cref="A2AAuditOptions.FailClosed"/> nothing is queued when the
    /// <see cref="A2AAuditOutcome.Attempted"/> row cannot be written.
    /// </summary>
    /// <param name="d">The dead letter.</param>
    /// <param name="actor">Who sends it again: the operator's id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<bool> RedriveAsync(PushDelivery d, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(actor);
        var e = _audit.For(A2AAuditAction.AdminDeadLetterRedriven, actor, d.Task.Tenant, d.Task.TaskId, d.DeliveryId);
        await _audit.BeginAsync(e, cancellationToken).ConfigureAwait(false);
        bool moved;
        try
        {
            moved = await _outbox.RedriveAsync(d, cancellationToken).ConfigureAwait(false);
            if (moved) _outbox.NotifyPending();
        }
        catch (Exception ex)
        {
            await _audit.FailAsync(e, ex).ConfigureAwait(false);
            throw;
        }
        if (moved) await _audit.SucceedAsync(e).ConfigureAwait(false);
        else await _audit.FailAsync(e, "NotFound").ConfigureAwait(false);
        return moved;
    }
}
