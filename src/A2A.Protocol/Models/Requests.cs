using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>HTTP auth used by the agent when calling a webhook (spec §4.3.2).</summary>
public sealed class AuthenticationInfo
{
    /// <summary>IANA HTTP auth scheme, e.g. "Bearer" or "Basic".</summary>
    public string Scheme { get; set; } = "Bearer";

    /// <summary>Credential for the auth scheme, for example the bearer token. Null when not set.</summary>
    public string? Credentials { get; set; }
}

/// <summary>Webhook registration for a task (spec §4.3.1). Also the request body of CreateTaskPushNotificationConfig.</summary>
public sealed class TaskPushNotificationConfig
{
    /// <summary>Tenant the registration belongs to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of this registration. The server assigns one when it is null.</summary>
    public string? Id { get; set; }

    /// <summary>Id of the task to notify about.</summary>
    public string? TaskId { get; set; }

    /// <summary>HTTPS URL that receives the notifications.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Client-chosen token echoed by the agent on each notification (header <c>X-A2A-Notification-Token</c>).</summary>
    public string? Token { get; set; }

    /// <summary>Credentials the agent uses when it calls <see cref="Url" />. Null for no authentication.</summary>
    public AuthenticationInfo? Authentication { get; set; }

    /// <summary>
    ///     A2A 1.1: the protocol version of the notifications sent to <see cref="Url" />. Null means the version of the
    ///     request that
    ///     created the config, or <c>"1.0"</c>.
    /// </summary>
    public string? ProtocolVersion { get; set; }
}

/// <summary>Options of a SendMessage call (spec §3.2.2).</summary>
public sealed class SendMessageConfiguration
{
    /// <summary>Output media types the client accepts. Null means any.</summary>
    public List<string>? AcceptedOutputModes { get; set; }

    /// <summary>Webhook to register for the task created by this message. Leave <c>TaskId</c> empty.</summary>
    public TaskPushNotificationConfig? TaskPushNotificationConfig { get; set; }

    /// <summary>
    ///     How many recent history messages to return in the task. Null means the server default. Deprecated in A2A 1.1
    ///     (use <see cref="TimelineLength" />).
    /// </summary>
    public int? HistoryLength { get; set; }

    /// <summary>When true, return as soon as the task exists instead of waiting for a terminal or interrupted state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReturnImmediately { get; set; }

    /// <summary>A2A 1.1: how many recent timeline entries to return in the task. Null means no limit from the client.</summary>
    public int? TimelineLength { get; set; }

    /// <summary>
    ///     A2A 1.1: refuse the message (<see cref="A2AErrorCode.TaskGenerationMismatch" />) unless the task's generation
    ///     equals this
    ///     value when the message is processed. Ignored when the message creates a task. Null for no check.
    /// </summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? IfGenerationMatch { get; set; }
}

/// <summary>Request body of SendMessage and SendStreamingMessage (spec §3.1.1, §3.1.2).</summary>
public sealed class SendMessageRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>
    ///     The message to send. Required to create a task. A2A 1.1: may be null only for a request that sends
    ///     <see cref="ArtifactUpdates" /> to an existing task (each update names its task).
    /// </summary>
    public Message? Message { get; set; }

    /// <summary>Options of the call. Null for the defaults.</summary>
    public SendMessageConfiguration? Configuration { get; set; }

    /// <summary>Extra data about the request. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    /// <summary>
    ///     A2A 1.1: artifacts written by the client, sent with the message or on their own to an existing task. Needs
    ///     <see cref="AgentCapabilities.UserArtifacts" />. Leave each update's <c>Generation</c> null. Null when none.
    /// </summary>
    public List<TaskArtifactUpdateEvent>? ArtifactUpdates { get; set; }
}

/// <summary>
///     A2A 1.1: one client request on a live stream (spec §3.1.12). Exactly one of <see cref="SendMessage" /> and
///     <see cref="Subscribe" /> is set; a request with neither has a kind this SDK does not know (
///     <see cref="IsUnknownKind" />).
/// </summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class StreamRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Sends a message (and optionally artifact updates) on the stream.</summary>
    public SendMessageRequest? SendMessage { get; set; }

    /// <summary>Attaches the stream to an existing task. Valid only as the first request of a stream.</summary>
    public SubscribeToTaskRequest? Subscribe { get; set; }

    /// <summary>True when neither payload is set: a kind added by a newer protocol version.</summary>
    [JsonIgnore]
    public bool IsUnknownKind => SendMessage is null && Subscribe is null;
}

/// <summary>Result of SendMessage: either a task or a direct message. A closed union; exactly one case.</summary>
[JsonConverter(typeof(SendMessageResponseConverter))]
public abstract record SendMessageResponse
{
    private protected SendMessageResponse()
    {
    }
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
    private protected StreamResponse()
    {
    }

    /// <summary>The task id this event refers to, if any.</summary>
    [JsonIgnore]
    public string? TaskId => this switch
    {
        TaskEvent t => t.Task.Id,
        MessageEvent m => m.Message.TaskId,
        StatusUpdateEvent s => s.StatusUpdate.TaskId,
        ArtifactUpdateEvent a => a.ArtifactUpdate.TaskId,
        MessageUpdateEvent u => u.MessageUpdate.TaskId,
        StreamErrorEvent e => e.Error.TaskId,
        _ => null
    };

    /// <summary>Creates a <see cref="TaskEvent" /> for <paramref name="task" />.</summary>
    public static TaskEvent Of(AgentTask task)
    {
        return new TaskEvent(task);
    }

    /// <summary>Creates a <see cref="MessageEvent" /> for <paramref name="message" />.</summary>
    public static MessageEvent Of(Message message)
    {
        return new MessageEvent(message);
    }

    /// <summary>Creates a <see cref="StatusUpdateEvent" /> for a status change.</summary>
    public static StatusUpdateEvent Of(TaskStatusUpdateEvent e)
    {
        return new StatusUpdateEvent(e);
    }

    /// <summary>Creates an <see cref="ArtifactUpdateEvent" /> for an artifact update.</summary>
    public static ArtifactUpdateEvent Of(TaskArtifactUpdateEvent e)
    {
        return new ArtifactUpdateEvent(e);
    }

    /// <summary>A2A 1.1: creates a <see cref="MessageUpdateEvent" /> for a message added to a task.</summary>
    public static MessageUpdateEvent Of(TaskMessageUpdateEvent e)
    {
        return new MessageUpdateEvent(e);
    }

    /// <summary>A2A 1.1: creates a <see cref="StreamErrorEvent" /> for a refused request on a live stream.</summary>
    public static StreamErrorEvent Of(StreamError e)
    {
        return new StreamErrorEvent(e);
    }
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

/// <summary>A2A 1.1 stream event: a message was added to the task (client input). Sent only to 1.1 callers.</summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, MessageUpdateEvent, StreamResponseConverter>))]
public sealed record MessageUpdateEvent(TaskMessageUpdateEvent MessageUpdate) : StreamResponse;

/// <summary>A2A 1.1 stream event: one client request on a live stream was refused. The stream stays open.</summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
[JsonConverter(typeof(OneOfCaseConverter<StreamResponse, StreamErrorEvent, StreamResponseConverter>))]
public sealed record StreamErrorEvent(StreamError Error) : StreamResponse;

/// <summary>Request body of GetTask (spec §3.1.3).</summary>
public sealed class GetTaskRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task to fetch.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     How many recent history messages to return. Null means the server default. Deprecated in A2A 1.1 (use
    ///     <see cref="TimelineLength" />).
    /// </summary>
    public int? HistoryLength { get; set; }

    /// <summary>A2A 1.1: how many recent timeline entries to return. Null means no limit from the client.</summary>
    public int? TimelineLength { get; set; }

    /// <summary>
    ///     A2A 1.1 long-poll: when set, the server waits until the task's generation is greater than this value (or a timeout)
    ///     before
    ///     it answers. Null to answer at once.
    /// </summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? CurrentGeneration { get; set; }
}

/// <summary>Request body of ListTasks (spec §3.1.4).</summary>
public sealed class ListTasksRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Only return tasks in this context. Null means any.</summary>
    public string? ContextId { get; set; }

    /// <summary>Only return tasks in this state. Null means any.</summary>
    public TaskState? Status { get; set; }

    /// <summary>1..100, default 50.</summary>
    public int? PageSize { get; set; }

    /// <summary>Token from <see cref="ListTasksResponse.NextPageToken" /> to fetch the next page. Null for the first page.</summary>
    public string? PageToken { get; set; }

    /// <summary>
    ///     How many recent history messages to return per task. Null means the server default. Deprecated in A2A 1.1 (use
    ///     <see cref="TimelineLength" />).
    /// </summary>
    public int? HistoryLength { get; set; }

    /// <summary>A2A 1.1: how many recent timeline entries to return per task. Null means no limit from the client.</summary>
    public int? TimelineLength { get; set; }

    /// <summary>Only return tasks whose status changed after this time. Null means no limit.</summary>
    public DateTimeOffset? StatusTimestampAfter { get; set; }

    /// <summary>True to include artifacts in the returned tasks. Null means the server default.</summary>
    public bool? IncludeArtifacts { get; set; }
}

/// <summary>Response of ListTasks (spec §3.1.4).</summary>
public sealed class ListTasksResponse
{
    /// <summary>The tasks on this page.</summary>
    public List<AgentTask> Tasks { get; set; } = new();

    /// <summary>Empty string on the last page.</summary>
    public string NextPageToken { get; set; } = string.Empty;

    /// <summary>Number of tasks on this page.</summary>
    public int PageSize { get; set; }

    /// <summary>Number of tasks that match the filters, across all pages.</summary>
    public int TotalSize { get; set; }
}

/// <summary>Request body of CancelTask (spec §3.1.5).</summary>
public sealed class CancelTaskRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task to cancel.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Extra data about the request. Null when none.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

/// <summary>Request body of SubscribeToTask (spec §3.1.6).</summary>
public sealed class SubscribeToTaskRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task to follow.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     A2A 1.1: resume after this generation: the server replays only what followed it and sends no task snapshot first.
    ///     Null to
    ///     start with a snapshot.
    /// </summary>
    [JsonConverter(typeof(ProtoNullableInt64Converter))]
    public long? LastSeenGeneration { get; set; }
}

/// <summary>Request body of GetTaskPushNotificationConfig (spec §3.1.8).</summary>
public sealed class GetTaskPushNotificationConfigRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task the registration belongs to.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Id of the registration to fetch.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Request body of DeleteTaskPushNotificationConfig (spec §3.1.10).</summary>
public sealed class DeleteTaskPushNotificationConfigRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task the registration belongs to.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Id of the registration to delete.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Request body of ListTaskPushNotificationConfigs (spec §3.1.9).</summary>
public sealed class ListTaskPushNotificationConfigsRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }

    /// <summary>Id of the task whose registrations to list.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Maximum number of registrations to return. Null means the server default.</summary>
    public int? PageSize { get; set; }

    /// <summary>
    ///     Token from <see cref="ListTaskPushNotificationConfigsResponse.NextPageToken" /> to fetch the next page. Null
    ///     for the first page.
    /// </summary>
    public string? PageToken { get; set; }
}

/// <summary>Response of ListTaskPushNotificationConfigs (spec §3.1.9).</summary>
public sealed class ListTaskPushNotificationConfigsResponse
{
    /// <summary>The registrations on this page.</summary>
    public List<TaskPushNotificationConfig> Configs { get; set; } = new();

    /// <summary>Token for the next page. Null on the last page.</summary>
    public string? NextPageToken { get; set; }
}

/// <summary>Request body of GetExtendedAgentCard (spec §3.1.11).</summary>
public sealed class GetExtendedAgentCardRequest
{
    /// <summary>Tenant to route the request to. Null when the agent is not multi-tenant.</summary>
    public string? Tenant { get; set; }
}