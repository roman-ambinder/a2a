using System.Runtime.CompilerServices;

namespace A2A.Client;

/// <summary>
///     A conversation with one agent: it keeps the context id and the id of a task that waits for input, so each turn is
///     just the next text. Create it with <see cref="A2AConversationExtensions.StartConversation" />.
/// </summary>
/// <remarks>
///     <para>
///         After each reply, <see cref="ContextId" /> takes the reply's context id, and <see cref="TaskId" /> keeps the
///         task's id only while the task waits for input or authorization. A task that ended cannot take more messages, so
///         the next send starts a new task in the same context.
///     </para>
///     <para>One turn at a time: a send while another one is running throws <see cref="InvalidOperationException" />.</para>
/// </remarks>
public sealed class A2AConversation
{
    private readonly IA2AClient _client;
    private readonly A2ARequestOptions? _options;
    private int _busy;

    internal A2AConversation(IA2AClient client, string? contextId, A2ARequestOptions? options)
    {
        _client = client;
        _options = options;
        ContextId = contextId;
    }

    /// <summary>The context id the next message is sent with. Null until the agent assigns one.</summary>
    public string? ContextId { get; private set; }

    /// <summary>
    ///     The task that waits for input or authorization, which the next message answers. Null when the next message starts
    ///     a new task.
    /// </summary>
    public string? TaskId { get; private set; }

    /// <summary>The reply of the last turn, or null before the first one.</summary>
    public AgentReply? LastReply { get; private set; }

    /// <summary>
    ///     Sends <paramref name="text" /> as the next turn and returns the reply. A task that is still running is polled every
    ///     second until it ends or needs input or authorization.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <param name="cancellationToken">Cancels the call and the polling.</param>
    public Task<AgentReply> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SendAsync(Message.FromUser(text), cancellationToken);
    }

    /// <summary>
    ///     Sends <paramref name="message" /> as the next turn and returns the reply. The message's context and task ids are
    ///     filled in from the conversation when they are not set. A task that is still running is polled every second until
    ///     it ends or needs input or authorization.
    /// </summary>
    /// <param name="message">The message, for example with file or data parts.</param>
    /// <param name="cancellationToken">Cancels the call and the polling.</param>
    public async Task<AgentReply> SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Enter();
        try
        {
            Address(message);
            var response = await _client.SendMessageAsync(new SendMessageRequest { Message = message }, _options,
                cancellationToken).ConfigureAwait(false);
            var reply = response switch
            {
                MessageResponse { Message: var direct } => new AgentReply(direct),
                TaskResponse { Task: var task } => await Settle(task, cancellationToken).ConfigureAwait(false),
                _ => throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent sent no result.")
            };
            return Record(reply);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    ///     Sends <paramref name="text" /> as the next turn over a stream and yields the agent's text as it arrives, like
    ///     <see cref="A2AClientTextExtensions.StreamTextAsync" />. When the enumeration ends, <see cref="LastReply" />
    ///     holds the reply and the ids are updated. Nothing is thrown for a task state.
    /// </summary>
    /// <param name="text">The message text.</param>
    /// <param name="cancellationToken">Cancels the stream and the polling.</param>
    /// <remarks>
    ///     The agent must declare streaming on its card. When the stream ends before the task ends or needs input, the task
    ///     is polled, so <see cref="LastReply" /> is final. <see cref="AgentReply.Text" /> is the text that was yielded.
    /// </remarks>
    public async IAsyncEnumerable<string> StreamTextAsync(string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        Enter();
        try
        {
            var message = Message.FromUser(text);
            Address(message);
            var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
            var streamed = new System.Text.StringBuilder();
            AgentTask? last = null;
            Message? direct = null;
            await foreach (var evt in _client.SendStreamingMessageAsync(new SendMessageRequest { Message = message },
                               _options, cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case TaskEvent { Task: var task }:
                        last = task;
                        foreach (var artifact in task.Artifacts ?? [])
                            if (seenArtifacts.Add(artifact.ArtifactId) && artifact.GetText() is { Length: > 0 } t)
                            {
                                streamed.Append(t);
                                yield return t;
                            }

                        break;
                    case StatusUpdateEvent { StatusUpdate: var status }:
                        last = new AgentTask
                        {
                            Id = status.TaskId, ContextId = status.ContextId, Status = status.Status,
                            Artifacts = last?.Artifacts
                        };
                        break;
                    case ArtifactUpdateEvent { ArtifactUpdate.Artifact: var artifact }:
                        seenArtifacts.Add(artifact.ArtifactId);
                        break;
                    case MessageEvent { Message: var m }:
                        direct = m;
                        break;
                }

                if (evt.GetText() is { Length: > 0 } chunk)
                {
                    streamed.Append(chunk);
                    yield return chunk;
                }

                if (evt.State is { } state && (state.IsTerminal() || state.IsInterrupted())) break;
                if (direct is not null) break;
            }

            AgentReply reply;
            if (direct is not null)
                reply = new AgentReply(direct);
            else if (last is null)
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent sent no result.");
            else if (!last.Status.State.IsTerminal() && !last.Status.State.IsInterrupted())
            {
                var settled = await A2AClientTextExtensions.WaitAsync(_client, last.Id, _options, cancellationToken)
                    .ConfigureAwait(false);
                reply = new AgentReply(settled, settled.GetText());
            }
            else
                reply = new AgentReply(last, streamed.ToString());

            Record(reply);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Enter()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            throw new InvalidOperationException(
                "A turn of this conversation is still running. Wait for it before sending the next message.");
    }

    private void Address(Message message)
    {
        message.ContextId ??= ContextId;
        message.TaskId ??= TaskId;
    }

    private async Task<AgentReply> Settle(AgentTask task, CancellationToken cancellationToken)
    {
        if (!task.Status.State.IsTerminal() && !task.Status.State.IsInterrupted())
            task = await A2AClientTextExtensions.WaitAsync(_client, task.Id, _options, cancellationToken)
                .ConfigureAwait(false);
        return new AgentReply(task, task.GetText());
    }

    private AgentReply Record(AgentReply reply)
    {
        if (reply.ContextId is not null) ContextId = reply.ContextId;
        TaskId = reply.State is { } state && state.IsInterrupted() ? reply.TaskId : null;
        LastReply = reply;
        return reply;
    }
}

/// <summary>Starts an <see cref="A2AConversation" /> on any <see cref="IA2AClient" />.</summary>
public static class A2AConversationExtensions
{
    /// <summary>
    ///     Starts a conversation with the agent. Each <see cref="A2AConversation.SendAsync(string, CancellationToken)" />
    ///     continues the same context, and answers a task that waits for input.
    /// </summary>
    /// <param name="client">The client: an <see cref="A2AClient" />, or any other <see cref="IA2AClient" />.</param>
    /// <param name="contextId">An existing context to continue, or null for a new one.</param>
    /// <param name="options">Options used for every call of the conversation.</param>
    public static A2AConversation StartConversation(this IA2AClient client, string? contextId = null,
        A2ARequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new A2AConversation(client, contextId, options);
    }
}
