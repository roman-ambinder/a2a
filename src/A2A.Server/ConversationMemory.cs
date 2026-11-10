using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>
///     What an agent can remember of its conversation: the messages of earlier tasks in the same context, and a rolling
///     summary.
///     Read it from <see cref="AgentRequestContext.Conversation" />. It is scoped to the caller of this request: the same
///     tenant and
///     owner, this context, and it never shows the current task.
/// </summary>
/// <remarks>
///     Messages come from the stored tasks, so deleted tasks (retention) leave the memory. The summary is written by your
///     code
///     (<see cref="SetSummaryAsync" />); the server never writes one. Text in earlier messages and in the summary comes
///     from the caller:
///     give it to a model as user content, never as system content.
/// </remarks>
public sealed class ConversationMemory
{
    /// <summary>The default number of earlier tasks to read: 20.</summary>
    public const int DefaultMaxTasks = 20;

    /// <summary>The default size limit for earlier messages: 262144 bytes (256 KiB) of JSON.</summary>
    public const long DefaultMaxBytes = 262_144;

    private readonly string _contextId;
    private readonly string _owner;

    private readonly TaskStore _store;
    private readonly string _taskId;
    private readonly string _tenant;

    internal ConversationMemory(TaskStore store, string tenant, string owner, string contextId, string taskId)
    {
        _store = store;
        _tenant = tenant;
        _owner = owner;
        _contextId = contextId;
        _taskId = taskId;
    }

    /// <summary>
    ///     True when the store can keep a summary (it implements <see cref="IContextSummaryStore" />). When false,
    ///     <see cref="SetSummaryAsync" /> throws.
    /// </summary>
    public bool CanStoreSummary => _store is IContextSummaryStore;

    /// <summary>
    ///     The messages of earlier tasks in this context, oldest first, without the current task and without rejected tasks.
    ///     Both limits always apply and the first one reached wins; the size limit cuts from the newest message back, so there
    ///     is
    ///     never a hole. <paramref name="maxBytes" /> is the UTF-8 length of each message's JSON, summed.
    /// </summary>
    /// <remarks>
    ///     The cost depends on the store. The PostgreSQL store cuts the history in the database. A store that does not
    ///     override
    ///     <see cref="TaskStore.ReadContextMessagesAsync" /> (a third-party store, and the in-memory store, where it costs
    ///     little)
    ///     loads whole tasks, artifacts included, and cuts afterward.
    /// </remarks>
    /// <param name="maxTasks">How many earlier tasks to read, at least 1.</param>
    /// <param name="maxBytes">The size limit, at least 1.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below 1.</exception>
    public ValueTask<IReadOnlyList<Message>> ReadEarlierMessagesAsync(int maxTasks = DefaultMaxTasks,
        long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTasks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L);
        return _store.ReadContextMessagesAsync(
            new ContextHistoryQuery(_tenant, _owner, _contextId, maxTasks, maxBytes, _taskId), cancellationToken);
    }

    /// <summary>
    ///     The summary of this context, or null when there is none, the context is missing, or it belongs to another
    ///     owner.
    /// </summary>
    public async ValueTask<string?> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var context = await _store.GetContextAsync(_tenant, _contextId, cancellationToken).ConfigureAwait(false);
        return context is not null && string.Equals(context.Owner, _owner, StringComparison.Ordinal)
            ? context.Summary
            : null;
    }

    /// <summary>
    ///     Sets the summary of this context, or clears it with null. Last write wins; tasks that run at the same time do
    ///     not merge their summaries.
    /// </summary>
    /// <param name="summary">The summary, or null to clear it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="NotSupportedException">The store cannot keep a summary (<see cref="CanStoreSummary" /> is false).</exception>
    /// <exception cref="InvalidOperationException">The context does not exist or belongs to another owner.</exception>
    public async ValueTask SetSummaryAsync(string? summary, CancellationToken cancellationToken = default)
    {
        if (_store is not IContextSummaryStore summaries)
            throw new NotSupportedException(
                "This task store cannot keep a conversation summary. It must implement IContextSummaryStore.");
        if (!await summaries.SetContextSummaryAsync(_tenant, _owner, _contextId, summary, cancellationToken)
                .ConfigureAwait(false))
            throw new InvalidOperationException("The context does not exist or belongs to another caller.");
    }
}