using A2A.Server.Storage;

namespace A2A.Server.Retention;

/// <summary>
///     Told when the retention sweep (<see cref="A2ARetentionService" />) deletes a context, so state kept outside the task
///     store for that context (an agent session, a cache) can go with it. Register any number in DI, as singletons.
/// </summary>
/// <remarks>
///     The sweep deletes a context once no task uses it and it is older than
///     <see cref="A2ARetentionOptions.OrphanContextRetention" />. Each hook is called once per deleted context, after the
///     delete, on the instance that holds the sweep lease. The context is already gone, so an exception cannot undo it: it
///     is logged, and the other hooks and the rest of the sweep go on. The sweep does not call the hook again for that
///     context. Contexts that a store deletes by other means (an admin delete, a store's own cleanup) are not reported.
///     A new message can create the context again under the same id right after the delete. The sweep then skips the
///     hooks if it sees the context back, but that check can come too early. Key state by the context's
///     <see cref="ContextRecord.CreatedAt" /> as well as its id: the context created again has a later one.
/// </remarks>
public interface IA2AContextLifecycle
{
    /// <summary>Called once for each context the retention sweep deleted.</summary>
    /// <param name="context">The deleted context: its tenant, owner and id.</param>
    /// <param name="cancellationToken">Cancelled when the sweep stops.</param>
    ValueTask OnContextDeletedAsync(ContextRecord context, CancellationToken cancellationToken);
}
