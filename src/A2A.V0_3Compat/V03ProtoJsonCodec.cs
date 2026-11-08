using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat;

/// <summary>
/// The v0.3 HTTP+JSON dialect, shaped after the v0.3 proto read as ProtoJSON: no <c>kind</c>, parts without a tag
/// (<c>{"text":…}</c>, <c>{"file":{"fileWithUri":…}}</c>, <c>{"data":{"data":{…}}}</c>), <c>TASK_STATE_*</c> states
/// (<c>TASK_STATE_CANCELLED</c> with two Ls), <c>ROLE_*</c> roles, the parts of a message named <c>content</c>, and stream
/// events wrapped as <c>{"statusUpdate":…}</c>. A part has no <c>metadata</c> and no <c>filename</c> here.
/// </summary>
internal sealed class V03ProtoJsonCodec : V03Codec
{
    public static readonly V03ProtoJsonCodec Instance = new();

    private static readonly Dictionary<string, TaskState> States = new(StringComparer.Ordinal)
    {
        ["TASK_STATE_UNSPECIFIED"] = TaskState.Unspecified,
        ["TASK_STATE_SUBMITTED"] = TaskState.Submitted,
        ["TASK_STATE_WORKING"] = TaskState.Working,
        ["TASK_STATE_COMPLETED"] = TaskState.Completed,
        ["TASK_STATE_FAILED"] = TaskState.Failed,
        ["TASK_STATE_CANCELLED"] = TaskState.Canceled,
        ["TASK_STATE_CANCELED"] = TaskState.Canceled, // one L is accepted on read
        ["TASK_STATE_INPUT_REQUIRED"] = TaskState.InputRequired,
        ["TASK_STATE_REJECTED"] = TaskState.Rejected,
        ["TASK_STATE_AUTH_REQUIRED"] = TaskState.AuthRequired,
    };

    private V03ProtoJsonCodec() { }

    protected override bool Tagged => false;
    protected override string MessagePartsName => "content";
    protected override string SendPushConfigName => "pushNotification";
    protected override bool BlockingWhenAbsent => false;

    protected override string StateName(TaskState state) => state switch
    {
        TaskState.Submitted => "TASK_STATE_SUBMITTED",
        TaskState.Working => "TASK_STATE_WORKING",
        TaskState.InputRequired => "TASK_STATE_INPUT_REQUIRED",
        TaskState.Completed => "TASK_STATE_COMPLETED",
        TaskState.Canceled => "TASK_STATE_CANCELLED",
        TaskState.Failed => "TASK_STATE_FAILED",
        TaskState.Rejected => "TASK_STATE_REJECTED",
        TaskState.AuthRequired => "TASK_STATE_AUTH_REQUIRED",
        _ => "TASK_STATE_UNSPECIFIED",
    };

    protected override TaskState ParseState(string name) => ParseStateFrom(name, States);

    protected override string RoleName(Role role) => role switch
    {
        Role.User => "ROLE_USER",
        Role.Agent => "ROLE_AGENT",
        _ => "ROLE_UNSPECIFIED",
    };

    protected override Role ParseRole(string name) => name switch
    {
        "ROLE_USER" => Role.User,
        "ROLE_AGENT" => Role.Agent,
        "ROLE_UNSPECIFIED" => Role.Unspecified,
        _ => throw new JsonException($"Unknown role '{name}'"),
    };

    // ---- parts ----

    protected override JsonObject WritePart(Part part)
    {
        var o = new JsonObject();
        switch (part)
        {
            case TextPart t:
                o["text"] = t.Text;
                DropPartExtras(part, "text/plain", keepFilename: false, keepMediaType: false);
                break;
            case RawPart r:
                o["file"] = File(part, "fileWithBytes", Convert.ToBase64String(r.Raw.Span));
                DropPartExtras(part, "", keepFilename: false, keepMediaType: true);
                break;
            case UrlPart u:
                o["file"] = File(part, "fileWithUri", u.Url.OriginalString);
                DropPartExtras(part, "", keepFilename: false, keepMediaType: true);
                break;
            case DataPart d:
                // The proto holds a Struct: an object. Anything else is wrapped; a proto part has no metadata to mark it with.
                o["data"] = new JsonObject
                {
                    ["data"] = d.Data.ValueKind == JsonValueKind.Object
                        ? V03Json.ToNode(d.Data)
                        : new JsonObject { ["value"] = V03Json.ToNode(d.Data) },
                };
                DropPartExtras(part, "application/json", keepFilename: false, keepMediaType: false);
                break;
            default:
                throw new JsonException($"Unknown part type {part.GetType().Name}");
        }
        if (part.Metadata is { Count: > 0 }) V03LossReporter.Drop("part.metadata");
        return o;
    }

    private static JsonObject File(Part part, string contentName, string content)
    {
        var file = new JsonObject { [contentName] = content };
        V03Json.Set(file, "mimeType", part.MediaType);
        return file;
    }

    protected override Part ReadPart(JsonObject o)
    {
        var text = V03Json.Str(o, "text");
        var file = V03Json.OptObj(o, "file");
        var data = V03Json.OptObj(o, "data");
        var count = (text is null ? 0 : 1) + (file is null ? 0 : 1) + (data is null ? 0 : 1);
        if (count != 1) throw new JsonException($"A part must set exactly one of text, file, data; found {count}");
        if (text is not null) return new TextPart(text);
        if (file is not null)
        {
            var bytes = V03Json.Str(file, "fileWithBytes");
            var uri = V03Json.Str(file, "fileWithUri");
            var mimeType = V03Json.Str(file, "mimeType");
            if (bytes is not null && uri is null)
            {
                byte[] raw;
                try { raw = Convert.FromBase64String(bytes); }
                catch (FormatException) { throw new JsonException("fileWithBytes must be valid base64"); }
                return new RawPart(raw) { MediaType = mimeType };
            }
            if (uri is not null && bytes is null)
            {
                if (!Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out var parsed)) throw new JsonException("fileWithUri is not a valid URI");
                return new UrlPart(parsed) { MediaType = mimeType };
            }
            throw new JsonException("file must set exactly one of fileWithBytes, fileWithUri");
        }
        var inner = V03Json.OptObj(data!, "data");
        return new DataPart(inner is null ? V03Json.ToElement(new JsonObject()) : V03Json.ToElement(inner)) { MediaType = "application/json" };
    }

    // ---- results and events ----

    public override JsonObject WriteStreamEvent(StreamResponse evt) => evt switch
    {
        TaskEvent t => new JsonObject { ["task"] = WriteTask(t.Task) },
        MessageEvent m => new JsonObject { ["message"] = WriteMessage(m.Message) },
        StatusUpdateEvent s => new JsonObject { ["statusUpdate"] = WriteStatusUpdateBody(s.StatusUpdate) },
        ArtifactUpdateEvent a => new JsonObject { ["artifactUpdate"] = WriteArtifactUpdateBody(a.ArtifactUpdate) },
        _ => throw new JsonException($"Unknown stream event {evt.GetType().Name}"),
    };

    public override StreamResponse ReadStreamEvent(JsonNode node, out bool final)
    {
        var o = V03Json.Obj(node, "event");
        final = false;
        if (V03Json.OptObj(o, "statusUpdate") is { } status) return new StatusUpdateEvent(ReadStatusUpdateBody(status, out final));
        if (V03Json.OptObj(o, "artifactUpdate") is { } artifact) return new ArtifactUpdateEvent(ReadArtifactUpdateBody(artifact));
        if (V03Json.OptObj(o, "task") is { } task) return new TaskEvent(ReadTask(task));
        if (V03Json.OptObj(o, "message") is { } message) return new MessageEvent(ReadMessage(message));
        throw new JsonException("event must set one of task, message, statusUpdate, artifactUpdate");
    }

    public override JsonObject WriteSendResult(SendMessageResponse response) => response switch
    {
        TaskResponse t => new JsonObject { ["task"] = WriteTask(t.Task) },
        MessageResponse m => new JsonObject { ["message"] = WriteMessage(m.Message) },
        _ => throw new JsonException($"Unknown send result {response.GetType().Name}"),
    };

    public override SendMessageResponse ReadSendResult(JsonNode node)
    {
        var o = V03Json.Obj(node, "result");
        if (V03Json.OptObj(o, "task") is { } task) return new TaskResponse(ReadTask(task));
        if (V03Json.OptObj(o, "message") is { } message) return new MessageResponse(ReadMessage(message));
        throw new JsonException("result must set one of task, message");
    }

    // ---- push config ----

    /// <summary>The resource name of a push config: <c>tasks/{taskId}/pushNotificationConfigs/{id}</c>.</summary>
    public static string PushConfigName(string taskId, string? configId) =>
        configId is null ? $"tasks/{taskId}/pushNotificationConfigs" : $"tasks/{taskId}/pushNotificationConfigs/{configId}";

    /// <summary>Splits <c>tasks/{id}</c> or <c>tasks/{id}/pushNotificationConfigs[/{configId}]</c>.</summary>
    public static bool TryParseName(string name, out string taskId, out string? configId)
    {
        taskId = "";
        configId = null;
        var parts = name.Split('/');
        if (parts.Length < 2 || parts[0] != "tasks" || parts[1].Length == 0) return false;
        taskId = parts[1];
        if (parts.Length == 2) return true;
        if (parts[2] != "pushNotificationConfigs" || parts.Length > 4) return false;
        if (parts.Length == 4) configId = parts[3];
        return true;
    }

    public override JsonObject WritePushConfig(TaskPushNotificationConfig config) => new()
    {
        ["name"] = PushConfigName(config.TaskId ?? string.Empty, config.Id),
        ["pushNotificationConfig"] = WritePushNotificationConfig(config),
    };

    public override TaskPushNotificationConfig ReadPushConfig(JsonNode node)
    {
        var o = V03Json.Obj(node, "push config");
        var inner = V03Json.OptObj(o, "pushNotificationConfig") ?? throw new JsonException("pushNotificationConfig is required");
        var config = ReadPushNotificationConfig(inner);
        if (!TryParseName(V03Json.ReqStr(o, "name"), out var taskId, out var configId)) throw new JsonException("name is not a task push config name");
        config.TaskId = taskId;
        config.Id ??= configId;
        return config;
    }

    // ---- errors ----

    /// <summary>
    /// The REST error body: <c>{"code":&lt;number&gt;,"message":…}</c>. The code is the A2A error number; the auth codes (no number)
    /// use the HTTP status.
    /// </summary>
    public static JsonObject WriteError(A2AException ex, int httpStatus) => new()
    {
        ["code"] = ex.Code is A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied ? httpStatus : (int)ex.Code,
        ["message"] = ex.Message,
    };
}
