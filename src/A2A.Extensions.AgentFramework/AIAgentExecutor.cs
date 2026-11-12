using System.Diagnostics.CodeAnalysis;
using A2A.Extensions.AgentFramework.Shared;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Runs a Microsoft Agent Framework <see cref="AIAgent" /> as an A2A agent. The reply streams to the caller as chunks
///     of one artifact, exactly as <c>ChatClientAgentExecutor</c> streams a chat client's reply.
/// </summary>
/// <remarks>
///     <para>
///         Every execution is an A2A task, so A2A cancel and follow-ups always work: cancelling the task cancels the token
///         the agent runs with, and the task ends canceled.
///     </para>
///     <para>
///         The agent gets the A2A task id, context id and the message's reference task ids in
///         <see cref="AgentRunOptions.AdditionalProperties" /> (keys in <see cref="A2AAgentKeys" />). Caller metadata is
///         never passed on.
///     </para>
///     <para>
///         A content filter stop, error content, or a tool call or approval request that nothing resolved throws
///         <see cref="InvalidOperationException" />. The server then marks the task failed with a generic message and logs
///         the real error. Reasoning, usage and tool details are never sent to the caller.
///     </para>
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class AIAgentExecutor : IAgentExecutor
{
    private const string UnresolvedText = "The agent asked for a tool call or an approval that nothing resolved.";

    private readonly AIAgent _agent;
    private readonly AIAgentExecutorOptions _options;

    /// <summary>
    ///     Creates an executor over <paramref name="agent" />. <paramref name="options" /> defaults to a new
    ///     <see cref="AIAgentExecutorOptions" />.
    /// </summary>
    /// <param name="agent">The agent to run.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <param name="sessions">
    ///     Where sessions are kept in <see cref="AgentSessionMode.Stored" /> mode. Not used in
    ///     <see cref="AgentSessionMode.Stateless" /> mode.
    /// </param>
    /// <exception cref="NotSupportedException">The options choose a mode this version cannot run yet.</exception>
    public AIAgentExecutor(AIAgent agent, AIAgentExecutorOptions? options = null, AgentSessionStore? sessions = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new AIAgentExecutorOptions();
        _options.ThrowIfUnsupported();
        _ = sessions; // AgentSessionMode.Stored uses it.
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

        var messages = await HistoryPrompt.BuildAsync(context, new HistoryPromptOptions(null,
            _options.MaxHistoryMessages, _options.AllowUrlParts, _options.UseContextMemory,
            _options.ContextMemoryMaxTasks, _options.ContextMemoryMaxBytes), cancellationToken).ConfigureAwait(false);
        var session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var runOptions = CreateRunOptions(context);

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var run = new ArtifactChunker(_options.ArtifactName, _options.StreamChunkSize, updater);
        var approvalRequested = false;
        await foreach (var update in _agent.RunStreamingAsync(messages, session, runOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason == ChatFinishReason.ContentFilter)
                throw new InvalidOperationException(ExecutorTexts.ContentFilter);
            foreach (var content in update.Contents)
            {
                // ToolApprovalMode.Off: an approval request is treated like a tool call that nothing ran.
                if (content is ToolApprovalRequestContent)
                {
                    approvalRequested = true;
                    continue;
                }

                await run.AddAsync(content, cancellationToken).ConfigureAwait(false);
            }
        }

        if ((run.HasUnresolvedToolCall || approvalRequested) && !run.HasOutput)
            throw new InvalidOperationException(UnresolvedText);

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
            // The next task in this context reads status messages from history, so the reply goes into this one.
            await updater.UpdateStatusAsync(TaskState.Completed, updater.NewAgentMessage(run.Reply.ToArray()),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     New run options with the A2A ids, then the user's hook. Never caller metadata: the agent may put
    ///     <c>AdditionalProperties</c> in front of a model.
    /// </summary>
    private AgentRunOptions CreateRunOptions(AgentRequestContext context)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            [A2AAgentKeys.TaskId] = context.TaskId,
            [A2AAgentKeys.ContextId] = context.ContextId
        };
        if (context.Message.ReferenceTaskIds is { Count: > 0 } references)
            properties[A2AAgentKeys.ReferenceTaskIds] = references.ToArray();
        var runOptions = new AgentRunOptions { AdditionalProperties = properties };
        _options.ConfigureRunOptions?.Invoke(runOptions, context);
        return runOptions;
    }
}
