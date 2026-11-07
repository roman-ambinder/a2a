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

/// <summary>Kind of content held by a <see cref="Part"/>.</summary>
public enum PartKind { None, Text, Raw, Url, Data }

/// <summary>A unit of content: text, raw bytes, a URL, or structured JSON data (spec §4.1.6). Exactly one content field is set.</summary>
public sealed class Part
{
    public string? Text { get; set; }

    /// <summary>Inline file bytes; base64 on the wire.</summary>
    public byte[]? Raw { get; set; }

    public string? Url { get; set; }

    /// <summary>Arbitrary JSON value.</summary>
    public JsonElement? Data { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }

    public string? Filename { get; set; }

    public string? MediaType { get; set; }

    [JsonIgnore]
    public PartKind Kind =>
        Text is not null ? PartKind.Text :
        Raw is not null ? PartKind.Raw :
        Url is not null ? PartKind.Url :
        Data is not null ? PartKind.Data : PartKind.None;

    [JsonIgnore]
    internal int ContentCount =>
        (Text is null ? 0 : 1) + (Raw is null ? 0 : 1) + (Url is null ? 0 : 1) + (Data is null ? 0 : 1);

    public static Part FromText(string text, string? mediaType = null) => new() { Text = text, MediaType = mediaType };

    public static Part FromBytes(byte[] bytes, string mediaType, string? filename = null) =>
        new() { Raw = bytes, MediaType = mediaType, Filename = filename };

    public static Part FromUrl(string url, string? mediaType = null, string? filename = null) =>
        new() { Url = url, MediaType = mediaType, Filename = filename };

    public static Part FromData(JsonElement data, string? mediaType = "application/json") => new() { Data = data, MediaType = mediaType };

    public static Part FromData<T>(T value, string? mediaType = "application/json") =>
        FromData(JsonSerializer.SerializeToElement(value, A2AJson.Options), mediaType);
}

/// <summary>One unit of communication between client and agent (spec §4.1.4).</summary>
public sealed class Message
{
    public string MessageId { get; set; } = string.Empty;
    public string? ContextId { get; set; }
    public string? TaskId { get; set; }
    public Role Role { get; set; }
    public List<Part> Parts { get; set; } = new();
    public Dictionary<string, JsonElement>? Metadata { get; set; }
    public List<string>? Extensions { get; set; }
    public List<string>? ReferenceTaskIds { get; set; }

    public static string NewId() => Guid.NewGuid().ToString();

    public static Message FromUser(string text, string? contextId = null, string? taskId = null) =>
        new() { MessageId = NewId(), Role = Role.User, Parts = [Part.FromText(text)], ContextId = contextId, TaskId = taskId };

    public static Message FromAgent(string text, string? contextId = null, string? taskId = null) =>
        new() { MessageId = NewId(), Role = Role.Agent, Parts = [Part.FromText(text)], ContextId = contextId, TaskId = taskId };

    /// <summary>Concatenated text of all text parts.</summary>
    public string GetText() => string.Concat(Parts.Where(p => p.Text is not null).Select(p => p.Text));
}

/// <summary>An output of a task (spec §4.1.7).</summary>
public sealed class Artifact
{
    public string ArtifactId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<Part> Parts { get; set; } = new();
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
    public string Id { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; } = new();
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
    public Artifact Artifact { get; set; } = new();

    /// <summary>Append these parts to the previously sent artifact with the same id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Append { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LastChunk { get; set; }

    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
