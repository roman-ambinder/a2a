using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>Task lifecycle states (spec §4.1.3).</summary>
[JsonConverter(typeof(ProtoEnumConverter<TaskState>))]
public enum TaskState
{
    /// <summary>The state is not known or not set.</summary>
    [ProtoName("TASK_STATE_UNSPECIFIED")] Unspecified = 0,

    /// <summary>The task was received and has not started.</summary>
    [ProtoName("TASK_STATE_SUBMITTED")] Submitted = 1,

    /// <summary>The agent is working on the task.</summary>
    [ProtoName("TASK_STATE_WORKING")] Working = 2,

    /// <summary>The task finished successfully. Terminal.</summary>
    [ProtoName("TASK_STATE_COMPLETED")] Completed = 3,

    /// <summary>The task finished with an error. Terminal.</summary>
    [ProtoName("TASK_STATE_FAILED")] Failed = 4,

    /// <summary>The task was canceled before it finished. Terminal.</summary>
    [ProtoName("TASK_STATE_CANCELED")] Canceled = 5,

    /// <summary>The agent needs more input from the user. Interrupted.</summary>
    [ProtoName("TASK_STATE_INPUT_REQUIRED")]
    InputRequired = 6,

    /// <summary>The agent refused the task. Terminal.</summary>
    [ProtoName("TASK_STATE_REJECTED")] Rejected = 7,

    /// <summary>The agent needs the user to authenticate. Interrupted.</summary>
    [ProtoName("TASK_STATE_AUTH_REQUIRED")]
    AuthRequired = 8
}

/// <summary>Helpers for <see cref="TaskState" />.</summary>
public static class TaskStateExtensions
{
    /// <summary>Completed, failed, canceled, or rejected.</summary>
    public static bool IsTerminal(this TaskState state)
    {
        return state is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected;
    }

    /// <summary>Input-required or auth-required.</summary>
    public static bool IsInterrupted(this TaskState state)
    {
        return state is TaskState.InputRequired or TaskState.AuthRequired;
    }
}

/// <summary>Sender of a message (spec §4.1.5).</summary>
[JsonConverter(typeof(ProtoEnumConverter<Role>))]
public enum Role
{
    /// <summary>The sender is not known or not set.</summary>
    [ProtoName("ROLE_UNSPECIFIED")] Unspecified = 0,

    /// <summary>The message comes from the user (the client).</summary>
    [ProtoName("ROLE_USER")] User = 1,

    /// <summary>The message comes from the agent.</summary>
    [ProtoName("ROLE_AGENT")] Agent = 2
}

/// <summary>One unit of communication between client and agent (spec §4.1.4).</summary>
public sealed class Message
{
    /// <summary>Unique id of the message. The client sets it on messages it sends.</summary>
    public required string MessageId { get; set; }

    /// <summary>Id of the context (conversation) the message belongs to. Null when not set.</summary>
    public string? ContextId { get; set; }

    /// <summary>Id of the task the message belongs to. Null when not set.</summary>
    public string? TaskId { get; set; }

    /// <summary>Who sent the message.</summary>
    public required Role Role { get; set; }

    /// <summary>Content of the message. Empty by default; a valid message needs at least one part.</summary>
    public required List<Part> Parts { get; set; } = new();

    /// <summary>Extra data about the message. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>URIs of extensions that apply to the message. Null when none.</summary>
    public List<string>? Extensions { get; set; }

    /// <summary>Ids of other tasks this message refers to. Null when none.</summary>
    public List<string>? ReferenceTaskIds { get; set; }

    /// <summary>Creates a new message id (a GUID string).</summary>
    public static string NewId()
    {
        return Guid.NewGuid().ToString();
    }

    /// <summary>Creates a user message with one text part and a new message id.</summary>
    public static Message FromUser(string text, string? contextId = null, string? taskId = null)
    {
        return new Message
        {
            MessageId = NewId(), Role = Role.User, Parts = [Part.FromText(text)], ContextId = contextId, TaskId = taskId
        };
    }

    /// <summary>Creates an agent message with one text part and a new message id.</summary>
    public static Message FromAgent(string text, string? contextId = null, string? taskId = null)
    {
        return new Message
        {
            MessageId = NewId(), Role = Role.Agent, Parts = [Part.FromText(text)], ContextId = contextId,
            TaskId = taskId
        };
    }

    /// <summary>Concatenated text of all text parts.</summary>
    public string GetText()
    {
        return string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
    }
}

/// <summary>An output of a task (spec §4.1.7).</summary>
public sealed class Artifact : IJsonOnDeserialized
{
    /// <summary>Id of the artifact, unique within its task.</summary>
    public required string ArtifactId { get; set; }

    /// <summary>Human-readable name. Null when not set.</summary>
    public string? Name { get; set; }

    /// <summary>What the artifact is. Null when not set.</summary>
    public string? Description { get; set; }

    /// <summary>Content of the artifact. Empty by default.</summary>
    public required List<Part> Parts { get; set; } = new();

    /// <summary>Extra data about the artifact. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>URIs of extensions that apply to the artifact. Null when none.</summary>
    public List<string>? Extensions { get; set; }

    /// <summary>A2A 1.1: the task generation at which the artifact first appeared. Null when not known (or for 1.0).</summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? StartGeneration { get; set; }

    /// <summary>A2A 1.1: the task generation at which the artifact was completed. Null while it is still streaming.</summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? EndGeneration { get; set; }

    /// <summary>
    ///     A2A 1.1: who wrote the artifact. Null (or <see cref="A2A.Role.Agent" />) for an agent output;
    ///     <see cref="A2A.Role.User" />
    ///     for an artifact written by the client.
    /// </summary>
    public Role? Role { get; set; }

    // See AgentCard: omitted parts must read as an empty list, not null.
    void IJsonOnDeserialized.OnDeserialized()
    {
        Parts ??= new List<Part>();
    }

    /// <summary>Concatenated text of all text parts. Empty when the artifact has none.</summary>
    public string GetText()
    {
        return string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
    }
}

/// <summary>Status of a task (spec §4.1.2). Named to avoid clashing with <see cref="System.Threading.Tasks.TaskStatus" />.</summary>
public sealed class AgentTaskStatus
{
    /// <summary>Current state of the task.</summary>
    public TaskState State { get; set; }

    /// <summary>Message that explains the state, for example the question when input is required. Null when none.</summary>
    public Message? Message { get; set; }

    /// <summary>When the status was set, in UTC. Null when not set.</summary>
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>The core unit of work (spec §4.1.1). Named to avoid clashing with <see cref="System.Threading.Tasks.Task" />.</summary>
public sealed class AgentTask
{
    /// <summary>Unique id of the task, set by the server.</summary>
    public required string Id { get; set; }

    /// <summary>Id of the context (conversation) the task belongs to. Empty by default.</summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>Current status of the task.</summary>
    public required AgentTaskStatus Status { get; set; }

    /// <summary>Outputs produced so far. Null when none or not requested.</summary>
    public List<Artifact>? Artifacts { get; set; }

    /// <summary>
    ///     Messages exchanged in the task, oldest first. Null when none or not requested. Deprecated in A2A 1.1 in favour of
    ///     <see cref="Timeline" />; servers keep filling it for the whole 1.x line.
    /// </summary>
    public List<Message>? History { get; set; }

    /// <summary>Extra data about the task. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>
    ///     A2A 1.1: a counter the server raises by exactly 1 on every change to the task's timeline or artifacts. 1 when the
    ///     task
    ///     is created. Null when not known: a 1.0 peer, or a server that does not implement it (it sends 0).
    /// </summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? Generation { get; set; }

    /// <summary>
    ///     A2A 1.1: the interaction in generation order: client messages and agent status entries. Null when none or not
    ///     requested.
    /// </summary>
    public List<TimelineEntry>? Timeline { get; set; }

    /// <summary>
    ///     Concatenated text of the text parts of all <see cref="Artifacts" />, in order. Empty when there is none. The
    ///     status message (for example the question of an input-required task) is not included: read
    ///     <see cref="AgentTaskStatus.Message" /> for it.
    /// </summary>
    public string GetText()
    {
        return Artifacts is null ? string.Empty : string.Concat(Artifacts.Select(a => a.GetText()));
    }
}

/// <summary>
///     A2A 1.1: one entry of a task's <see cref="AgentTask.Timeline" /> (spec §4.1.8). Exactly one of
///     <see cref="Message" /> (client
///     input) and <see cref="Status" /> (agent status) is set. An entry of a kind this SDK does not know has neither:
///     readers skip it
///     (<see cref="IsUnknownKind" />), and its <see cref="Generation" /> still counts.
/// </summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TimelineEntry
{
    /// <summary>A client message recorded in the timeline. Null for another kind of entry.</summary>
    public Message? Message { get; set; }

    /// <summary>An agent status (state plus the agent's message) recorded in the timeline. Null for another kind of entry.</summary>
    public AgentTaskStatus? Status { get; set; }

    /// <summary>The task's generation right after this entry was appended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(ProtoInt64Converter))]
    public long Generation { get; set; }

    /// <summary>True when the entry has neither a message nor a status: a kind added by a newer protocol version. Skip it.</summary>
    [JsonIgnore]
    public bool IsUnknownKind => Message is null && Status is null;

    /// <summary>Creates a client-message entry.</summary>
    public static TimelineEntry FromMessage(Message message, long generation)
    {
        return new TimelineEntry { Message = message, Generation = generation };
    }

    /// <summary>Creates an agent-status entry.</summary>
    public static TimelineEntry FromStatus(AgentTaskStatus status, long generation)
    {
        return new TimelineEntry { Status = status, Generation = generation };
    }
}

/// <summary>Streaming/push event: task status changed (spec §4.2.1).</summary>
public sealed class TaskStatusUpdateEvent
{
    /// <summary>Id of the task that changed.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Id of the context the task belongs to.</summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>The new status of the task.</summary>
    public AgentTaskStatus Status { get; set; } = new();

    /// <summary>Extra data about the update. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>A2A 1.1: the task's generation after this update. Null when not known (a 1.0 peer).</summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? Generation { get; set; }
}

/// <summary>Streaming/push event: artifact created or extended (spec §4.2.2).</summary>
public sealed class TaskArtifactUpdateEvent
{
    /// <summary>Id of the task that produced the artifact.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Id of the context the task belongs to.</summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>The artifact, or the new chunk of it when <see cref="Append" /> is true.</summary>
    public required Artifact Artifact { get; set; }

    /// <summary>Append these parts to the previously sent artifact with the same id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Append { get; set; }

    /// <summary>True if this is the last chunk of the artifact. Defaults to false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LastChunk { get; set; }

    /// <summary>Extra data about the update. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>
    ///     A2A 1.1: the task's generation after this update. Null when not known (a 1.0 peer). A client that sends an artifact
    ///     update
    ///     leaves it null; the server ignores any value it gets.
    /// </summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? Generation { get; set; }
}

/// <summary>A2A 1.1 streaming event: a message was added to the task, for example client input (spec §4.2.3).</summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TaskMessageUpdateEvent
{
    /// <summary>Id of the task the message was added to.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Id of the context the task belongs to.</summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>The message that was added.</summary>
    public required Message Message { get; set; }

    /// <summary>Extra data about the update. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>The task's generation after the message was added. Null when not known.</summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? Generation { get; set; }
}

/// <summary>
///     A2A 1.1: a non-fatal refusal of one client request on a live stream (spec §4.2.4). The stream stays open, so the
///     client can
///     correct the request and send it again.
/// </summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class StreamError
{
    /// <summary>Id of the task the refused request applied to. Null when not known.</summary>
    public string? TaskId { get; set; }

    /// <summary>
    ///     Machine-readable reason, with the codes of the unary operation, for example <c>INVALID_ARGUMENT</c> or
    ///     <c>UNAVAILABLE</c>.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>Human-readable reason. Null when none.</summary>
    public string? Message { get; set; }

    /// <summary>
    ///     The <c>messageId</c> of the refused message, or the <c>artifactId</c> of the refused artifact update. Null
    ///     when none.
    /// </summary>
    public string? ReferenceId { get; set; }

    /// <summary>Extra data about the refusal. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}