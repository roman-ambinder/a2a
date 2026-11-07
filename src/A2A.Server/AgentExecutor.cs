using System.Text.Json;
using System.Threading.Channels;

namespace A2A.Server;

/// <summary>
/// Your agent logic. Called once per incoming message; report progress through the <see cref="TaskUpdater"/>.
/// Resolved from a fresh DI scope per execution, so scoped dependencies (e.g. a DbContext) are safe.
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    /// Handle a message. Either call <see cref="TaskUpdater.ReplyAsync(Message, CancellationToken)"/> once for a simple
    /// stateless reply, or drive a task with status and artifact updates. Return when you have nothing more to report
    /// (usually after a terminal or input-required state). <paramref name="cancellationToken"/> fires on CancelTask or shutdown.
    /// </summary>
    Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken);

    /// <summary>
    /// Called on CancelTask. Default: mark the task canceled. <paramref name="task"/> is a copy made for this execution:
    /// changing it does not change the stored task.
    /// </summary>
    Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CancelAsync(cancellationToken: cancellationToken).AsTask();
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
    /// The existing task (including this message in its history), or null for a new conversation turn. A copy made for this
    /// execution: changing it does not change the stored task.
    /// </summary>
    public AgentTask? Task { get; init; }

    /// <summary>Options from the SendMessage request. Null when the client sent none.</summary>
    public SendMessageConfiguration? Configuration { get; init; }

    /// <summary>The <c>metadata</c> of the SendMessage request. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Details of the call: caller, tenant, extensions.</summary>
    public required ServerCallContext Call { get; init; }

    /// <summary>True when no task exists yet for this message.</summary>
    public bool IsNewTask => Task is null;

    /// <summary>Concatenated text of the incoming message.</summary>
    public string UserText => Message.GetText();
}

/// <summary>
/// Emits task events. Events are applied to the store in order, then streamed to subscribers and sent to webhooks.
/// After a terminal state, further task events are rejected.
/// </summary>
/// <remarks>
/// Once emitted, an event belongs to the server: the stored task, the event log, streams and webhooks use the instances you
/// pass, without copying them. Do not change a <see cref="Message"/>, <see cref="Artifact"/>, part list or metadata
/// dictionary after you passed it to a method of this class (and do not dispose the <see cref="JsonDocument"/> behind a
/// <see cref="JsonElement"/> in it). Build a new instance for the next event instead.
/// </remarks>
public sealed class TaskUpdater
{
    private readonly ChannelWriter<StreamResponse> _writer;
    private readonly bool _isNewTask;
    private readonly object _gate = new();
    private bool _taskEmitted;
    private bool _replied;
    private bool _terminal;

    internal TaskUpdater(string taskId, string contextId, bool isNewTask, ChannelWriter<StreamResponse> writer,
        Dictionary<string, JsonElement>? requestMetadata = null)
    {
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
    /// The <c>metadata</c> of the request that started this run: the SendMessage metadata for
    /// <see cref="IAgentExecutor.ExecuteAsync"/>, the CancelTask metadata for <see cref="IAgentExecutor.CancelAsync"/>.
    /// Null when the request had none.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? RequestMetadata { get; }

    /// <summary>True once a terminal state was emitted. Further task events throw.</summary>
    public bool IsTerminal { get { lock (_gate) return _terminal; } }
    /// <summary>True once a direct message reply was sent.</summary>
    public bool HasReplied { get { lock (_gate) return _replied; } }
    internal bool HasTask { get { lock (_gate) return _taskEmitted || !_isNewTask; } }

    /// <summary>Replies with a message and no task. Only allowed for a new conversation turn before any task event.</summary>
    public ValueTask ReplyAsync(Message message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_isNewTask || _taskEmitted) throw new InvalidOperationException("Direct replies are only allowed before a task exists. Use status updates instead.");
            if (_replied) throw new InvalidOperationException("A direct reply was already sent.");
            _replied = true;
        }
        message.Role = Role.Agent;
        message.ContextId ??= ContextId;
        message.TaskId = null;
        if (string.IsNullOrEmpty(message.MessageId)) message.MessageId = Message.NewId();
        return _writer.WriteAsync(StreamResponse.Of(message), cancellationToken);
    }

    /// <summary>Replies with a direct agent message that has one text part. Same rules as <see cref="ReplyAsync(Message, CancellationToken)"/>.</summary>
    public ValueTask ReplyAsync(string text, CancellationToken cancellationToken = default) =>
        ReplyAsync(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] }, cancellationToken);

    /// <summary>Moves the task to Submitted.</summary>
    public ValueTask SubmitAsync(CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Submitted, null, null, cancellationToken);

    /// <summary>Moves the task to Working, with an optional agent message.</summary>
    public ValueTask StartWorkAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Working, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Moves the task to Completed (terminal), with an optional agent message.</summary>
    public ValueTask CompleteAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Completed, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Moves the task to Failed (terminal), with an optional agent message.</summary>
    public ValueTask FailAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Failed, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Moves the task to Rejected (terminal), with an optional agent message.</summary>
    public ValueTask RejectAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Rejected, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Moves the task to Canceled (terminal), with an optional agent message.</summary>
    public ValueTask CancelAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Canceled, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Asks the client for more input. The client continues by sending a message with this task id.</summary>
    public ValueTask RequireInputAsync(string text, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.InputRequired, NewAgentMessage(text), null, cancellationToken);

    /// <summary>Asks the client to obtain authorization (spec §7.6). Streams stay open while you wait for out-of-band credentials.</summary>
    public ValueTask RequireAuthAsync(string text, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.AuthRequired, NewAgentMessage(text), null, cancellationToken);

    /// <summary>Moves the task to <paramref name="state"/>, with an optional agent message and metadata. Throws when <paramref name="state"/> is Unspecified or the task is already terminal.</summary>
    public ValueTask UpdateStatusAsync(TaskState state, Message? message = null, Dictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (state == TaskState.Unspecified) throw new ArgumentException("State must be specified.", nameof(state));
        MarkTaskEvent(state.IsTerminal());
        if (message is not null) Stamp(message);
        var evt = new TaskStatusUpdateEvent
        {
            TaskId = TaskId,
            ContextId = ContextId,
            Status = new AgentTaskStatus { State = state, Message = message, Timestamp = DateTimeOffset.UtcNow },
            Metadata = metadata,
        };
        return _writer.WriteAsync(StreamResponse.Of(evt), cancellationToken);
    }

    /// <summary>Adds an artifact, or appends parts to one (<paramref name="append"/>). Returns the artifact id.</summary>
    public async ValueTask<string> AddArtifactAsync(IEnumerable<Part> parts, string? artifactId = null, string? name = null,
        string? description = null, bool append = false, bool lastChunk = false, Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = new Artifact
        {
            ArtifactId = artifactId ?? Guid.NewGuid().ToString(),
            Name = name,
            Description = description,
            Parts = parts.ToList(),
            Metadata = metadata,
        };
        await AddArtifactAsync(artifact, append, lastChunk, cancellationToken).ConfigureAwait(false);
        return artifact.ArtifactId;
    }

    /// <summary>Adds <paramref name="artifact"/>, or appends its parts to the artifact with the same id when <paramref name="append"/> is true. <paramref name="lastChunk"/> marks the final chunk. Both default to false.</summary>
    public ValueTask AddArtifactAsync(Artifact artifact, bool append = false, bool lastChunk = false, CancellationToken cancellationToken = default)
    {
        MarkTaskEvent(false);
        var evt = new TaskArtifactUpdateEvent { TaskId = TaskId, ContextId = ContextId, Artifact = artifact, Append = append, LastChunk = lastChunk };
        return _writer.WriteAsync(StreamResponse.Of(evt), cancellationToken);
    }

    /// <summary>Builds an agent message bound to this task.</summary>
    public Message NewAgentMessage(string text) => Stamp(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] });

    /// <summary>Builds an agent message from <paramref name="parts"/>, bound to this task.</summary>
    public Message NewAgentMessage(params Part[] parts) => Stamp(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = parts.ToList() });

    private Message Stamp(Message m)
    {
        m.Role = Role.Agent;
        m.TaskId = TaskId;
        m.ContextId = ContextId;
        if (string.IsNullOrEmpty(m.MessageId)) m.MessageId = Message.NewId();
        return m;
    }

    private void MarkTaskEvent(bool terminal)
    {
        lock (_gate)
        {
            if (_replied) throw new InvalidOperationException("A direct reply was already sent; no task can be created for this request.");
            if (_terminal) throw new InvalidOperationException("The task is already in a terminal state.");
            _taskEmitted = true;
            if (terminal) _terminal = true;
        }
    }
}

/// <summary>Adapts a delegate to <see cref="IAgentExecutor"/>.</summary>
public sealed class DelegateAgentExecutor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutor
{
    /// <summary>Runs the delegate given to the constructor.</summary>
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        execute(context, updater, cancellationToken);
}
