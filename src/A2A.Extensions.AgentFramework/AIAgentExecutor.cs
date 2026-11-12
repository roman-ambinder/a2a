using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using A2A.Extensions.AgentFramework.Shared;
using System.Text.Json;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
///     <para>
///         In <see cref="AgentSessionMode.Stored" /> mode the session of each context and caller is loaded from the
///         <see cref="AgentSessionStore" />, only the new message is sent, and the session is saved after the run: before
///         the task's final status when the run succeeds (a failed save then fails the task), and on a best-effort basis
///         after a failure or a cancel. Two tasks of one context that run at the same time race on the session: the last
///         save wins. A pending tool approval is not part of that race: each task's request is a record of its own.
///     </para>
/// </remarks>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class AIAgentExecutor : IAgentExecutor
{
    private const string UnresolvedText = "The agent asked for a tool call or an approval that nothing resolved.";

    /// <summary>How long a session save may take. A store that hangs cannot hold the execution longer.</summary>
    internal static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(5);

    private readonly AIAgent _agent;
    private readonly ILogger _logger;
    private readonly AIAgentExecutorOptions _options;
    private readonly AgentSessionStore? _sessions;

    /// <summary>
    ///     Creates an executor over <paramref name="agent" />. <paramref name="options" /> defaults to a new
    ///     <see cref="AIAgentExecutorOptions" />.
    /// </summary>
    /// <param name="agent">The agent to run.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <param name="sessions">
    ///     Where sessions are kept in <see cref="AgentSessionMode.Stored" /> mode; required there. Not used in
    ///     <see cref="AgentSessionMode.Stateless" /> mode.
    /// </param>
    /// <param name="logger">Logs session saves that failed after a failed or canceled run. Null for none.</param>
    /// <exception cref="NotSupportedException">The options choose a mode this version cannot run yet.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <see cref="AgentSessionMode.Stored" /> without <paramref name="sessions" />.
    /// </exception>
    public AIAgentExecutor(AIAgent agent, AIAgentExecutorOptions? options = null, AgentSessionStore? sessions = null,
        ILogger<AIAgentExecutor>? logger = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? new AIAgentExecutorOptions();
        _options.ThrowIfUnsupported();
        if (_options.SessionMode == AgentSessionMode.Stored && sessions is null)
            throw new InvalidOperationException(StoreMissingText);
        _sessions = sessions;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The error for <see cref="AgentSessionMode.Stored" /> without a store.</summary>
    internal const string StoreMissingText = "SessionMode.Stored needs an AgentSessionStore registered in DI.";

    /// <summary>The reason the agent gets when the caller did not approve.</summary>
    internal const string NotApprovedReason = "The caller did not approve this action.";

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

        if (_sessions is null)
        {
            var messages = await HistoryPrompt.BuildAsync(context, new HistoryPromptOptions(null,
                _options.MaxHistoryMessages, _options.AllowUrlParts, _options.UseContextMemory,
                _options.ContextMemoryMaxTasks, _options.ContextMemoryMaxBytes), cancellationToken).ConfigureAwait(false);
            var session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            await RunAsync(context, updater, messages, session, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = SessionKeys.For(context.Call.Tenant, context.Call.Principal, context.ContextId);
        var stored = await _sessions.GetOrCreateSessionAsync(_agent, key, cancellationToken).ConfigureAwait(false);
        // The session holds the conversation: send only the new message, after the answer to a pending approval.
        var contents = new List<AIContent>();
        // The request is a record of its own (only an existing task can have one); one an earlier version kept in the
        // session is taken out of the session either way, so it is never answered later.
        var inSession = PendingToolApproval.Take(stored, context.TaskId);
        var pending = context.Task is null
            ? null
            : await PendingToolApproval.TakeRecordAsync(_sessions, _agent,
                SessionKeys.ApprovalFor(context.Call.Tenant, context.Call.Principal, context.ContextId, context.TaskId),
                cancellationToken).ConfigureAwait(false);
        if ((pending ?? inSession) is { } found && AnswerFor(found, context) is { } answer)
            contents.Add(answer);
        foreach (var part in context.Message.Parts)
            if (ModelContent.ToModelContent(part, _options.AllowUrlParts) is { } content)
                contents.Add(content);
        List<ChatMessage> newMessage = contents.Count == 0 ? [] : [new ChatMessage(ChatRole.User, contents)];
        var saveAttempted = false;
        try
        {
            await RunAsync(context, updater, newMessage, stored, () =>
            {
                saveAttempted = true;
                return SaveAsync(key, stored, context, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!saveAttempted)
        {
            // A failed or canceled run keeps what the session learned. Best effort: the run's own error is what counts.
            await SaveAfterFailureAsync(key, stored, context).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     The answer to the stored approval request, or null when it is stale: approvals are off, the message does not
    ///     answer an approval (<see cref="ApprovalState.None" />), or the entry belongs to another task or another hash
    ///     than the one this task waits for. A stale entry is dropped, never answered: it may be another action.
    /// </summary>
    private ToolApprovalResponseContent? AnswerFor(PendingToolApproval pending, AgentRequestContext context)
    {
        // ApprovalState.None also fails the hash check below (no pending hash on the task); checked first to say so plainly.
        if (_options.ToolApprovals != ToolApprovalMode.Bound || context.Approval == ApprovalState.None) return null;
        if (!string.Equals(pending.TaskId, context.TaskId, StringComparison.Ordinal)) return null;
        // The hash the server holds for this task (it checked the caller's echo against it for Approved). This catches a
        // store that returns an older copy of the session, whose entry is for an earlier call of the same task.
        if (context.Task?.Status.Message?.Metadata is not { } metadata
            || !metadata.TryGetValue(A2AProtocol.ApprovalHashKey, out var hash) || hash.ValueKind != JsonValueKind.String
            || !string.Equals(hash.GetString(), pending.Hash, StringComparison.Ordinal)) return null;
        return pending.ReadRequest() is { } request
            ? context.Approval == ApprovalState.Approved
                ? request.CreateResponse(true, null)
                : request.CreateResponse(false, NotApprovedReason)
            : null;
    }

    /// <summary>
    ///     Saves within <see cref="SaveTimeout" />, also when the store ignores the token. After a save, and within the
    ///     same time, records the caller in the context's <see cref="SessionIndex" /> (once per process), so deleting the
    ///     context finds this caller's session also when the context is shared. A failure there is logged, not thrown: the
    ///     session itself is saved.
    /// </summary>
    private async Task SaveAsync(AgentSessionStoreKey key, AgentSession session, AgentRequestContext context,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SaveTimeout);
        await _sessions!.SaveSessionAsync(_agent, key, session, timeout.Token).AsTask()
            .WaitAsync(SaveTimeout, cancellationToken).ConfigureAwait(false);
        var left = SaveTimeout - deadline.Elapsed;
        try
        {
            if (left <= TimeSpan.Zero) throw new TimeoutException("No time left for the session index.");
            await SessionIndex.AddAsync(_sessions, _agent, context.Call.Tenant, context.ContextId,
                context.Call.Principal, timeout.Token).AsTask().WaitAsync(left, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AgentFrameworkLog.SessionIndexFailed(_logger, ex, context.ContextId);
        }
    }

    private async Task SaveAfterFailureAsync(AgentSessionStoreKey key, AgentSession session, AgentRequestContext context)
    {
        try
        {
            // Not the execution's token: after a cancel it is cancelled already, and the session is still worth keeping.
            await SaveAsync(key, session, context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentFrameworkLog.SessionSaveFailed(_logger, ex, context.TaskId);
        }
    }

    private async Task RunAsync(AgentRequestContext context, TaskUpdater updater, List<ChatMessage> messages,
        AgentSession session, Func<Task>? saveBeforeEnding, CancellationToken cancellationToken)
    {
        var runOptions = CreateRunOptions(context);

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var run = new ArtifactChunker(_options.ArtifactName, _options.StreamChunkSize, updater);
        var approvalRequested = false;
        ToolApprovalRequestContent? toAsk = null;
        await foreach (var update in _agent.RunStreamingAsync(messages, session, runOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason == ChatFinishReason.ContentFilter)
                throw new InvalidOperationException(ExecutorTexts.ContentFilter);
            foreach (var content in update.Contents)
            {
                if (content is ToolApprovalRequestContent request)
                {
                    if (_options.ToolApprovals == ToolApprovalMode.Bound)
                    {
                        toAsk = request;
                        break;
                    }

                    // ToolApprovalMode.Off: an approval request is treated like a tool call that nothing ran.
                    approvalRequested = true;
                    continue;
                }

                await run.AddAsync(content, cancellationToken).ConfigureAwait(false);
            }

            // Only the first request is asked; leaving the loop stops the run (the enumerator is disposed).
            if (toAsk is not null) break;
        }

        if (toAsk is not null)
        {
            await AskApprovalAsync(context, updater, run, toAsk, saveBeforeEnding!, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if ((run.HasUnresolvedToolCall || approvalRequested) && !run.HasOutput)
            throw new InvalidOperationException(UnresolvedText);

        await run.FinishAsync(cancellationToken).ConfigureAwait(false);
        // Before the final status: a session that could not be saved fails the task instead of losing the turn quietly.
        if (saveBeforeEnding is not null) await saveBeforeEnding().ConfigureAwait(false);

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
    ///     Asks the caller to confirm the exact call: the status text is the canonical description, and the hash binds the
    ///     answer to it (<see cref="TaskUpdater.RequireApprovalAsync" />). The task waits in <c>INPUT_REQUIRED</c>.
    /// </summary>
    private async Task AskApprovalAsync(AgentRequestContext context, TaskUpdater updater, ArtifactChunker run,
        ToolApprovalRequestContent request, Func<Task> save, CancellationToken cancellationToken)
    {
        var description = ApprovalDescription.For(request.ToolCall);
        PendingApproval approval;
        try
        {
            approval = new PendingApproval(request.ToolCall.CallId, description);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The agent asked to approve a tool call whose id cannot be bound.", ex);
        }

        // What the agent said before it asked stays with the task; it is not the end of the reply.
        await run.FlushAsync(cancellationToken).ConfigureAwait(false);
        await save().ConfigureAwait(false);
        // Saved before the status, so a caller cannot answer a request the store does not hold yet.
        await SaveApprovalAsync(context, PendingToolApproval.Create(context.TaskId, approval.Hash, request),
            cancellationToken).ConfigureAwait(false);
        await updater.RequireApprovalAsync(approval, description, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Saves the task's approval request as its own record, within <see cref="SaveTimeout" /> (a failure fails the
    ///     task), then lists it in the context's <see cref="SessionIndex" /> with the time left, so deleting the context
    ///     deletes it. A failure there is logged, not thrown: the request itself is saved.
    /// </summary>
    private async Task SaveApprovalAsync(AgentRequestContext context, PendingToolApproval value,
        CancellationToken cancellationToken)
    {
        var tenant = context.Call.Tenant;
        var caller = context.Call.Principal;
        var key = SessionKeys.ApprovalFor(tenant, caller, context.ContextId, context.TaskId);
        var deadline = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SaveTimeout);
        await PendingToolApproval.SaveRecordAsync(_sessions!, _agent, key, value, timeout.Token).AsTask()
            .WaitAsync(SaveTimeout, cancellationToken).ConfigureAwait(false);
        var left = SaveTimeout - deadline.Elapsed;
        try
        {
            if (left <= TimeSpan.Zero) throw new TimeoutException("No time left for the session index.");
            await SessionIndex.AddApprovalAsync(_sessions!, _agent, tenant, context.ContextId, caller, context.TaskId,
                timeout.Token).AsTask().WaitAsync(left, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AgentFrameworkLog.SessionIndexFailed(_logger, ex, context.ContextId);
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
