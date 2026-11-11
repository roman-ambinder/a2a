using System.Runtime.CompilerServices;
using System.Text;
using A2A.Client;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>
///     Uses a remote A2A agent through <see cref="IChatClient" />. Create it with
///     <see cref="A2AClientAIExtensions.AsChatClient" />.
/// </summary>
/// <remarks>
///     <para>
///         Only the trailing run of user messages is sent, merged into one A2A message: A2A keeps the conversation on the
///         server.
///         System and tool messages are never sent, so your prompts do not reach the remote agent.
///         <see cref="ChatOptions.Tools" /> are ignored.
///     </para>
///     <para>
///         <see cref="ChatOptions.ConversationId" /> is the A2A context id, and the response has the context id back as
///         its
///         <see cref="ChatResponse.ConversationId" />. To continue a task that needs input, put the task id in
///         <see cref="ChatOptions.AdditionalProperties" /> under <see cref="A2AChatKeys.TaskId" />.
///         Responses carry <see cref="A2AChatKeys.TaskId" /> and <see cref="A2AChatKeys.TaskState" />.
///     </para>
///     <para>
///         A failed, rejected or canceled task throws <see cref="InvalidOperationException" /> with the state only, not
///         the remote text.
///     </para>
/// </remarks>
public sealed class A2AChatClient : IChatClient
{
    private readonly A2AClient _client;
    private readonly ChatClientMetadata _metadata;
    private readonly A2AChatClientOptions _options;

    internal A2AChatClient(A2AClient client, A2AChatClientOptions options)
    {
        _client = client;
        _options = options;
        _metadata = new ChatClientMetadata("a2a", null, client.AgentCard?.Name);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildMessage(messages, options);
        var known = await RemoteAgentSupport
            .LookupKnownAsync(_client, request.TaskId, _options.RequestOptions, cancellationToken, cancellationToken)
            .ConfigureAwait(false);
        var response = await _client.SendMessageAsync(request, null, _options.RequestOptions, cancellationToken)
            .ConfigureAwait(false);
        switch (response)
        {
            case MessageResponse { Message: var reply }:
            {
                var chatMessage = reply.ToChatMessage();
                chatMessage.Role = ChatRole.Assistant;
                return new ChatResponse(chatMessage)
                {
                    ConversationId = reply.ContextId,
                    ResponseId = reply.MessageId,
                    AdditionalProperties = Properties(reply.TaskId, null),
                    RawRepresentation = response
                };
            }
            case TaskResponse { Task: var task }:
            {
                if (RemoteAgentSupport.NeedsWait(task.Status.State))
                    task = await _client.WaitForTaskAsync(task.Id, null, _options.RequestOptions, cancellationToken)
                        .ConfigureAwait(false);
                ThrowIfFailed(task.Status.State);

                var chatMessages = new List<ChatMessage>();
                var fresh = task.Artifacts?.Where(a => !known.ArtifactIds.Contains(a.ArtifactId)).ToList();
                if (fresh is not null)
                    foreach (var artifact in fresh)
                    {
                        if (_options.OnArtifactReceived is { } hook)
                            await hook(artifact, cancellationToken).ConfigureAwait(false);
                        chatMessages.Add(artifact.ToChatMessage());
                    }

                // An interrupted task asks something: its status message is the question. Otherwise the artifacts are the
                // reply, else the status message (an agent that ends with CompleteAsync("text")), as A2AAgentFunction does.
                if ((task.Status.State.IsInterrupted() || fresh is not { Count: > 0 }) &&
                    task.Status.Message is { } status && status.MessageId != known.StatusMessageId &&
                    !EchoesArtifacts(status.Parts, ArtifactText(fresh)))
                    chatMessages.Add(AgentMessage(status));
                return new ChatResponse(chatMessages)
                {
                    ConversationId = task.ContextId,
                    ResponseId = task.Id,
                    AdditionalProperties = Properties(task.Id, task.Status.State),
                    RawRepresentation = response
                };
            }
            default:
                throw new InvalidOperationException("The remote agent sent an unknown response.");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildMessage(messages, options);
        var known = await RemoteAgentSupport
            .LookupKnownAsync(_client, request.TaskId, _options.RequestOptions, cancellationToken, cancellationToken)
            .ConfigureAwait(false);
        var chunked = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var artifactText = new StringBuilder();

        await foreach (var evt in _client
                           .SendStreamingMessageAsync(request, null, _options.RequestOptions, cancellationToken)
                           .ConfigureAwait(false))
            switch (evt)
            {
                case MessageEvent { Message: var reply }:
                    yield return Update(reply.ContextId, reply.TaskId, null, reply.MessageId, reply.Parts, null);
                    break;

                case ArtifactUpdateEvent { ArtifactUpdate: var chunk }:
                    if (_options.OnArtifactReceived is { } chunkHook)
                        await chunkHook(chunk.Artifact, cancellationToken).ConfigureAwait(false);
                    chunked.Add(chunk.Artifact.ArtifactId);
                    artifactText.Append(TextOf(chunk.Artifact.Parts));
                    yield return Update(chunk.ContextId, chunk.TaskId, null, chunk.Artifact.ArtifactId,
                        chunk.Artifact.Parts, null);
                    break;

                case StatusUpdateEvent { StatusUpdate: var change }:
                    foreach (var update in StatusUpdates(change.ContextId, change.TaskId, change.Status,
                                 artifactText.ToString(), chunked.Count > 0 || emitted.Count > 0,
                                 known.StatusMessageId)) yield return update;
                    // This SDK's server keeps the stream open at an interrupted state, waiting for a follow-up. The turn is over:
                    // stop here, or a caller that reads to the end would wait for the stream duration limit.
                    if (change.Status.State.IsInterrupted()) yield break;
                    break;

                case TaskEvent { Task: var snapshot }:
                    if (snapshot.Artifacts is { } artifacts)
                        foreach (var artifact in artifacts)
                        {
                            if (known.ArtifactIds.Contains(artifact.ArtifactId) ||
                                chunked.Contains(artifact.ArtifactId) || !emitted.Add(artifact.ArtifactId)) continue;
                            if (_options.OnArtifactReceived is { } hook)
                                await hook(artifact, cancellationToken).ConfigureAwait(false);
                            artifactText.Append(TextOf(artifact.Parts));
                            yield return Update(snapshot.ContextId, snapshot.Id, null, artifact.ArtifactId,
                                artifact.Parts, null);
                        }

                    foreach (var update in StatusUpdates(snapshot.ContextId, snapshot.Id, snapshot.Status,
                                 artifactText.ToString(), chunked.Count > 0 || emitted.Count > 0,
                                 known.StatusMessageId)) yield return update;
                    // A snapshot of the existing task starts a continued stream. Its old interrupted status is not this turn's end.
                    if (snapshot.Status.State.IsInterrupted() &&
                        snapshot.Status.Message?.MessageId != known.StatusMessageId) yield break;
                    break;
            }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Does nothing. The <see cref="A2AClient" /> belongs to the caller.</summary>
    public void Dispose()
    {
    }

    private static IEnumerable<ChatResponseUpdate> StatusUpdates(string? contextId, string? taskId,
        AgentTaskStatus status, string artifactText, bool hadArtifacts, string? knownStatusMessageId)
    {
        var state = status.State;
        ThrowIfFailed(state);
        if (state is TaskState.Working or TaskState.Submitted or TaskState.Unspecified)
            yield break; // progress is not surfaced
        // A ChatClientAgentExecutor with KeepConversationOpen sends its reply as artifact chunks and again as the status message.
        // The caller must not see the same text twice, so an interrupted status that only repeats the artifacts adds no content.
        // Any other state shows its status message only when the turn sent no artifacts (the agent replied in the status).
        var parts = (state.IsInterrupted() || !hadArtifacts) && status.Message is { } message &&
                    message.MessageId != knownStatusMessageId && !EchoesArtifacts(message.Parts, artifactText)
            ? message.Parts
            : [];
        yield return Update(contextId, taskId, state, status.Message?.MessageId, parts,
            state == TaskState.Completed ? ChatFinishReason.Stop : null);
    }

    private static string TextOf(IEnumerable<Part> parts)
    {
        return string.Concat(parts.OfType<TextPart>().Select(p => p.Text));
    }

    private static string ArtifactText(IEnumerable<Artifact>? artifacts)
    {
        return artifacts is null ? string.Empty : string.Concat(artifacts.Select(a => TextOf(a.Parts)));
    }

    private static bool EchoesArtifacts(List<Part> statusParts, string artifactText)
    {
        return artifactText.Length > 0 && string.Equals(TextOf(statusParts), artifactText, StringComparison.Ordinal);
    }

    private static ChatResponseUpdate Update(string? contextId, string? taskId, TaskState? state, string? messageId,
        List<Part> parts, ChatFinishReason? finish)
    {
        var contents = new List<AIContent>(parts.Count);
        foreach (var part in parts) contents.Add(part.ToAIContent());
        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ConversationId = string.IsNullOrEmpty(contextId) ? null : contextId,
            MessageId = messageId,
            FinishReason = finish,
            AdditionalProperties = Properties(taskId, state)
        };
    }

    private static ChatMessage AgentMessage(Message message)
    {
        var chatMessage = message.ToChatMessage();
        chatMessage.Role = ChatRole.Assistant;
        return chatMessage;
    }

    private static AdditionalPropertiesDictionary? Properties(string? taskId, TaskState? state)
    {
        if (string.IsNullOrEmpty(taskId) && state is null) return null;
        var properties = new AdditionalPropertiesDictionary();
        if (!string.IsNullOrEmpty(taskId)) properties[A2AChatKeys.TaskId] = taskId;
        if (state is { } s) properties[A2AChatKeys.TaskState] = RemoteAgentSupport.StateName(s);
        return properties;
    }

    private static void ThrowIfFailed(TaskState state)
    {
        if (state is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
            throw new InvalidOperationException(
                $"The remote agent ended the task in state '{RemoteAgentSupport.StateName(state)}'.");
    }

    private static Message BuildMessage(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var start = list.Count;
        while (start > 0 && list[start - 1].Role == ChatRole.User) start--;
        if (start == list.Count)
            throw new ArgumentException("The messages must end with at least one user message.", nameof(messages));

        var result = new Message { MessageId = Message.NewId(), Role = Role.User, Parts = [] };
        // Message and content properties are caller side data (they can hold anything). They are never sent to the remote agent.
        for (var i = start; i < list.Count; i++)
            foreach (var content in list[i].Contents)
                if (A2AContentExtensions.ConvertToPart(content, false) is { } part)
                    result.Parts.Add(part);
        if (result.Parts.Count == 0)
            throw new ArgumentException("The user messages have no content that A2A can carry.", nameof(messages));

        result.ContextId = string.IsNullOrEmpty(options?.ConversationId) ? null : options.ConversationId;
        if (options?.AdditionalProperties is { } extra && extra.TryGetValue(A2AChatKeys.TaskId, out var taskId) &&
            taskId is string id && id.Length > 0)
            result.TaskId = id;
        return result;
    }
}