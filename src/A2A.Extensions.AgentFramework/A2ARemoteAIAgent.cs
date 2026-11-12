using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client;
using A2A.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     A remote A2A agent as a Microsoft Agent Framework <see cref="AIAgent" />. Create it with
///     <see cref="A2AClientAgentExtensions.AsAIAgent" />.
/// </summary>
/// <remarks>
///     <para>
///         It sends through <see cref="A2AChatClient" />, so the same rules hold: only the trailing run of user messages is
///         sent, merged into one A2A message (A2A keeps the conversation on the remote agent); system and tool messages are
///         never sent; a response holds this turn's content only.
///     </para>
///     <para>
///         The <see cref="A2ARemoteAgentSession" /> keeps the remote context id, and the task id while the remote task
///         waits for input, so the next run with the same session continues the conversation or the task. Responses carry
///         <see cref="A2AAgentKeys.TaskId" /> and <see cref="A2AAgentKeys.TaskState" /> in their <c>AdditionalProperties</c>.
///     </para>
///     <para>
///         A failed, rejected or canceled remote task throws <see cref="InvalidOperationException" /> with the state only,
///         not the remote text.
///     </para>
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class A2ARemoteAIAgent : AIAgent
{
    private const string ContextIdProperty = "contextId";
    private const string TaskIdProperty = "taskId";

    private readonly A2AChatClient _chat;
    private readonly A2AClient _client;
    private readonly A2AAIAgentOptions _options;

    internal A2ARemoteAIAgent(A2AClient client, A2AAIAgentOptions options)
    {
        _client = client;
        _options = options;
        _chat = client.AsChatClient(new A2AChatClientOptions
        {
            RequestOptions = options.RequestOptions,
            OnArtifactReceived = options.OnArtifactReceived
        });
    }

    /// <inheritdoc />
    public override string? Name => _options.Name;

    /// <inheritdoc />
    public override string? Description => _options.Description;

    /// <inheritdoc />
    protected override string? IdCore => _options.Id;

    /// <summary>
    ///     Also answers <see cref="AIAgentMetadata" /> (provider <c>a2a</c>) and the remote <see cref="AgentCard" /> when the
    ///     client knows it.
    /// </summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null)
        {
            if (serviceType == typeof(AIAgentMetadata)) return new AIAgentMetadata("a2a");
            if (serviceType == typeof(AgentCard)) return _client.AgentCard;
        }

        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new A2ARemoteAgentSession());
    }

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var remote = AsRemote(session);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (remote.ContextId is { } contextId) writer.WriteString(ContextIdProperty, contextId);
            if (remote.TaskId is { } taskId) writer.WriteString(TaskIdProperty, taskId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return ValueTask.FromResult(document.RootElement.Clone());
    }

    /// <inheritdoc />
    /// <exception cref="JsonException">
    ///     The JSON is not an object with only the optional string (or null) properties <c>contextId</c> and
    ///     <c>taskId</c>.
    /// </exception>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        if (serializedState.ValueKind != JsonValueKind.Object)
            throw new JsonException("An A2A remote agent session is a JSON object.");
        var session = new A2ARemoteAgentSession();
        foreach (var property in serializedState.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw new JsonException($"The session property '{property.Name}' must be a string or null.")
            };
            switch (property.Name)
            {
                case ContextIdProperty: session.ContextId = value; break;
                case TaskIdProperty: session.TaskId = value; break;
                default: throw new JsonException($"An A2A remote agent session has no property '{property.Name}'.");
            }
        }

        return ValueTask.FromResult<AgentSession>(session);
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var remote = session is null ? new A2ARemoteAgentSession() : AsRemote(session);
        var chatResponse = await _chat.GetResponseAsync(messages, ChatOptionsFor(remote), cancellationToken)
            .ConfigureAwait(false);
        Track(remote, chatResponse.ConversationId, chatResponse.AdditionalProperties);
        var response = new AgentResponse(chatResponse) { AgentId = Id };
        CopyIds(chatResponse.AdditionalProperties, response.AdditionalProperties ??= []);
        return response;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var remote = session is null ? new A2ARemoteAgentSession() : AsRemote(session);
        await foreach (var chatUpdate in _chat.GetStreamingResponseAsync(messages, ChatOptionsFor(remote),
                               cancellationToken)
                           .ConfigureAwait(false))
        {
            // Kept up to date on every update, so a caller that stops reading early still has the ids.
            Track(remote, chatUpdate.ConversationId, chatUpdate.AdditionalProperties);
            var update = new AgentResponseUpdate(chatUpdate) { AgentId = Id };
            if (chatUpdate.AdditionalProperties is { } properties)
                CopyIds(properties, update.AdditionalProperties ??= []);
            yield return update;
        }
    }

    private static A2ARemoteAgentSession AsRemote(AgentSession session)
    {
        return session as A2ARemoteAgentSession ?? throw new ArgumentException(
            $"The session must be an {nameof(A2ARemoteAgentSession)} made by this agent, not a {session.GetType().Name}.",
            nameof(session));
    }

    private static ChatOptions ChatOptionsFor(A2ARemoteAgentSession session)
    {
        var options = new ChatOptions { ConversationId = session.ContextId };
        if (session.TaskId is { } taskId) options.AdditionalProperties = new() { [A2AChatKeys.TaskId] = taskId };
        return options;
    }

    /// <summary>The context id when named; the task id only while the task waits for input.</summary>
    private static void Track(A2ARemoteAgentSession session, string? conversationId,
        AdditionalPropertiesDictionary? properties)
    {
        if (!string.IsNullOrEmpty(conversationId)) session.ContextId = conversationId;
        if (properties is null || !properties.TryGetValue(A2AChatKeys.TaskState, out var state)) return;
        session.TaskId = state is "input-required" or "auth-required"
                         && properties.TryGetValue(A2AChatKeys.TaskId, out var taskId) && taskId is string id
            ? id
            : null;
    }

    private static void CopyIds(AdditionalPropertiesDictionary? from, AdditionalPropertiesDictionary to)
    {
        if (from is null) return;
        if (from.TryGetValue(A2AChatKeys.TaskId, out var taskId)) to[A2AAgentKeys.TaskId] = taskId;
        if (from.TryGetValue(A2AChatKeys.TaskState, out var state)) to[A2AAgentKeys.TaskState] = state;
    }
}
