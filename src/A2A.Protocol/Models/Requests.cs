using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A;

/// <summary>HTTP auth used by the agent when calling a webhook (spec §4.3.2).</summary>
public sealed class AuthenticationInfo
{
    /// <summary>IANA HTTP auth scheme, e.g. "Bearer" or "Basic".</summary>
    public string Scheme { get; set; } = "Bearer";

    public string? Credentials { get; set; }
}

/// <summary>Webhook registration for a task (spec §4.3.1). Also the request body of CreateTaskPushNotificationConfig.</summary>
public sealed class TaskPushNotificationConfig
{
    public string? Tenant { get; set; }
    public string? Id { get; set; }
    public string? TaskId { get; set; }
    public string Url { get; set; } = string.Empty;

    /// <summary>Client-chosen token echoed by the agent on each notification (header <c>X-A2A-Notification-Token</c>).</summary>
    public string? Token { get; set; }

    public AuthenticationInfo? Authentication { get; set; }
}

public sealed class SendMessageConfiguration
{
    public List<string>? AcceptedOutputModes { get; set; }

    /// <summary>Webhook to register for the task created by this message. Leave <c>TaskId</c> empty.</summary>
    public TaskPushNotificationConfig? TaskPushNotificationConfig { get; set; }

    public int? HistoryLength { get; set; }

    /// <summary>When true, return as soon as the task exists instead of waiting for a terminal or interrupted state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReturnImmediately { get; set; }
}

public sealed class SendMessageRequest
{
    public string? Tenant { get; set; }
    public Message Message { get; set; } = new();
    public SendMessageConfiguration? Configuration { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Result of SendMessage: either a task or a direct message.</summary>
public sealed class SendMessageResponse
{
    public AgentTask? Task { get; set; }
    public Message? Message { get; set; }
}

/// <summary>One event in a streaming response or a push notification. Exactly one member is set.</summary>
public sealed class StreamResponse
{
    public AgentTask? Task { get; set; }
    public Message? Message { get; set; }
    public TaskStatusUpdateEvent? StatusUpdate { get; set; }
    public TaskArtifactUpdateEvent? ArtifactUpdate { get; set; }

    public static StreamResponse Of(AgentTask task) => new() { Task = task };
    public static StreamResponse Of(Message message) => new() { Message = message };
    public static StreamResponse Of(TaskStatusUpdateEvent e) => new() { StatusUpdate = e };
    public static StreamResponse Of(TaskArtifactUpdateEvent e) => new() { ArtifactUpdate = e };

    /// <summary>The task id this event refers to, if any.</summary>
    [JsonIgnore]
    public string? TaskId => Task?.Id ?? StatusUpdate?.TaskId ?? ArtifactUpdate?.TaskId ?? Message?.TaskId;
}

public sealed class GetTaskRequest
{
    public string? Tenant { get; set; }
    public string Id { get; set; } = string.Empty;
    public int? HistoryLength { get; set; }
}

public sealed class ListTasksRequest
{
    public string? Tenant { get; set; }
    public string? ContextId { get; set; }
    public TaskState? Status { get; set; }

    /// <summary>1..100, default 50.</summary>
    public int? PageSize { get; set; }

    public string? PageToken { get; set; }
    public int? HistoryLength { get; set; }
    public DateTimeOffset? StatusTimestampAfter { get; set; }
    public bool? IncludeArtifacts { get; set; }
}

public sealed class ListTasksResponse
{
    public List<AgentTask> Tasks { get; set; } = new();

    /// <summary>Empty string on the last page.</summary>
    public string NextPageToken { get; set; } = string.Empty;

    public int PageSize { get; set; }
    public int TotalSize { get; set; }
}

public sealed class CancelTaskRequest
{
    public string? Tenant { get; set; }
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

public sealed class SubscribeToTaskRequest
{
    public string? Tenant { get; set; }
    public string Id { get; set; } = string.Empty;
}

public sealed class GetTaskPushNotificationConfigRequest
{
    public string? Tenant { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

public sealed class DeleteTaskPushNotificationConfigRequest
{
    public string? Tenant { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

public sealed class ListTaskPushNotificationConfigsRequest
{
    public string? Tenant { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public int? PageSize { get; set; }
    public string? PageToken { get; set; }
}

public sealed class ListTaskPushNotificationConfigsResponse
{
    public List<TaskPushNotificationConfig> Configs { get; set; } = new();
    public string? NextPageToken { get; set; }
}

public sealed class GetExtendedAgentCardRequest
{
    public string? Tenant { get; set; }
}
