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

    /// <summary>Called on CancelTask. Default: mark the task canceled.</summary>
    Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken) =>
        updater.CancelAsync(cancellationToken: cancellationToken).AsTask();
}

/// <summary>Everything the executor knows about the incoming message.</summary>
public sealed class AgentRequestContext
{
    public required Message Message { get; init; }

    /// <summary>The task id: existing, or pre-assigned for a new task (only materialized if you emit a task event).</summary>
    public required string TaskId { get; init; }

    public required string ContextId { get; init; }

    /// <summary>The existing task (including this message in its history), or null for a new conversation turn.</summary>
    public AgentTask? Task { get; init; }

    public SendMessageConfiguration? Configuration { get; init; }

    public Dictionary<string, JsonElement>? Metadata { get; init; }

    public required ServerCallContext Call { get; init; }

    public bool IsNewTask => Task is null;

    /// <summary>Concatenated text of the incoming message.</summary>
    public string UserText => Message.GetText();
}

/// <summary>
/// Emits task events. Events are applied to the store in order, then streamed to subscribers and sent to webhooks.
/// After a terminal state, further task events are rejected.
/// </summary>
public sealed class TaskUpdater
{
    private readonly ChannelWriter<StreamResponse> _writer;
    private readonly bool _isNewTask;
    private readonly object _gate = new();
    private bool _taskEmitted;
    private bool _replied;
    private bool _terminal;

    internal TaskUpdater(string taskId, string contextId, bool isNewTask, ChannelWriter<StreamResponse> writer)
    {
        TaskId = taskId;
        ContextId = contextId;
        _isNewTask = isNewTask;
        _writer = writer;
    }

    public string TaskId { get; }
    public string ContextId { get; }

    public bool IsTerminal { get { lock (_gate) return _terminal; } }
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

    public ValueTask ReplyAsync(string text, CancellationToken cancellationToken = default) =>
        ReplyAsync(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] }, cancellationToken);

    public ValueTask SubmitAsync(CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Submitted, null, null, cancellationToken);

    public ValueTask StartWorkAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Working, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    public ValueTask CompleteAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Completed, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    public ValueTask FailAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Failed, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    public ValueTask RejectAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Rejected, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    public ValueTask CancelAsync(string? text = null, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.Canceled, text is null ? null : NewAgentMessage(text), null, cancellationToken);

    /// <summary>Asks the client for more input. The client continues by sending a message with this task id.</summary>
    public ValueTask RequireInputAsync(string text, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.InputRequired, NewAgentMessage(text), null, cancellationToken);

    /// <summary>Asks the client to obtain authorization (spec §7.6). Streams stay open while you wait for out-of-band credentials.</summary>
    public ValueTask RequireAuthAsync(string text, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(TaskState.AuthRequired, NewAgentMessage(text), null, cancellationToken);

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

    public ValueTask AddArtifactAsync(Artifact artifact, bool append = false, bool lastChunk = false, CancellationToken cancellationToken = default)
    {
        MarkTaskEvent(false);
        var evt = new TaskArtifactUpdateEvent { TaskId = TaskId, ContextId = ContextId, Artifact = artifact, Append = append, LastChunk = lastChunk };
        return _writer.WriteAsync(StreamResponse.Of(evt), cancellationToken);
    }

    /// <summary>Builds an agent message bound to this task.</summary>
    public Message NewAgentMessage(string text) => Stamp(new Message { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] });

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
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        execute(context, updater, cancellationToken);
}
