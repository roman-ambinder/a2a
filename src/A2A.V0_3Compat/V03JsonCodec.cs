using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat;

/// <summary>
///     The v0.3 JSON-RPC dialect, shaped after the v0.3 JSON schema: <c>kind</c> tags, <c>{"kind":"text"}</c> parts,
///     lower-case
///     kebab states (<c>input-required</c>), roles <c>user</c> and <c>agent</c>, flat stream events.
/// </summary>
internal sealed class V03JsonCodec : V03Codec
{
    /// <summary>Metadata key that marks a data part whose non-object value was wrapped as <c>{"value":…}</c>.</summary>
    public const string DataPartCompatKey = "data_part_compat";

    public static readonly V03JsonCodec Instance = new();

    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();

    private static readonly Dictionary<string, TaskState> States = new(StringComparer.Ordinal)
    {
        ["submitted"] = TaskState.Submitted,
        ["working"] = TaskState.Working,
        ["input-required"] = TaskState.InputRequired,
        ["completed"] = TaskState.Completed,
        ["canceled"] = TaskState.Canceled,
        ["failed"] = TaskState.Failed,
        ["rejected"] = TaskState.Rejected,
        ["auth-required"] = TaskState.AuthRequired,
        ["unknown"] = TaskState.Unspecified
    };

    private V03JsonCodec()
    {
    }

    protected override bool Tagged => true;
    protected override string MessagePartsName => "parts";
    protected override string SendPushConfigName => "pushNotificationConfig";

    protected override string StateName(TaskState state)
    {
        return state switch
        {
            TaskState.Submitted => "submitted",
            TaskState.Working => "working",
            TaskState.InputRequired => "input-required",
            TaskState.Completed => "completed",
            TaskState.Canceled => "canceled",
            TaskState.Failed => "failed",
            TaskState.Rejected => "rejected",
            TaskState.AuthRequired => "auth-required",
            _ => "unknown"
        };
    }

    protected override TaskState ParseState(string name)
    {
        return ParseStateFrom(name, States);
    }

    protected override string RoleName(Role role)
    {
        return role == Role.User ? "user" : "agent";
    }

    protected override Role ParseRole(string name)
    {
        return name switch
        {
            "user" => Role.User,
            "agent" => Role.Agent,
            _ => throw new JsonException($"Unknown role '{name}'")
        };
    }

    // ---- parts ----

    protected override JsonObject WritePart(Part part)
    {
        var o = new JsonObject();
        var metadata = part.Metadata;
        switch (part)
        {
            case TextPart t:
                o["kind"] = "text";
                o["text"] = t.Text;
                DropPartExtras(part, "text/plain", false, false);
                break;
            case RawPart r:
                o["kind"] = "file";
                o["file"] = File(part, "bytes", Convert.ToBase64String(r.Raw.Span));
                break;
            case UrlPart u:
                o["kind"] = "file";
                o["file"] = File(part, "uri", u.Url.OriginalString);
                break;
            case DataPart d:
                o["kind"] = "data";
                if (d.Data.ValueKind == JsonValueKind.Object)
                {
                    o["data"] = V03Json.ToNode(d.Data);
                }
                else
                {
                    // v0.3 data must be an object. Wrap anything else and mark it, so a v1.0 reader can unwrap it (as a2a-python does).
                    o["data"] = new JsonObject { ["value"] = V03Json.ToNode(d.Data) };
                    metadata = metadata is null
                        ? new Dictionary<string, JsonElement>()
                        : new Dictionary<string, JsonElement>(metadata);
                    metadata[DataPartCompatKey] = True;
                }

                DropPartExtras(part, "application/json", false, false);
                break;
            default:
                throw new JsonException($"Unknown part type {part.GetType().Name}");
        }

        V03Json.SetMetadata(o, metadata);
        return o;
    }

    private static JsonObject File(Part part, string contentName, string content)
    {
        var file = new JsonObject { [contentName] = content };
        V03Json.Set(file, "mimeType", part.MediaType);
        V03Json.Set(file, "name", part.Filename);
        return file;
    }

    protected override Part ReadPart(JsonObject o)
    {
        var kind = V03Json.Str(o, "kind") ?? Infer(o);
        var metadata = V03Json.Metadata(o);
        switch (kind)
        {
            case "text":
                return new TextPart(V03Json.ReqStr(o, "text")) { Metadata = metadata };
            case "file":
            {
                var file = V03Json.OptObj(o, "file") ?? throw new JsonException("file is required");
                var bytes = V03Json.Str(file, "bytes");
                var uri = V03Json.Str(file, "uri");
                var mimeType = V03Json.Str(file, "mimeType");
                var name = V03Json.Str(file, "name");
                if (bytes is not null && uri is null)
                {
                    byte[] raw;
                    try
                    {
                        raw = Convert.FromBase64String(bytes);
                    }
                    catch (FormatException)
                    {
                        throw new JsonException("file bytes must be valid base64");
                    }

                    return new RawPart(raw) { Metadata = metadata, MediaType = mimeType, Filename = name };
                }

                if (uri is not null && bytes is null)
                {
                    if (!Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out var parsed))
                        throw new JsonException("file uri is not a valid URI");
                    return new UrlPart(parsed) { Metadata = metadata, MediaType = mimeType, Filename = name };
                }

                throw new JsonException("file must set exactly one of bytes, uri");
            }
            case "data":
            {
                var data = V03Json.OptObj(o, "data") ?? throw new JsonException("data must be a JSON object");
                if (metadata is not null && metadata.TryGetValue(DataPartCompatKey, out var marker) &&
                    marker.ValueKind == JsonValueKind.True
                    && data.Count == 1 && data["value"] is { } wrapped)
                {
                    metadata.Remove(DataPartCompatKey);
                    return new DataPart(V03Json.ToElement(wrapped))
                        { Metadata = metadata.Count == 0 ? null : metadata, MediaType = "application/json" };
                }

                return new DataPart(V03Json.ToElement(data)) { Metadata = metadata, MediaType = "application/json" };
            }
            default:
                throw new JsonException($"Unknown part kind '{kind}'");
        }
    }

    private static string Infer(JsonObject o)
    {
        return o["text"] is not null ? "text" :
            o["file"] is not null ? "file" :
            o["data"] is not null ? "data" : throw new JsonException("part has no kind and no text, file or data");
    }

    // ---- results and events ----

    public override JsonObject WriteStreamEvent(StreamResponse evt)
    {
        return evt switch
        {
            TaskEvent t => WriteTask(t.Task),
            MessageEvent m => WriteMessage(m.Message),
            StatusUpdateEvent s => WriteStatusUpdateBody(s.StatusUpdate),
            ArtifactUpdateEvent a => WriteArtifactUpdateBody(a.ArtifactUpdate),
            _ => throw new JsonException($"Unknown stream event {evt.GetType().Name}")
        };
    }

    public override StreamResponse ReadStreamEvent(JsonNode node, out bool final)
    {
        var o = V03Json.Obj(node, "event");
        final = false;
        var kind = V03Json.Str(o, "kind") ?? (o["artifact"] is not null ? "artifact-update"
            : o["messageId"] is not null ? "message"
            : o["final"] is not null ? "status-update"
            : o["status"] is not null ? "task" : null);
        switch (kind)
        {
            case "task": return new TaskEvent(ReadTask(o));
            case "message": return new MessageEvent(ReadMessage(o));
            case "status-update": return new StatusUpdateEvent(ReadStatusUpdateBody(o, out final));
            case "artifact-update": return new ArtifactUpdateEvent(ReadArtifactUpdateBody(o));
            default: throw new JsonException(kind is null ? "event has no kind" : $"Unknown event kind '{kind}'");
        }
    }

    public override JsonObject WriteSendResult(SendMessageResponse response)
    {
        return response switch
        {
            TaskResponse t => WriteTask(t.Task),
            MessageResponse m => WriteMessage(m.Message),
            _ => throw new JsonException($"Unknown send result {response.GetType().Name}")
        };
    }

    public override SendMessageResponse ReadSendResult(JsonNode node)
    {
        var o = V03Json.Obj(node, "result");
        var kind = V03Json.Str(o, "kind") ?? (o["messageId"] is not null ? "message" : "task");
        return kind switch
        {
            "task" => new TaskResponse(ReadTask(o)),
            "message" => new MessageResponse(ReadMessage(o)),
            _ => throw new JsonException($"Unknown result kind '{kind}'")
        };
    }

    // ---- push config ----

    public override JsonObject WritePushConfig(TaskPushNotificationConfig config)
    {
        return new JsonObject
        {
            ["taskId"] = config.TaskId ?? string.Empty,
            ["pushNotificationConfig"] = WritePushNotificationConfig(config)
        };
    }

    public override TaskPushNotificationConfig ReadPushConfig(JsonNode node)
    {
        var o = V03Json.Obj(node, "push config");
        var inner = V03Json.OptObj(o, "pushNotificationConfig") ??
                    throw new JsonException("pushNotificationConfig is required");
        var config = ReadPushNotificationConfig(inner);
        config.TaskId = V03Json.ReqStr(o, "taskId");
        return config;
    }

    // ---- request params ----

    /// <summary><c>tasks/get</c> params.</summary>
    public static JsonObject WriteGetTaskParams(GetTaskRequest request)
    {
        var o = new JsonObject { ["id"] = request.Id };
        if (request.HistoryLength is { } h) o["historyLength"] = h;
        return o;
    }

    /// <summary>Reads <c>tasks/get</c> params. <c>metadata</c> is dropped.</summary>
    public static GetTaskRequest ReadGetTaskParams(JsonNode? node)
    {
        var o = V03Json.Obj(node, "params");
        if (o["metadata"] is not null) V03LossReporter.Drop("tasks/get.metadata");
        return new GetTaskRequest { Id = V03Json.ReqStr(o, "id"), HistoryLength = V03Json.Int(o, "historyLength") };
    }

    /// <summary>Params with only a task id (<c>tasks/cancel</c>, <c>tasks/resubscribe</c>, push config list).</summary>
    public static JsonObject WriteTaskIdParams(string id)
    {
        return new JsonObject { ["id"] = id };
    }

    /// <summary>Reads <c>tasks/cancel</c> params.</summary>
    public static CancelTaskRequest ReadCancelParams(JsonNode? node)
    {
        var o = V03Json.Obj(node, "params");
        return new CancelTaskRequest { Id = V03Json.ReqStr(o, "id"), Metadata = V03Json.Metadata(o) };
    }

    /// <summary>Reads <c>tasks/resubscribe</c> params. <c>metadata</c> is dropped.</summary>
    public static SubscribeToTaskRequest ReadResubscribeParams(JsonNode? node)
    {
        var o = V03Json.Obj(node, "params");
        if (o["metadata"] is not null) V03LossReporter.Drop("tasks/resubscribe.metadata");
        return new SubscribeToTaskRequest { Id = V03Json.ReqStr(o, "id") };
    }

    /// <summary>Params of push config get and delete.</summary>
    public static JsonObject WritePushConfigIdParams(string taskId, string? configId)
    {
        var o = new JsonObject { ["id"] = taskId };
        V03Json.Set(o, "pushNotificationConfigId", configId);
        return o;
    }

    // ---- JSON-RPC envelopes ----

    /// <summary>A JSON-RPC success response around <paramref name="result" />.</summary>
    public static JsonObject WriteSuccess(JsonElement? id, JsonNode? result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["result"] = result
        };
    }

    /// <summary>
    ///     A JSON-RPC error response: the numeric code and message, without the v1.0 <c>data</c> (ErrorInfo) array. The auth
    ///     codes
    ///     have no JSON-RPC number and are sent as Internal, as the v1.0 binding does (they also travel as HTTP 401 and 403).
    /// </summary>
    public static JsonObject WriteError(JsonElement? id, A2AException ex)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["error"] = new JsonObject
            {
                ["code"] = ex.Code is A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied
                    ? (int)A2AErrorCode.Internal
                    : (int)ex.Code,
                ["message"] = ex.Message
            }
        };
    }

    private static JsonNode? IdNode(JsonElement? id)
    {
        return id is { } value ? V03Json.ToNode(value) : null;
    }
}