using A2A.Extensions.AI.Shared;
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
///         <see cref="A2A.Extensions.AI.A2AModelAgentOptions.UseContextMemory" /> to give the model the earlier tasks of the context, or
///         use
///         <see cref="A2A.Extensions.AI.A2AModelAgentOptions.KeepConversationOpen" /> to continue one task.
///     </para>
/// </remarks>
public sealed class ChatClientAgentExecutor : IAgentExecutor
{
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
            await updater.RejectAsync(ExecutorTexts.UrlRejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = await BuildPromptAsync(context, cancellationToken).ConfigureAwait(false);
        var chatOptions = _options.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.ConversationId = null;

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var run = new ArtifactChunker(_options.ArtifactName, _options.StreamChunkSize, updater);
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason == ChatFinishReason.ContentFilter)
                throw new InvalidOperationException(ExecutorTexts.ContentFilter);
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

    private ValueTask<List<ChatMessage>> BuildPromptAsync(AgentRequestContext context,
        CancellationToken cancellationToken)
    {
        return HistoryPrompt.BuildAsync(context, new HistoryPromptOptions(_options.Instructions,
            _options.MaxHistoryMessages, _options.AllowUrlParts, _options.UseContextMemory,
            _options.ContextMemoryMaxTasks, _options.ContextMemoryMaxBytes), cancellationToken);
    }
}
