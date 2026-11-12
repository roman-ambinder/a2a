using System.Runtime.CompilerServices;

namespace A2A.Client;

/// <summary>
///     Text in, text out: <see cref="AskAsync" /> and <see cref="StreamTextAsync" /> for any <see cref="IA2AClient" />,
///     for callers that only need the agent's answer as text.
/// </summary>
public static class A2AClientTextExtensions
{
    /// <summary>
    ///     Sends <paramref name="text" /> and returns the agent's answer as text: the text of a direct reply, or the text of
    ///     the task's artifacts once the task has completed. A task that is still running when the call returns is polled
    ///     every second, like <see cref="A2AClient.WaitForTaskAsync" />.
    /// </summary>
    /// <param name="client">The client: an <see cref="A2AClient" />, or any other <see cref="IA2AClient" />.</param>
    /// <param name="text">The message text.</param>
    /// <param name="contextId">The context (conversation) to continue, or null for a new one.</param>
    /// <param name="options">Options for the call.</param>
    /// <param name="cancellationToken">Cancels the call and the polling.</param>
    /// <exception cref="TaskNotCompletedException">
    ///     The task ended in a state other than completed (failed, rejected, canceled), or the agent needs input or
    ///     authorization.
    /// </exception>
    public static async Task<string> AskAsync(this IA2AClient client, string text, string? contextId = null, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(text);
        var response = await client.SendMessageAsync(
                new SendMessageRequest { Message = Message.FromUser(text, contextId) }, options, cancellationToken)
            .ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var reply }:
                return reply.GetText();
            case TaskResponse { Task: var task }:
                if (!task.Status.State.IsTerminal() && !task.Status.State.IsInterrupted())
                    task = await WaitAsync(client, task.Id, options, cancellationToken).ConfigureAwait(false);
                if (task.Status.State != TaskState.Completed) throw new TaskNotCompletedException(task);
                return task.GetText();
            default:
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, "The agent sent no result.");
        }
    }

    /// <summary>
    ///     Sends <paramref name="text" /> as a streaming message and yields the agent's text as it arrives: each chunk of an
    ///     artifact, and the text of a direct reply. Status updates without text are skipped.
    /// </summary>
    /// <param name="client">The client: an <see cref="A2AClient" />, or any other <see cref="IA2AClient" />.</param>
    /// <param name="text">The message text.</param>
    /// <param name="contextId">The context (conversation) to continue, or null for a new one.</param>
    /// <param name="options">Options for the call.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    /// <remarks>
    ///     A task snapshot in the stream yields the text of the artifacts not seen before, so text is not repeated. The agent
    ///     must declare streaming on its card (<see cref="AgentCapabilities.Streaming" />). The enumeration ends when the task
    ///     reaches a terminal state or needs input or authorization, even when the server keeps the stream open; the task
    ///     itself goes on.
    /// </remarks>
    /// <exception cref="TaskNotCompletedException">
    ///     After the last chunk: the task ended in a state other than completed, or the agent needs input or authorization.
    /// </exception>
    public static async IAsyncEnumerable<string> StreamTextAsync(this IA2AClient client, string text, string? contextId = null,
        A2ARequestOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(text);
        var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
        AgentTask? ended = null;
        await foreach (var evt in client.SendStreamingMessageAsync(
                               new SendMessageRequest { Message = Message.FromUser(text, contextId) }, options,
                               cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TaskEvent { Task: var task }:
                    foreach (var artifact in task.Artifacts ?? [])
                        if (seenArtifacts.Add(artifact.ArtifactId) && artifact.GetText() is { Length: > 0 } t)
                            yield return t;
                    break;
                case ArtifactUpdateEvent { ArtifactUpdate.Artifact: var artifact }:
                    seenArtifacts.Add(artifact.ArtifactId);
                    break;
            }

            if (evt.GetText() is { Length: > 0 } chunk) yield return chunk;
            if (evt.State is not { } state || (!state.IsTerminal() && !state.IsInterrupted())) continue;
            if (state == TaskState.Completed) yield break;
            // A server can keep the stream open while the task waits for input; leaving it does not stop the task.
            ended = evt is TaskEvent { Task: var snapshot }
                ? snapshot
                : new AgentTask
                {
                    Id = evt.TaskId ?? string.Empty,
                    ContextId = evt.ContextId ?? string.Empty,
                    Status = ((StatusUpdateEvent)evt).StatusUpdate.Status
                };
            break;
        }

        if (ended is not null) throw new TaskNotCompletedException(ended);
    }

    /// <summary>Polls the task until it is terminal or interrupted, like <see cref="A2AClient.WaitForTaskAsync" />.</summary>
    internal static async Task<AgentTask> WaitAsync(IA2AClient client, string taskId, A2ARequestOptions? options,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = 0 }, options,
                cancellationToken).ConfigureAwait(false);
            if (task.Status.State.IsTerminal() || task.Status.State.IsInterrupted()) return task;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }
}
