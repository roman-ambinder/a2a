using System.Text;
using A2A.Server;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>
///     Runs an <see cref="IChatClient" /> as an A2A agent. The reply streams to the caller as chunks of one artifact.
/// </summary>
/// <remarks>
///     <para>
///         Tools work through your <see cref="IChatClient" /> pipeline (<c>UseFunctionInvocation</c>) and
///         <see cref="ChatOptions.Tools" />. Reasoning, usage and tool details are never sent to the caller.
///     </para>
///     <para>
///         A model error, a content filter stop, or a tool call that nothing resolved throws
///         <see cref="InvalidOperationException" />.
///         The server then marks the task failed with a generic message and logs the real error. Cancellation ends the
///         task as canceled.
///     </para>
///     <para>
///         By default history is per task: a new task in the same context does not see earlier tasks. Turn on
///         <see cref="ChatClientAgentOptions.UseContextMemory" /> to give the model the earlier tasks of the context, or
///         use
///         <see cref="ChatClientAgentOptions.KeepConversationOpen" /> to continue one task.
///     </para>
/// </remarks>
public sealed class ChatClientAgentExecutor : IAgentExecutor
{
    private const string SummaryPrefix = "Summary of the earlier conversation:\n";
    private const string UrlRejectedText = "URL parts are not accepted by this agent.";

    private readonly IChatClient _chatClient;
    private readonly ChatClientAgentOptions _options;

    /// <summary>
    ///     Creates an executor over <paramref name="chatClient" />. <paramref name="options" /> defaults to a new
    ///     <see cref="ChatClientAgentOptions" />.
    /// </summary>
    public ChatClientAgentExecutor(IChatClient chatClient, ChatClientAgentOptions? options = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new ChatClientAgentOptions();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(updater);

        if (!_options.AllowUrlParts && context.Message.Parts.Any(p => p is UrlPart))
        {
            await updater.RejectAsync(UrlRejectedText, cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = await BuildPromptAsync(context, cancellationToken).ConfigureAwait(false);
        var chatOptions = _options.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.ConversationId = null;

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var run = new Run(_options, updater);
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason == ChatFinishReason.ContentFilter)
                throw new InvalidOperationException("The model stopped because of its content filter.");
            foreach (var content in update.Contents)
                await run.AddAsync(content, cancellationToken).ConfigureAwait(false);
        }

        if (run.HasUnresolvedToolCall && !run.HasOutput)
            throw new InvalidOperationException(
                "The model asked for a tool call and the chat client pipeline did not run it. Add UseFunctionInvocation.");

        await run.FinishAsync(cancellationToken).ConfigureAwait(false);

        if (_options.KeepConversationOpen)
        {
            // History keeps status messages only, so the reply must go into one for the next turn to see it.
            var reply = run.Reply.Count == 0 ? null : updater.NewAgentMessage(run.Reply.ToArray());
            await updater.UpdateStatusAsync(TaskState.InputRequired, reply, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else if (_options.UseContextMemory && run.Reply.Count > 0)
        {
            // The next task in this context reads status messages from history, so the reply goes into this one. It is stored twice.
            await updater.UpdateStatusAsync(TaskState.Completed, updater.NewAgentMessage(run.Reply.ToArray()),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<List<ChatMessage>> BuildPromptAsync(AgentRequestContext context,
        CancellationToken cancellationToken)
    {
        var source = new List<Message>();
        string? summary = null;
        if (_options.UseContextMemory && context.Conversation is { } memory)
        {
            summary = await memory.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            source.AddRange(await memory
                .ReadEarlierMessagesAsync(_options.ContextMemoryMaxTasks, _options.ContextMemoryMaxBytes,
                    cancellationToken).ConfigureAwait(false));
        }

        if (context.Task?.History is { } history) source.AddRange(history);
        // History normally has the new message already; add it only when it is missing.
        if (source.Count == 0 || source[^1].MessageId != context.Message.MessageId) source.Add(context.Message);

        var skip = Math.Max(0, source.Count - _options.MaxHistoryMessages);
        // Trimming can leave an agent reply first. Start at a user turn; the last message is the user's, so one always remains.
        while (skip < source.Count - 1 && source[skip].Role == Role.Agent) skip++;
        var messages = new List<ChatMessage>(source.Count - skip + 1);
        if (!string.IsNullOrEmpty(_options.Instructions))
            messages.Add(new ChatMessage(ChatRole.System, _options.Instructions));
        // The summary comes from caller text, so it is a user message and never gains system rights. It does not count toward MaxHistoryMessages.
        if (!string.IsNullOrEmpty(summary)) messages.Add(new ChatMessage(ChatRole.User, SummaryPrefix + summary));
        for (var i = skip; i < source.Count; i++)
        {
            var contents = new List<AIContent>();
            foreach (var part in source[i].Parts)
                if (A2AContentExtensions.ToModelContent(part, _options.AllowUrlParts) is { } content)
                    contents.Add(content);
            if (contents.Count == 0) continue;
            messages.Add(new ChatMessage(source[i].Role == Role.Agent ? ChatRole.Assistant : ChatRole.User, contents));
        }

        return messages;
    }

    /// <summary>Collects output and emits it as artifact chunks. One chunk is always held back so the last one can say so.</summary>
    private sealed class Run(ChatClientAgentOptions options, TaskUpdater updater)
    {
        private readonly string _artifactId = Guid.NewGuid().ToString();
        private readonly List<Part> _pending = [];
        private readonly StringBuilder _text = new();
        private bool _first = true;
        private bool _pendingNonText;

        public List<Part> Reply { get; } = [];
        public bool HasOutput { get; private set; }
        public bool HasUnresolvedToolCall { get; private set; }

        public async ValueTask AddAsync(AIContent content, CancellationToken ct)
        {
            switch (content)
            {
                case ErrorContent:
                    throw new InvalidOperationException("The model reported an error.");
                case FunctionCallContent { InformationalOnly: false }:
                    HasUnresolvedToolCall = true;
                    return;
                case TextContent text:
                    if (string.IsNullOrEmpty(text.Text)) return;
                    await FlushIfDueAsync(ct).ConfigureAwait(false);
                    _text.Append(text.Text);
                    HasOutput = true;
                    return;
                case DataContent or UriContent:
                    if (A2AContentExtensions.ConvertToPart(content, false) is not { } part) return;
                    await FlushIfDueAsync(ct).ConfigureAwait(false);
                    MoveTextToPending();
                    _pending.Add(part);
                    _pendingNonText = true;
                    HasOutput = true;
                    return;
            }
        }

        public ValueTask FinishAsync(CancellationToken ct)
        {
            return EmitAsync(true, ct);
        }

        private ValueTask FlushIfDueAsync(CancellationToken ct)
        {
            return _pendingNonText || _text.Length >= options.StreamChunkSize
                ? EmitAsync(false, ct)
                : ValueTask.CompletedTask;
        }

        private void MoveTextToPending()
        {
            if (_text.Length == 0) return;
            _pending.Add(new TextPart(_text.ToString()));
            _text.Clear();
        }

        private async ValueTask EmitAsync(bool lastChunk, CancellationToken ct)
        {
            MoveTextToPending();
            if (_pending.Count == 0) return;
            // A new list for every event: the server owns what we pass in.
            var parts = new List<Part>(_pending);
            _pending.Clear();
            _pendingNonText = false;
            Reply.AddRange(parts);
            var append = !_first;
            _first = false;
            await updater.AddArtifactAsync(parts, _artifactId, append ? null : options.ArtifactName, append: append,
                lastChunk: lastChunk, cancellationToken: ct).ConfigureAwait(false);
        }
    }
}