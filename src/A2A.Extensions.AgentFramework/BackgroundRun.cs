using System.Runtime.CompilerServices;
using A2A.Client;
using A2A.Extensions.AgentFramework.Shared;
using A2A.Extensions.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Reads a remote task for a background run, from where the caller stands (<see cref="ContinuationState" />). It
///     shows the same content as <see cref="A2AChatClient" />: artifacts are the reply; an interrupted task's status
///     message is its question; a status message that only repeats the artifacts adds nothing.
/// </summary>
internal sealed class BackgroundRun(IA2AClient client, A2ARemoteAgentOptions options)
{
    /// <summary>
    ///     What the caller has already had of <paramref name="taskId" /> (a task it continues): all of its artifacts and its
    ///     status message. Best effort, as the facade does: a failed lookup counts as nothing known.
    /// </summary>
    internal async Task<ContinuationState?> KnownAsync(string? taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(taskId)) return null;
        try
        {
            var existing = await client.GetTaskAsync(taskId, 0, options.RequestOptions, cancellationToken).ConfigureAwait(false);
            var artifacts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (existing.Artifacts is { } list)
                foreach (var artifact in list)
                    artifacts[artifact.ArtifactId] = artifact.Parts.Count;
            return new ContinuationState(taskId, artifacts, existing.Status.Message?.MessageId);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    ///     The reply so far of a task, after <paramref name="seen" />. A task that is neither terminal nor interrupted gets
    ///     a continuation token instead of messages; the content it already has is left for the poll that finds it done.
    /// </summary>
    internal async Task<ChatResponse> ReadAsync(AgentTask task, ContinuationState seen, CancellationToken cancellationToken)
    {
        var properties = Properties(task.Id, task.Status.State);
        if (RemoteAgentSupport.NeedsWait(task.Status.State))
            return new ChatResponse([])
            {
                ConversationId = task.ContextId, ResponseId = task.Id, AdditionalProperties = properties,
                ContinuationToken = ContinuationTokens.For(seen with { TaskId = task.Id })
            };
        RemoteAgentSupport.ThrowIfFailed(task.Status.State);

        var messages = new List<ChatMessage>();
        var text = new System.Text.StringBuilder();
        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
            {
                if (Unseen(artifact, seen.Artifacts) is not { } fresh) continue;
                if (options.OnArtifactReceived is { } hook) await hook(fresh, cancellationToken).ConfigureAwait(false);
                text.Append(TextOf(fresh.Parts));
                messages.Add(fresh.ToChatMessage());
            }

        // Judged on the whole task, not only on what is new since the token: a caller that already had the artifacts
        // (from a stream that dropped) must not get them again as the status message.
        var anyArtifacts = task.Artifacts is { Count: > 0 } all && all.Any(a => a.Parts.Count > 0);
        var allText = task.Artifacts is { } every ? string.Concat(every.Select(a => TextOf(a.Parts))) : "";
        if ((task.Status.State.IsInterrupted() || !anyArtifacts) && task.Status.Message is { } status &&
            status.MessageId != seen.StatusMessageId && !Echoes(status.Parts, text.ToString()) &&
            !Echoes(status.Parts, allText))
            messages.Add(AgentMessage(status));
        return new ChatResponse(messages)
        {
            ConversationId = task.ContextId, ResponseId = task.Id, AdditionalProperties = properties
        };
    }

    /// <summary>
    ///     A stream of a task's events as updates, from <paramref name="seen" /> on. Every update but the last carries a
    ///     continuation token at the point just after it; the last (a terminal or interrupted state) carries none.
    /// </summary>
    internal async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(IAsyncEnumerable<StreamResponse> events,
        ContinuationState seen, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parts = new Dictionary<string, int>(seen.Artifacts, StringComparer.Ordinal);
        var status = seen.StatusMessageId;
        var taskId = seen.TaskId;
        var text = new System.Text.StringBuilder();
        // A resumed stream: the caller already had these parts, so the turn had artifacts.
        var hadArtifacts = seen.Artifacts.Values.Any(n => n > 0);
        string? snapshotText = null; // the text of every artifact, once a task snapshot gave them all

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            switch (evt)
            {
                case MessageEvent { Message: var reply }:
                    // A direct reply is the whole answer: nothing to resume.
                    yield return Update(reply.ContextId, reply.TaskId, null, reply.MessageId, reply.Parts, null, null);
                    yield break;

                case ArtifactUpdateEvent { ArtifactUpdate: var chunk }:
                {
                    taskId = chunk.TaskId;
                    if (snapshotText is not null) snapshotText += TextOf(chunk.Artifact.Parts);
                    var before = chunk.Append && parts.TryGetValue(chunk.Artifact.ArtifactId, out var n) ? n : 0;
                    parts[chunk.Artifact.ArtifactId] = before + chunk.Artifact.Parts.Count;
                    if (options.OnArtifactReceived is { } hook) await hook(chunk.Artifact, cancellationToken).ConfigureAwait(false);
                    text.Append(TextOf(chunk.Artifact.Parts));
                    hadArtifacts = true;
                    yield return Update(chunk.ContextId, chunk.TaskId, null, chunk.Artifact.ArtifactId, chunk.Artifact.Parts,
                        null, Token());
                    break;
                }

                case TaskEvent { Task: var snapshot }:
                    taskId = snapshot.Id;
                    snapshotText = snapshot.Artifacts is { } all ? string.Concat(all.Select(a => TextOf(a.Parts))) : "";
                    if (snapshot.Artifacts is { } artifacts)
                        foreach (var artifact in artifacts)
                        {
                            if (Unseen(artifact, parts) is not { } fresh) continue;
                            parts[artifact.ArtifactId] = artifact.Parts.Count;
                            if (options.OnArtifactReceived is { } hook) await hook(fresh, cancellationToken).ConfigureAwait(false);
                            text.Append(TextOf(fresh.Parts));
                            hadArtifacts = true;
                            yield return Update(snapshot.ContextId, snapshot.Id, null, artifact.ArtifactId, fresh.Parts, null, Token());
                        }

                    foreach (var update in StatusUpdate(snapshot.ContextId, snapshot.Id, snapshot.Status))
                        yield return update;
                    if (IsLast(snapshot.Status.State)) yield break;
                    break;

                case StatusUpdateEvent { StatusUpdate: var change }:
                    taskId = change.TaskId;
                    foreach (var update in StatusUpdate(change.ContextId, change.TaskId, change.Status))
                        yield return update;
                    if (IsLast(change.Status.State)) yield break;
                    break;
            }

        ResponseContinuationToken Token() =>
            ContinuationTokens.For(new ContinuationState(taskId, new Dictionary<string, int>(parts, StringComparer.Ordinal), status));

        IEnumerable<ChatResponseUpdate> StatusUpdate(string? contextId, string? id, AgentTaskStatus taskStatus)
        {
            var state = taskStatus.State;
            RemoteAgentSupport.ThrowIfFailed(state);
            if (state is TaskState.Working or TaskState.Submitted or TaskState.Unspecified) yield break;
            var message = taskStatus.Message;
            var show = (state.IsInterrupted() || !hadArtifacts) && message is not null &&
                       message.MessageId != status && !Echoes(message.Parts, text.ToString()) &&
                       !(snapshotText is not null && Echoes(message.Parts, snapshotText));
            if (message is not null) status = message.MessageId;
            yield return Update(contextId, id, state, message?.MessageId, show ? message!.Parts : [],
                state == TaskState.Completed ? ChatFinishReason.Stop : null, IsLast(state) ? null : Token());
        }
    }

    /// <summary>The end of this turn: a terminal state, or an interrupted one that waits for the caller.</summary>
    private static bool IsLast(TaskState state) => state.IsTerminal() || state.IsInterrupted();

    /// <summary>The parts of <paramref name="artifact" /> after the ones already seen, or null when there are none.</summary>
    private static Artifact? Unseen(Artifact artifact, IReadOnlyDictionary<string, int> seen)
    {
        var skip = seen.TryGetValue(artifact.ArtifactId, out var n) ? n : 0;
        if (artifact.Parts.Count <= skip) return null;
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId, Name = artifact.Name, Description = artifact.Description,
            Metadata = artifact.Metadata, Parts = artifact.Parts.Skip(skip).ToList()
        };
    }

    private static string TextOf(IEnumerable<Part> parts) => string.Concat(parts.OfType<TextPart>().Select(p => p.Text));

    private static bool Echoes(List<Part> statusParts, string artifactText) =>
        artifactText.Length > 0 && string.Equals(TextOf(statusParts), artifactText, StringComparison.Ordinal);

    private static ChatMessage AgentMessage(Message message)
    {
        var chatMessage = message.ToChatMessage();
        chatMessage.Role = ChatRole.Assistant;
        return chatMessage;
    }

    private static ChatResponseUpdate Update(string? contextId, string? taskId, TaskState? state, string? messageId,
        List<Part> parts, ChatFinishReason? finish, ResponseContinuationToken? token)
    {
        var contents = new List<AIContent>(parts.Count);
        foreach (var part in parts) contents.Add(part.ToAIContent());
        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ConversationId = string.IsNullOrEmpty(contextId) ? null : contextId,
            MessageId = messageId,
            FinishReason = finish,
            AdditionalProperties = Properties(taskId, state),
            ContinuationToken = token
        };
    }

    private static AdditionalPropertiesDictionary? Properties(string? taskId, TaskState? state)
    {
        if (string.IsNullOrEmpty(taskId) && state is null) return null;
        var properties = new AdditionalPropertiesDictionary();
        if (!string.IsNullOrEmpty(taskId)) properties[A2AChatKeys.TaskId] = taskId;
        if (state is { } s) properties[A2AChatKeys.TaskState] = RemoteAgentSupport.StateName(s);
        return properties;
    }
}
