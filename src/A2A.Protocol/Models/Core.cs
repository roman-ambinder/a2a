using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>Task lifecycle states (spec §4.1.3).</summary>
[JsonConverter(typeof(ProtoEnumConverter<TaskState>))]
public enum TaskState
{
    [ProtoName("TASK_STATE_UNSPECIFIED")] Unspecified = 0,
    [ProtoName("TASK_STATE_SUBMITTED")] Submitted = 1,
    [ProtoName("TASK_STATE_WORKING")] Working = 2,
    [ProtoName("TASK_STATE_COMPLETED")] Completed = 3,
    [ProtoName("TASK_STATE_FAILED")] Failed = 4,
    [ProtoName("TASK_STATE_CANCELED")] Canceled = 5,
    [ProtoName("TASK_STATE_INPUT_REQUIRED")] InputRequired = 6,
    [ProtoName("TASK_STATE_REJECTED")] Rejected = 7,
    [ProtoName("TASK_STATE_AUTH_REQUIRED")] AuthRequired = 8,
}

public static class TaskStateExtensions
{
    /// <summary>Completed, failed, canceled, or rejected.</summary>
    public static bool IsTerminal(this TaskState state) =>
        state is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected;

    /// <summary>Input-required or auth-required.</summary>
    public static bool IsInterrupted(this TaskState state) =>
        state is TaskState.InputRequired or TaskState.AuthRequired;
}

/// <summary>Sender of a message (spec §4.1.5).</summary>
[JsonConverter(typeof(ProtoEnumConverter<Role>))]
public enum Role
{
    [ProtoName("ROLE_UNSPECIFIED")] Unspecified = 0,
    [ProtoName("ROLE_USER")] User = 1,
    [ProtoName("ROLE_AGENT")] Agent = 2,
}

/// <summary>One unit of communication between client and agent (spec §4.1.4).</summary>
public sealed class Message
{
    public required string MessageId { get; set; }
    public string? ContextId { get; set; }
    public string? TaskId { get; set; }
    public required Role Role { get; set; }
    public required List<Part> Parts { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
    public List<string>? Extensions { get; set; }
    public List<string>? ReferenceTaskIds { get; set; }

    public static string NewId() => Guid.NewGuid().ToString();

    public static Message FromUser(string text, string? contextId = null, string? taskId = null) =>
        new() { MessageId = NewId(), Role = Role.User, Parts = [Part.FromText(text)], ContextId = contextId, TaskId = taskId };

    public static Message FromAgent(string text, string? contextId = null, string? taskId = null) =>
        new() { MessageId = NewId(), Role = Role.Agent, Parts = [Part.FromText(text)], ContextId = contextId, TaskId = taskId };

    /// <summary>Concatenated text of all text parts.</summary>
    public string GetText() => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>An output of a task (spec §4.1.7).</summary>
public sealed class Artifact
{
    public required string ArtifactId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public required List<Part> Parts { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
    public List<string>? Extensions { get; set; }
}

/// <summary>Status of a task (spec §4.1.2). Named to avoid clashing with <see cref="System.Threading.Tasks.TaskStatus"/>.</summary>
public sealed class AgentTaskStatus
{
    public TaskState State { get; set; }
    public Message? Message { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>The core unit of work (spec §4.1.1). Named to avoid clashing with <see cref="System.Threading.Tasks.Task"/>.</summary>
public sealed class AgentTask
{
    public required string Id { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public required AgentTaskStatus Status { get; set; }
    public List<Artifact>? Artifacts { get; set; }
    public List<Message>? History { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Streaming/push event: task status changed (spec §4.2.1).</summary>
public sealed class TaskStatusUpdateEvent
{
    public string TaskId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; } = new();
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Streaming/push event: artifact created or extended (spec §4.2.2).</summary>
public sealed class TaskArtifactUpdateEvent
{
    public string TaskId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public required Artifact Artifact { get; set; }

    /// <summary>Append these parts to the previously sent artifact with the same id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Append { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LastChunk { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
