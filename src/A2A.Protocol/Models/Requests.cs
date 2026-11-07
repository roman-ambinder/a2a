using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

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
    public required Message Message { get; set; }
    public SendMessageConfiguration? Configuration { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Result of SendMessage: either a task or a direct message. A closed union; exactly one case.</summary>
[JsonConverter(typeof(SendMessageResponseConverter))]
public abstract record SendMessageResponse
{
    private protected SendMessageResponse() { }
}

/// <summary>SendMessage result: a task.</summary>
[JsonConverter(typeof(OneOfCaseConverter<SendMessageResponse, TaskResponse, SendMessageResponseConverter>))]
public sealed record TaskResponse(AgentTask Task) : SendMessageResponse;

/// <summary>SendMessage result: a direct message.</summary>
[JsonConverter(typeof(OneOfCaseConverter<SendMessageResponse, MessageResponse, SendMessageResponseConverter>))]
public sealed record MessageResponse(Message Message) : SendMessageResponse;

/// <summary>One event in a streaming response or a push notification. A closed union; exactly one case.</summary>
[JsonConverter(typeof(StreamResponseConverter))]
public abstract record StreamResponse
{
    private protected StreamResponse() { }

    public static TaskEvent Of(AgentTask task) => new(task);
    public static MessageEvent Of(Message message) => new(message);
    public static StatusUpdateEvent Of(TaskStatusUpdateEvent e) => new(e);
    public static ArtifactUpdateEvent Of(TaskArtifactUpdateEvent e) => new(e);

    /// <summary>The task id this event refers to, if any.</summary>
    [JsonIgnore]
    public string? TaskId => this switch
    {
        TaskEvent t => t.Task.Id,
        MessageEvent m => m.Message.TaskId,
        StatusUpdateEvent s => s.StatusUpdate.TaskId,
        ArtifactUpdateEvent a => a.ArtifactUpdate.TaskId,
        _ => null,
    };
}

/// <summary>Stream event: a task snapshot.</summary>
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, TaskEvent, StreamResponseConverter>))]
public sealed record TaskEvent(AgentTask Task) : StreamResponse;

/// <summary>Stream event: a message.</summary>
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, MessageEvent, StreamResponseConverter>))]
public sealed record MessageEvent(Message Message) : StreamResponse;

/// <summary>Stream event: task status changed.</summary>
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, StatusUpdateEvent, StreamResponseConverter>))]
public sealed record StatusUpdateEvent(TaskStatusUpdateEvent StatusUpdate) : StreamResponse;

/// <summary>Stream event: artifact created or extended.</summary>
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, ArtifactUpdateEvent, StreamResponseConverter>))]
public sealed record ArtifactUpdateEvent(TaskArtifactUpdateEvent ArtifactUpdate) : StreamResponse;

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
