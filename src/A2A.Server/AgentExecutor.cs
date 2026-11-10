using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Delegation;
using A2A.Elicitations;
using A2A.Integrity;
using A2A.Server.Authorization;
using A2A.Server.Execution;
using A2A.Server.Storage;
using A2A.Skills;

namespace A2A.Server;

/// <summary>
///     Your agent logic. Called once per incoming message; report progress through the <see cref="TaskUpdater" />.
///     Resolved from a fresh DI scope per execution, so scoped dependencies (e.g. a DbContext) are safe.
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    ///     Handle a message. Either call <see cref="TaskUpdater.ReplyAsync(Message, CancellationToken)" /> once for a simple
    ///     stateless reply, or drive a task with status and artifact updates. Return when you have nothing more to report
    ///     (usually after a terminal or input-required state). <paramref name="cancellationToken" /> fires on CancelTask or
    ///     shutdown.
    /// </summary>
    Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken);

    /// <summary>
    ///     Called on CancelTask. Default: mark the task canceled. <paramref name="task" /> is a copy made for this execution:
    ///     changing it does not change the stored task.
    /// </summary>
    Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken)
    {
        return updater.CancelAsync(cancellationToken: cancellationToken).AsTask();
    }
}

/// <summary>Everything the executor knows about the incoming message.</summary>
public sealed class AgentRequestContext
{
    /// <summary>The message the client sent.</summary>
    public required Message Message { get; init; }

    /// <summary>The task id: existing, or pre-assigned for a new task (only materialized if you emit a task event).</summary>
    public required string TaskId { get; init; }

    /// <summary>The context (conversation) id: the client's, or one the server assigned.</summary>
    public required string ContextId { get; init; }

    /// <summary>
    ///     The existing task (including this message in its history), or null for a new conversation turn. A copy made for
    ///     this
    ///     execution: changing it does not change the stored task.
    /// </summary>
    public AgentTask? Task { get; init; }

    /// <summary>
    ///     The memory of this conversation: messages of earlier tasks in the same context, and a rolling summary. Set by the
    ///     server
    ///     for the caller's tenant and principal. Null only for a context built by hand.
    /// </summary>
    public ConversationMemory? Conversation { get; init; }

    /// <summary>Options from the SendMessage request. Null when the client sent none.</summary>
    public SendMessageConfiguration? Configuration { get; init; }

    /// <summary>The <c>metadata</c> of the SendMessage request. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Details of the call: caller, tenant, extensions.</summary>
    public required ServerCallContext Call { get; init; }

    /// <summary>True when no task exists yet for this message.</summary>
    public bool IsNewTask => Task is null;

    /// <summary>
    ///     For a follow-up to a task waiting for an approval (<see cref="TaskUpdater.RequireApprovalAsync" />): whether the
    ///     message
    ///     carries the pending approval's hash. Act on the approved action only when this is
    ///     <see cref="ApprovalState.Approved" />.
    /// </summary>
    public ApprovalState Approval { get; init; }

    /// <summary>
    ///     A2A 1.1: the artifacts the client wrote and sent with this message (
    ///     <see cref="SendMessageRequest.ArtifactUpdates" />), in
    ///     the order they were applied, each with <see cref="Artifact.Role" /> user. They are already stored on the task (for
    ///     a new task,
    ///     once it is created). Null when the client sent none. Do not change them: the stored task shares them.
    /// </summary>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public IReadOnlyList<Artifact>? UserArtifacts => UserArtifactUpdates?.Select(u => u.Artifact).ToList();

    /// <summary>
    ///     A2A 1.1: the client input that arrives while this execution runs. Read it to take messages while you work (a live
    ///     exchange, or any SendMessage for the task); until you do, a message for a working task is refused as usual. Null
    ///     for a
    ///     context built by hand.
    /// </summary>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public ILiveInput? LiveInput { get; init; }

    /// <summary>The checked artifact updates behind <see cref="UserArtifacts" />; the engine stores them for a new task.</summary>
    internal IReadOnlyList<TaskArtifactUpdateEvent>? UserArtifactUpdates { get; init; }

    /// <summary>Concatenated text of the incoming message.</summary>
    public string UserText => Message.GetText();

    /// <summary>
    ///     The skill the client selected (<c>Message.metadata["skillId"]</c>). Null when the caller did not activate the
    ///     skill-selection extension, or sent no usable value. To read the value without that check, use
    ///     <c>Message.GetRequestedSkillId()</c>.
    /// </summary>
    /// <remarks>
    ///     This is a hint from the caller. Unless the card opts in with <see cref="SkillSelection.CreateExtension" /> (
    ///     <c>rejectUnknownSkills: true</c>), it may name a skill
    ///     the agent does not have. Do not use it for authorization: branch on <see cref="AuthorizedSkillId" /> instead.
    /// </remarks>
    [Experimental("A2AEXP001",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public string? RequestedSkillId =>
        Call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) ? Message.GetRequestedSkillId() : null;

    /// <summary>
    ///     The skill id the server checked and allowed for this message (<see cref="A2AServerOptions.SkillAuthorization" />).
    ///     Null when
    ///     skill authorization is off, or the message named no skill. Unlike <see cref="RequestedSkillId" /> this is an
    ///     authorization
    ///     result, and it is the one to branch on. A later change to the message cannot alter it.
    /// </summary>
    public string? AuthorizedSkillId => Call.AuthorizedSkillId;

    /// <summary>Set by the server: answers <see cref="IsSkillAllowedAsync" />. Null for a context built by hand.</summary>
    internal SkillAuthorizer? SkillAuthorizer { get; init; }

    /// <summary>
    ///     True when a form passed to
    ///     <see cref="TaskUpdater.RequireInputAsync(AgentRequestContext, ElicitationForm, string, CancellationToken)" />
    ///     reaches the client: the caller activated the elicitation extension and the agent card declares it. When false, the
    ///     client
    ///     gets the text question only.
    /// </summary>
    public bool CanElicit => Call.ActivatedExtensions.Contains(Elicitation.ExtensionUri);

    /// <summary>
    ///     The answer to a form (
    ///     <see cref="TaskUpdater.RequireInputAsync(AgentRequestContext, ElicitationForm, string, CancellationToken)" />),
    ///     already checked against that form. Null when the message is not an answer, including a plain text reply to a form,
    ///     or when
    ///     the caller did not activate the extension. Decline and cancel are answers too: check
    ///     <see cref="ElicitationAnswer.Action" />.
    /// </summary>
    /// <remarks>The values are what the user typed. Treat them as untrusted input, and do not use them for authorization.</remarks>
    public ElicitationAnswer? ElicitationAnswer { get; init; }

    /// <summary>Reads an inbound upload; set by the server when uploads are enabled. Null otherwise.</summary>
    internal Func<UrlPart, ArtifactByteRange?, CancellationToken, ValueTask<ArtifactObjectContent?>>? UploadOpener
    {
        get;
        init;
    }

    /// <summary>The registered token exchanger, set by the server. Null when none is registered.</summary>
    internal ITokenExchanger? TokenExchanger { get; init; }

    /// <summary>
    ///     True when <see cref="ExchangeTokenAsync" /> can run: a token exchanger is registered and the caller sent a bearer
    ///     token.
    /// </summary>
    public bool CanExchangeToken => TokenExchanger is not null && !string.IsNullOrEmpty(Call.InboundBearerToken);

    /// <summary>
    ///     Asks whether the caller may use the skill <paramref name="skillId" />: the same decision the server makes for a
    ///     message that
    ///     names it (the skill must be on the agent's card, then <see cref="A2AAuthorizationHandler.AuthorizeSkillAsync" />
    ///     must allow it).
    ///     Works in any <see cref="SkillAuthorizationMode" />. An agent that routes by itself calls this before it runs a
    ///     skill.
    ///     False for an unknown skill, and when the context was built by hand.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="skillId" /> is null or empty.</exception>
    public ValueTask<bool> IsSkillAllowedAsync(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        return SkillAuthorizer is { } authorizer
            ? authorizer.IsAllowedAsync(Call, skillId, cancellationToken)
            : ValueTask.FromResult(false);
    }

    /// <summary>
    ///     Opens the file a client uploaded and then sent as <paramref name="part" /> (<c>MapA2AUploads</c> in
    ///     A2A.Server.AspNetCore). Returns
    ///     null when the part is not the link of one of the caller's own uploads: an external URL, a file of another owner or
    ///     tenant, a
    ///     missing file, or when inbound uploads are not enabled. Another owner's file is never returned, whatever the message
    ///     said. The
    ///     call was authorized as SendMessage, so the read needs no authorization of its own. The caller disposes the result;
    ///     the bytes stream from the object store and are never buffered.
    /// </summary>
    /// <param name="part">A <c>url</c> part of the incoming message.</param>
    /// <param name="range">The bytes to read, or null for all.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="A2AException">The caller is not authenticated (Unauthenticated).</exception>
    public ValueTask<ArtifactObjectContent?> OpenUploadAsync(UrlPart part, ArtifactByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        return UploadOpener is { } open
            ? open(part, range, cancellationToken)
            : ValueTask.FromResult<ArtifactObjectContent?>(null);
    }

    /// <summary>
    ///     Exchanges the caller's token for a new token for another agent (RFC 8693). The caller's own token is never given to
    ///     agent code: use the result with <c>A2ARequestOptions.UseBearerToken</c> on the next call.
    /// </summary>
    /// <param name="audience">Logical name of the target service. Not empty.</param>
    /// <param name="scopes">Scopes wanted for the new token, or null.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="ArgumentException"><paramref name="audience" /> is null or empty.</exception>
    /// <exception cref="InvalidOperationException"><see cref="CanExchangeToken" /> is false.</exception>
    /// <exception cref="TokenExchangeException">The exchange failed.</exception>
    public ValueTask<TokenExchangeResult> ExchangeTokenAsync(string audience, IReadOnlyList<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(audience);
        if (TokenExchanger is not { } exchanger || string.IsNullOrEmpty(Call.InboundBearerToken))
            throw new InvalidOperationException(
                "No token exchange is possible: register an ITokenExchanger and call with a bearer token.");
        return exchanger.ExchangeAsync(new TokenExchangeRequest
        {
            SubjectToken = Call.InboundBearerToken,
            Audience = audience,
            Scopes = scopes
        }, cancellationToken);
    }
}

/// <summary>
///     Emits task events. Events are applied to the store in order, then streamed to subscribers and sent to webhooks.
///     After a terminal state, further task events are rejected.
/// </summary>
/// <remarks>
///     Once emitted, an event belongs to the server: the stored task, the event log, streams and webhooks use the
///     instances you
///     pass, without copying them. Do not change a <see cref="Message" />, <see cref="Artifact" />, part list or metadata
///     dictionary after you passed it to a method of this class (and do not dispose the <see cref="JsonDocument" /> behind
///     a
///     <see cref="JsonElement" /> in it). Build a new instance for the next event instead. The <c>metadata</c>
///     dictionaries passed
///     to <see cref="UpdateStatusAsync" /> and
///     <see
///         cref="AddArtifactAsync(IEnumerable{Part}, string?, string?, string?, bool, bool, Dictionary{string, JsonElement}?, CancellationToken)" />
///     are copied, so those may be reused.
/// </remarks>
public sealed class TaskUpdater
{
    private readonly object _gate = new();
    private readonly bool _isNewTask;
    private readonly ArtifactOffload? _offload;
    private readonly Func<ValueTask>? _onBudgetExceeded;
    private readonly TaskUsageMeter _usage;
    private readonly ChannelWriter<StreamResponse> _writer;
    private Dictionary<string, ArtifactState>? _artifacts; // by artifact id; under _gate
    private bool _replied;
    private bool _taskEmitted;
    private bool _terminal;

    internal TaskUpdater(string taskId, string contextId, bool isNewTask, ChannelWriter<StreamResponse> writer,
        Dictionary<string, JsonElement>? requestMetadata = null, ArtifactOffload? offload = null,
        TaskUsageMeter? usage = null,
        Func<ValueTask>? onBudgetExceeded = null)
    {
        _offload = offload;
        _usage = usage ?? new TaskUsageMeter();
        _onBudgetExceeded = onBudgetExceeded;
        TaskId = taskId;
        ContextId = contextId;
        _isNewTask = isNewTask;
        _writer = writer;
        RequestMetadata = requestMetadata;
    }

    /// <summary>Id of the task this updater writes to.</summary>
    public string TaskId { get; }

    /// <summary>Id of the context the task belongs to.</summary>
    public string ContextId { get; }

    /// <summary>
    ///     The <c>metadata</c> of the request that started this run: the SendMessage metadata for
    ///     <see cref="IAgentExecutor.ExecuteAsync" />, the CancelTask metadata for <see cref="IAgentExecutor.CancelAsync" />,
    ///     the
    ///     metadata passed to <c>ContinueTaskAsync</c> for <see cref="IContinuableAgentExecutor.ContinueAsync" />.
    ///     Null when the request had none.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? RequestMetadata { get; }

    /// <summary>
    ///     The task's total tokens, over all its runs: what earlier runs stored when this run started, plus what this run
    ///     reported
    ///     with <see cref="ReportUsageAsync" />.
    /// </summary>
    public long UsedTokens => _usage.Tokens;

    /// <summary>The task's total cost units, over all its runs. Counted like <see cref="UsedTokens" />.</summary>
    public decimal UsedCostUnits => _usage.CostUnits;

    /// <summary>True once a terminal state was emitted. Further task events throw.</summary>
    public bool IsTerminal
    {
        get
        {
            lock (_gate)
            {
                return _terminal;
            }
        }
    }

    /// <summary>True once a direct message reply was sent.</summary>
    public bool HasReplied
    {
        get
        {
            lock (_gate)
            {
                return _replied;
            }
        }
    }

    internal bool HasTask
    {
        get
        {
            lock (_gate)
            {
                return _taskEmitted || !_isNewTask;
            }
        }
    }

    /// <summary>
    ///     Reports what the agent has used for this task. The server adds it to the task's total, stores it in the task's
    ///     metadata
    ///     (<c>a2a.usage</c>) with the next event of this run, and checks it against <see cref="A2ALimits.MaxTaskTokens" />
    ///     and
    ///     <see cref="A2ALimits.MaxTaskCostUnits" />. A report that takes the total strictly past a budget fails the task
    ///     ("Task exceeded its budget"), cancels the token of the run, and throws <see cref="OperationCanceledException" />,
    ///     so an agent
    ///     that ignores its token still stops. Usage exactly at the budget is allowed.
    /// </summary>
    /// <remarks>
    ///     <para>Sums saturate at the largest value; they never wrap. A report after a terminal state is ignored.</para>
    ///     <para>
    ///         Report before you set the status: usage reported after the run's last event is not stored. The check uses this
    ///         run's view
    ///         (the stored total at the start plus its own reports), and happens after the spending, so one report can
    ///         overshoot by its own size.
    ///         A CancelTask run counts usage and has no budget. When two runs of one task overlap and one ends the task, an
    ///         event of the other
    ///         that is not stored yet is dropped, with the usage it carried.
    ///     </para>
    /// </remarks>
    /// <param name="tokens">Tokens used since the last report. Not negative.</param>
    /// <param name="costUnits">Cost units used since the last report, in any unit you choose. Not negative.</param>
    /// <param name="cancellationToken">Cancels the report before it counts.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens" /> or <paramref name="costUnits" /> is negative.</exception>
    /// <exception cref="OperationCanceledException">This report took the task past its budget, or an earlier one did.</exception>
    public async ValueTask ReportUsageAsync(long tokens, decimal costUnits = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ArgumentOutOfRangeException.ThrowIfNegative(costUnits);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsTerminal) return;
        var outcome = _usage.Report(tokens, costUnits);
        if (outcome == UsageOutcome.Within) return;
        if (outcome == UsageOutcome.Crossed && _onBudgetExceeded is not null)
            await _onBudgetExceeded().ConfigureAwait(false);
        throw new OperationCanceledException(TaskExecutionEngine.BudgetExceededText);
    }

    /// <summary>
    ///     Learns the usage earlier runs stored, from the task this run started with. Called by the engine before the
    ///     executor runs.
    /// </summary>
    internal void SeedUsage(Dictionary<string, JsonElement>? taskMetadata)
    {
        _usage.Seed(taskMetadata);
    }

    /// <summary>Replies with a message and no task. Only allowed for a new conversation turn before any task event.</summary>
    public ValueTask ReplyAsync(Message message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_isNewTask || _taskEmitted)
                throw new InvalidOperationException(
                    "Direct replies are only allowed before a task exists. Use status updates instead.");
            if (_replied) throw new InvalidOperationException("A direct reply was already sent.");
            _replied = true;
        }

        message.Role = Role.Agent;
        message.ContextId ??= ContextId;
        message.TaskId = null;
        if (string.IsNullOrEmpty(message.MessageId)) message.MessageId = Message.NewId();
        return _writer.WriteAsync(StreamResponse.Of(message), cancellationToken);
    }

    /// <summary>
    ///     Replies with a direct agent message that has one text part. Same rules as
    ///     <see cref="ReplyAsync(Message, CancellationToken)" />.
    /// </summary>
    public ValueTask ReplyAsync(string text, CancellationToken cancellationToken = default)
    {
        return ReplyAsync(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] },
            cancellationToken);
    }

    /// <summary>Moves the task to Submitted.</summary>
    public ValueTask SubmitAsync(CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Submitted, null, null, cancellationToken);
    }

    /// <summary>Moves the task to Working, with an optional agent message.</summary>
    public ValueTask StartWorkAsync(string? text = null, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Working, text is null ? null : NewAgentMessage(text), null,
            cancellationToken);
    }

    /// <summary>Moves the task to Completed (terminal), with an optional agent message.</summary>
    public ValueTask CompleteAsync(string? text = null, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Completed, text is null ? null : NewAgentMessage(text), null,
            cancellationToken);
    }

    /// <summary>Moves the task to Failed (terminal), with an optional agent message.</summary>
    public ValueTask FailAsync(string? text = null, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Failed, text is null ? null : NewAgentMessage(text), null,
            cancellationToken);
    }

    /// <summary>Moves the task to Rejected (terminal), with an optional agent message.</summary>
    public ValueTask RejectAsync(string? text = null, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Rejected, text is null ? null : NewAgentMessage(text), null,
            cancellationToken);
    }

    /// <summary>Moves the task to Canceled (terminal), with an optional agent message.</summary>
    public ValueTask CancelAsync(string? text = null, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.Canceled, text is null ? null : NewAgentMessage(text), null,
            cancellationToken);
    }

    /// <summary>Asks the client for more input. The client continues by sending a message with this task id.</summary>
    public ValueTask RequireInputAsync(string text, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.InputRequired, NewAgentMessage(text), null, cancellationToken);
    }

    /// <summary>
    ///     Asks the client for more input, with a typed form. The form goes in the metadata of the status message only when
    ///     <see cref="AgentRequestContext.CanElicit" /> is true; otherwise the client sees <paramref name="text" /> and
    ///     nothing else. A
    ///     client that cannot read the form can always answer in words, so <paramref name="text" /> must say what you need.
    ///     The answer
    ///     arrives as <see cref="AgentRequestContext.ElicitationAnswer" />.
    /// </summary>
    /// <param name="context">The context of the request that runs now. Activation is per call.</param>
    /// <param name="form">The form to ask.</param>
    /// <param name="text">The question in words. Not empty.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="text" /> is empty.</exception>
    public ValueTask RequireInputAsync(AgentRequestContext context, ElicitationForm form, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var message = NewAgentMessage(text);
        if (context.CanElicit) message.SetElicitationForm(form);
        return UpdateStatusAsync(TaskState.InputRequired, message, null, cancellationToken);
    }

    /// <summary>
    ///     Asks the client to obtain authorization (spec §7.6). Streams stay open while you wait for out-of-band
    ///     credentials.
    /// </summary>
    public ValueTask RequireAuthAsync(string text, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(TaskState.AuthRequired, NewAgentMessage(text), null, cancellationToken);
    }

    /// <summary>
    ///     Asks the client to confirm one exact action. The status message carries the approval's hash in its metadata
    ///     (<see cref="A2AProtocol.ApprovalHashKey" />); a client that approves echoes it in the follow-up message's metadata
    ///     under the
    ///     same key, and the executor then sees <see cref="AgentRequestContext.Approval" /> =
    ///     <see cref="ApprovalState.Approved" />.
    ///     Call it again when the follow-up is <see cref="ApprovalState.NotApproved" />.
    /// </summary>
    public ValueTask RequireApprovalAsync(PendingApproval approval, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var message = NewAgentMessage(text);
        using var hash = JsonDocument.Parse($"\"{approval.Hash}\""); // lowercase hex: nothing to escape
        message.Metadata = new Dictionary<string, JsonElement>
            { [A2AProtocol.ApprovalHashKey] = hash.RootElement.Clone() };
        return UpdateStatusAsync(TaskState.InputRequired, message, null, cancellationToken);
    }

    /// <summary>
    ///     Moves the task to <paramref name="state" />, with an optional agent message and metadata. Throws when
    ///     <paramref name="state" /> is Unspecified or the task is already terminal.
    /// </summary>
    public ValueTask UpdateStatusAsync(TaskState state, Message? message = null,
        Dictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (state == TaskState.Unspecified) throw new ArgumentException("State must be specified.", nameof(state));
        MarkTaskEvent(state.IsTerminal());
        if (message is not null) Stamp(message);
        var evt = new TaskStatusUpdateEvent
        {
            TaskId = TaskId,
            ContextId = ContextId,
            Status = new AgentTaskStatus { State = state, Message = message, Timestamp = DateTimeOffset.UtcNow },
            Metadata = metadata is null
                ? null
                : new Dictionary<string, JsonElement>(
                    metadata) // the stored event keeps its own: the caller may reuse the dictionary
        };
        return _writer.WriteAsync(StreamResponse.Of(evt), cancellationToken);
    }

    /// <summary>
    ///     Adds an artifact, or appends parts to one (<paramref name="append" />). Returns the artifact id.
    ///     <paramref name="lastChunk" /> seals the artifact. See
    ///     <see cref="AddArtifactAsync(Artifact, bool, bool, CancellationToken)" /> for the rules.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The artifact is sealed and <paramref name="append" /> is true, or the
    ///     artifact is being hashed (<see cref="AddHashedArtifactAsync" />) and <paramref name="append" /> is true.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="parts" /> is empty and the event is not an append to an artifact
    ///     that exists.
    /// </exception>
    public async ValueTask<string> AddArtifactAsync(IEnumerable<Part> parts, string? artifactId = null,
        string? name = null,
        string? description = null, bool append = false, bool lastChunk = false,
        Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = new Artifact
        {
            ArtifactId = artifactId ?? Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            Parts = parts.ToList(),
            Metadata = metadata is null
                ? null
                : new Dictionary<string, JsonElement>(
                    metadata) // the stored artifact keeps its own: the caller may reuse the dictionary
        };
        await AddArtifactAsync(artifact, append, lastChunk, cancellationToken).ConfigureAwait(false);
        return artifact.ArtifactId;
    }

    /// <summary>
    ///     Adds <paramref name="artifact" />, or appends its parts to the artifact with the same id when
    ///     <paramref name="append" /> is true.
    ///     <paramref name="lastChunk" /> marks the final chunk and seals the artifact. Both default to false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without <paramref name="append" />, the artifact is created, or replaced when it exists, also a sealed one.
    ///         With
    ///         <paramref name="append" />, parts are added to an open artifact. An append to an artifact that does not exist
    ///         creates it.
    ///         An append to a sealed artifact throws: send it with <c>append: false</c> to replace it, or use a new artifact
    ///         id.
    ///     </para>
    ///     <para>
    ///         An empty <see cref="Artifact.Parts" /> list is allowed only for an append to an artifact that exists. It seals
    ///         the artifact
    ///         or updates its metadata. A peer that follows the spec text strictly may reject such an event: send the last
    ///         parts with
    ///         <paramref name="lastChunk" /> when you can.
    ///     </para>
    ///     <para>
    ///         The updater knows the artifacts this run sent and those earlier runs stored (the task the run started with).
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The artifact is sealed and <paramref name="append" /> is true, or the
    ///     artifact is being hashed (<see cref="AddHashedArtifactAsync" />) and <paramref name="append" /> is true.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="artifact" /> has no parts and the event is not an append to an
    ///     artifact that exists.
    /// </exception>
    public ValueTask AddArtifactAsync(Artifact artifact, bool append = false, bool lastChunk = false,
        CancellationToken cancellationToken = default)
    {
        return AddArtifactAsync(artifact, null, append, lastChunk, cancellationToken);
    }

    /// <summary>
    ///     Like <see cref="AddArtifactAsync(Artifact, bool, bool, CancellationToken)" />, with metadata for the event itself.
    ///     <paramref name="eventMetadata" /> is copied, and merges into the task's metadata: shallow, last write wins (the
    ///     same rule
    ///     as the metadata of a status event). It is not the artifact's own <see cref="Artifact.Metadata" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The artifact is sealed and <paramref name="append" /> is true, or the
    ///     artifact is being hashed (<see cref="AddHashedArtifactAsync" />) and <paramref name="append" /> is true.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="artifact" /> has no parts and the event is not an append to an
    ///     artifact that exists.
    /// </exception>
    public async ValueTask AddArtifactAsync(Artifact artifact, Dictionary<string, JsonElement>? eventMetadata,
        bool append = false, bool lastChunk = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await OffloadAsync(artifact, append, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            CheckCanEmit();
            Track(artifact, append, lastChunk, false);
            MarkTaskEvent(false); // last: a rejected call must not count as an emitted task event
        }

        await WriteArtifact(artifact, append, lastChunk, cancellationToken, eventMetadata).ConfigureAwait(false);
    }

    /// <summary>
    ///     Adds an artifact like
    ///     <see
    ///         cref="AddArtifactAsync(IEnumerable{Part}, string?, string?, string?, bool, bool, Dictionary{string, JsonElement}?, CancellationToken)" />
    ///     ,
    ///     and sets <c>metadata["sha256"]</c> to the hash of the artifact so far: every part from the first chunk up to and
    ///     including this one
    ///     (<see cref="ArtifactHash" />). The value on the last chunk is the hash of the final artifact. Returns the artifact
    ///     id.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It has the same rules as <see cref="AddArtifactAsync(Artifact, bool, bool, CancellationToken)" />, and
    ///         overwrites any <c>sha256</c> in
    ///         <paramref name="metadata" />. The hash continues from chunk to chunk without keeping the content. For an append
    ///         to an artifact an
    ///         earlier run stored, it first hashes the stored parts. Hash state is released when the artifact is sealed and
    ///         when the agent returns.
    ///     </para>
    ///     <para>
    ///         An append works only on an artifact this updater is hashing, one from an earlier run, or a new one. After a
    ///         plain
    ///         <see cref="AddArtifactAsync(Artifact, bool, bool, CancellationToken)" /> in this run, the earlier content is
    ///         not known, and the call throws.
    ///         A plain append to an artifact this updater is hashing throws too, because it would make the hash wrong.
    ///     </para>
    ///     <para>
    ///         This detects corruption and bad reassembly. It is not authenticity: anyone who can change the content can
    ///         change an unsigned hash.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The artifact is sealed and <paramref name="append" /> is true; the earlier
    ///     content of the artifact is not known; or a data part cannot be put in canonical JSON form.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="parts" /> is empty and the event is not an append to an artifact
    ///     that exists, or a part is null.
    /// </exception>
    [Experimental("A2AEXP003",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public async ValueTask<string> AddHashedArtifactAsync(IEnumerable<Part> parts, string? artifactId = null,
        string? name = null,
        string? description = null, bool append = false, bool lastChunk = false,
        Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var artifact = new Artifact
        {
            ArtifactId = artifactId ?? Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            Parts = parts.ToList(),
            Metadata = metadata is null
                ? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement>(metadata)
        };
        await OffloadAsync(artifact, append, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            CheckCanEmit();
            Track(artifact, append, lastChunk, true);
            MarkTaskEvent(false);
        }

        await WriteArtifact(artifact, append, lastChunk, cancellationToken).ConfigureAwait(false);
        return artifact.ArtifactId;
    }

    /// <summary>
    ///     Uploads the content of <paramref name="content" /> to the artifact object store and adds a sealed artifact with one
    ///     URL part
    ///     that points to it, without passing the bytes through an event, so <see cref="A2ALimits.MaxEventBytes" /> does not
    ///     apply.
    ///     The part carries <c>sha256</c> (of the bytes) and <c>sizeBytes</c> in its metadata, and the artifact carries the
    ///     hash of
    ///     its delivered parts (<see cref="AddHashedArtifactAsync" />). Returns the artifact id.
    /// </summary>
    /// <remarks>
    ///     The upload comes first and the event after it. When the event is dropped later (the task ended, the artifact was
    ///     sealed),
    ///     the object stays until the retention sweep removes it. A failed upload throws and emits nothing.
    /// </remarks>
    /// <param name="content">The bytes to store. Read once to the end; not disposed.</param>
    /// <param name="mediaType">The media type of the file.</param>
    /// <param name="artifactId">The artifact id. A new one when null.</param>
    /// <param name="name">Human-readable artifact name, also used as the file name of the part.</param>
    /// <param name="description">What the artifact is.</param>
    /// <param name="metadata">Artifact metadata. Copied.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="InvalidOperationException">
    ///     No <see cref="ArtifactObjectStore" /> is registered, or the task is already
    ///     terminal.
    /// </exception>
    /// <exception cref="ArtifactObjectTooLargeException">
    ///     The stream is longer than
    ///     <see cref="ArtifactStorageOptions.MaxObjectBytes" />.
    /// </exception>
    public async ValueTask<string> AddArtifactFromStreamAsync(Stream content, string mediaType,
        string? artifactId = null, string? name = null,
        string? description = null, Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var offload = _offload ?? throw new InvalidOperationException(
            "AddArtifactFromStreamAsync needs an ArtifactObjectStore. Register one (services.AddSingleton<ArtifactObjectStore>(...)) and set A2AServerOptions.ArtifactStorage.PublicBaseUrl.");
        lock (_gate)
        {
            CheckCanEmit(); // no upload for an event that cannot be sent
        }

        var part = await offload.UploadAsync(content, mediaType, name, null, cancellationToken).ConfigureAwait(false);
        return await AddHashedArtifactAsync([part], artifactId, name, description, false, true, metadata,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Before the artifact is tracked and hashed: checks the task can take an event, then (outside any lock) replaces the
    ///     large raw
    ///     parts of a new artifact with URL parts. Appended chunks stay inline.
    /// </summary>
    private async ValueTask OffloadAsync(Artifact artifact, bool append, CancellationToken cancellationToken)
    {
        if (_offload is null || append || artifact.Parts is not { Count: > 0 } parts) return;
        lock (_gate)
        {
            CheckCanEmit();
        }

        artifact.Parts = await _offload.PrepareAsync(parts, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteArtifact(Artifact artifact, bool append, bool lastChunk, CancellationToken cancellationToken,
        Dictionary<string, JsonElement>? eventMetadata = null)
    {
        var evt = new TaskArtifactUpdateEvent
        {
            TaskId = TaskId, ContextId = ContextId, Artifact = artifact, Append = append, LastChunk = lastChunk,
            Metadata = eventMetadata is null
                ? null
                : new Dictionary<string, JsonElement>(
                    eventMetadata) // the stored event keeps its own: the caller may reuse the dictionary
        };
        return _writer.WriteAsync(StreamResponse.Of(evt), cancellationToken);
    }

    /// <summary>Builds an agent message bound to this task.</summary>
    public Message NewAgentMessage(string text)
    {
        return Stamp(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] });
    }

    /// <summary>Builds an agent message from <paramref name="parts" />, bound to this task.</summary>
    public Message NewAgentMessage(params Part[] parts)
    {
        return Stamp(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = parts.ToList() });
    }

    private Message Stamp(Message m)
    {
        m.Role = Role.Agent;
        m.TaskId = TaskId;
        m.ContextId = ContextId;
        if (string.IsNullOrEmpty(m.MessageId)) m.MessageId = Message.NewId();
        return m;
    }

    /// <summary>
    ///     Learns the artifacts earlier runs left, from the task this run started with. Called by the engine before the
    ///     executor runs.
    /// </summary>
    internal void SeedArtifacts(List<Artifact>? artifacts)
    {
        if (artifacts is not { Count: > 0 }) return;
        lock (_gate)
        {
            _artifacts ??= new Dictionary<string, ArtifactState>();
            foreach (var a in artifacts)
            {
                if (string.IsNullOrEmpty(a.ArtifactId)) continue;
                _artifacts[a.ArtifactId] = ArtifactSeal.IsSealed(a)
                    ? new ArtifactState { Sealed = true }
                    : new ArtifactState { Stored = new List<Part>(a.Parts) };
            }
        }
    }

    /// <summary>Frees the hashes. Called when the agent returned. Seal flags stay, so a late call is still checked.</summary>
    internal void ReleaseHashState()
    {
        lock (_gate)
        {
            if (_artifacts is null) return;
            foreach (var state in _artifacts.Values) state.Release();
        }
    }

    /// <summary>
    ///     Checks an artifact event against what the updater knows, and records its effect (under <c>_gate</c>). Throws before
    ///     anything is queued. For <paramref name="hashing" />, sets <c>metadata["sha256"]</c> of <paramref name="artifact" />
    ///     , which is new.
    /// </summary>
    private void Track(Artifact artifact, bool append, bool lastChunk, bool hashing)
    {
        var id = artifact.ArtifactId;
        var usable = !string.IsNullOrWhiteSpace(id); // the engine rejects an event without an id
        ArtifactState? state = null;
        if (usable) _artifacts?.TryGetValue(id, out state);
        if (artifact.Parts is { Count: 0 } && !(append && state is not null))
            throw new ArgumentException(
                "Artifact parts are empty. Empty parts are allowed only to append to an artifact that exists, to seal it or update its metadata.",
                nameof(artifact));
        if (!usable) return;
        if (append && state is { Sealed: true })
            throw new InvalidOperationException(
                $"Artifact '{id}' is sealed: lastChunk was already sent. Send it with append: false to replace it, or use a new artifact id.");
        if (append && !hashing && state?.Hash is not null)
            throw new InvalidOperationException(
                $"Artifact '{id}' is being hashed: use AddHashedArtifactAsync for every chunk, or send it with append: false to start over.");

        IncrementalHash? hash = null;
        if (hashing)
        {
            if (artifact.Parts is null) throw new ArgumentException("Artifact parts are missing.", nameof(artifact));
            if (append && state is not null)
            {
                if (state.Hash is null && state.Stored is null)
                    throw new InvalidOperationException(
                        $"Artifact '{id}' cannot be hashed: its earlier content was sent without AddHashedArtifactAsync in this run.");
                if (state.Hash is null)
                {
                    var started = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    try
                    {
                        ArtifactHash.Update(started, state.Stored!);
                    }
                    catch
                    {
                        started.Dispose();
                        throw;
                    }

                    state.Hash = started;
                    state.Stored = null;
                }

                hash = state.Hash;
            }
            else
            {
                hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            try
            {
                ArtifactHash.Update(hash, artifact.Parts); // adds nothing when it throws
            }
            catch
            {
                if (!ReferenceEquals(hash, state?.Hash)) hash.Dispose();
                throw;
            }

            (artifact.Metadata ??= new Dictionary<string, JsonElement>())[ArtifactHash.MetadataKey] =
                HashValue(ArtifactHash.Format(hash.GetCurrentHash()));
        }

        if (append && state is not null)
        {
            // Plain appends make the earlier content unknown to a later hashed append.
            if (!hashing) state.Stored = null;
        }
        else
        {
            state?.Release();
            _artifacts ??= new Dictionary<string, ArtifactState>();
            _artifacts[id] = state = new ArtifactState { Hash = hash };
        }

        if (lastChunk)
        {
            state!.Sealed = true;
            state.Release();
        }
    }

    // A JSON string: the value is "sha256:" and lowercase hex, so there is nothing to escape.
    private static JsonElement HashValue(string value)
    {
        using var doc = JsonDocument.Parse($"\"{value}\"");
        return doc.RootElement.Clone();
    }

    private void CheckCanEmit()
    {
        lock (_gate)
        {
            if (_replied)
                throw new InvalidOperationException(
                    "A direct reply was already sent; no task can be created for this request.");
            if (_terminal) throw new InvalidOperationException("The task is already in a terminal state.");
        }
    }

    private void MarkTaskEvent(bool terminal)
    {
        lock (_gate)
        {
            CheckCanEmit();
            _taskEmitted = true;
            if (terminal) _terminal = true;
        }
    }

    /// <summary>What the updater knows about one artifact. Small on purpose: no content is kept once hashing has started.</summary>
    private sealed class ArtifactState
    {
        /// <summary>The running hash of the artifact so far. Null when the artifact is not being hashed.</summary>
        public IncrementalHash? Hash;

        public bool Sealed;

        /// <summary>The parts an earlier run stored, kept until the first hashed append needs them. Null otherwise.</summary>
        public List<Part>? Stored;

        public void Release()
        {
            Hash?.Dispose();
            Hash = null;
            Stored = null;
        }
    }
}

/// <summary>Adapts a delegate to <see cref="IAgentExecutor" />.</summary>
public sealed class DelegateAgentExecutor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute)
    : IAgentExecutor
{
    /// <summary>Runs the delegate given to the constructor.</summary>
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        return execute(context, updater, cancellationToken);
    }
}