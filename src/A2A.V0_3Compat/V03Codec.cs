using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat;

/// <summary>
/// Converts between the v1.0 model and one v0.3 wire dialect, as <see cref="JsonNode"/> trees. The two dialects share their
/// structure (messages, tasks, artifacts, statuses, push configs) and differ in a few shapes, which the subclasses supply:
/// <see cref="V03JsonCodec"/> (JSON-RPC, from the v0.3 JSON schema) and <see cref="V03ProtoJsonCodec"/> (HTTP+JSON, from the v0.3 proto).
/// Readers throw <see cref="JsonException"/> on bad input. Writers report what they drop to <see cref="V03LossReporter"/>.
/// </summary>
internal abstract class V03Codec
{
    // ---- dialect hooks ----

    /// <summary>True when message, task and event objects carry a <c>kind</c> tag.</summary>
    protected abstract bool Tagged { get; }

    /// <summary>Name of the member that holds a message's parts.</summary>
    protected abstract string MessagePartsName { get; }

    /// <summary>Name of the member of the send configuration that holds the push config.</summary>
    protected abstract string SendPushConfigName { get; }

    protected abstract string StateName(TaskState state);
    protected abstract TaskState ParseState(string name);
    protected abstract string RoleName(Role role);
    protected abstract Role ParseRole(string name);
    protected abstract JsonObject WritePart(Part part);
    protected abstract Part ReadPart(JsonObject part);

    /// <summary>A stream event (or a push body) as it appears in the dialect.</summary>
    public abstract JsonObject WriteStreamEvent(StreamResponse evt);

    /// <summary>Reads a stream event. <paramref name="final"/> is the <c>final</c> flag of a status update (false for other events).</summary>
    public abstract StreamResponse ReadStreamEvent(JsonNode node, out bool final);

    /// <summary>The result of message send: a task or a message.</summary>
    public abstract JsonObject WriteSendResult(SendMessageResponse response);

    /// <summary>Reads the result of message send.</summary>
    public abstract SendMessageResponse ReadSendResult(JsonNode node);

    /// <summary>A push config as the dialect names it (taskId or name, plus the config).</summary>
    public abstract JsonObject WritePushConfig(TaskPushNotificationConfig config);

    /// <summary>Reads a push config. The task id comes from the dialect's member (taskId or name).</summary>
    public abstract TaskPushNotificationConfig ReadPushConfig(JsonNode node);

    // ---- parts ----

    public JsonObject WritePartNode(Part part) => WritePart(part);

    public Part ReadPartNode(JsonNode? node) => ReadPart(V03Json.Obj(node, "part"));

    // ---- messages ----

    public JsonObject WriteMessage(Message message)
    {
        var o = new JsonObject();
        if (Tagged) o["kind"] = "message";
        o["messageId"] = message.MessageId;
        o["role"] = RoleName(message.Role);
        var parts = new JsonArray();
        foreach (var p in message.Parts) V03Json.Append(parts, WritePart(p));
        o[MessagePartsName] = parts;
        V03Json.Set(o, "contextId", message.ContextId);
        V03Json.Set(o, "taskId", message.TaskId);
        V03Json.SetMetadata(o, message.Metadata);
        if (message.Extensions is { Count: > 0 }) o["extensions"] = V03Json.StrArray(message.Extensions);
        if (message.ReferenceTaskIds is { Count: > 0 })
        {
            if (Tagged) o["referenceTaskIds"] = V03Json.StrArray(message.ReferenceTaskIds);
            else V03LossReporter.Drop("message.referenceTaskIds");
        }
        return o;
    }

    public Message ReadMessage(JsonNode? node)
    {
        var o = V03Json.Obj(node, "message");
        // Both spellings are accepted in both dialects: the proto calls the parts of a message "content".
        var parts = V03Json.List(o, "parts", ReadPartNode) ?? V03Json.List(o, "content", ReadPartNode) ?? [];
        return new Message
        {
            MessageId = V03Json.ReqStr(o, "messageId"),
            Role = ReadRole(o),
            Parts = parts,
            ContextId = V03Json.Str(o, "contextId"),
            TaskId = V03Json.Str(o, "taskId"),
            Metadata = V03Json.Metadata(o),
            Extensions = V03Json.StrList(o, "extensions"),
            ReferenceTaskIds = V03Json.StrList(o, "referenceTaskIds"),
        };
    }

    private Role ReadRole(JsonObject o)
    {
        if (V03Json.Str(o, "role") is { } role) return ParseRole(role);
        return Tagged ? throw new JsonException("role is required") : Role.Unspecified;
    }

    // ---- statuses, artifacts, tasks ----

    public JsonObject WriteStatus(AgentTaskStatus status)
    {
        var o = new JsonObject { ["state"] = StateName(status.State) };
        if (status.Message is { } m) o["message"] = WriteMessage(m);
        if (status.Timestamp is { } t) o["timestamp"] = V03Json.FormatTimestamp(t);
        return o;
    }

    public AgentTaskStatus ReadStatus(JsonNode? node)
    {
        var o = V03Json.Obj(node, "status");
        return new AgentTaskStatus
        {
            State = V03Json.Str(o, "state") is { } s ? ParseState(s) : (Tagged ? throw new JsonException("state is required") : TaskState.Unspecified),
            Message = V03Json.OptObj(o, "message") is { } m ? ReadMessage(m) : null,
            Timestamp = V03Json.Timestamp(o, "timestamp"),
        };
    }

    public JsonObject WriteArtifact(Artifact artifact)
    {
        var o = new JsonObject { ["artifactId"] = artifact.ArtifactId };
        V03Json.Set(o, "name", artifact.Name);
        V03Json.Set(o, "description", artifact.Description);
        var parts = new JsonArray();
        foreach (var p in artifact.Parts) V03Json.Append(parts, WritePart(p));
        o["parts"] = parts;
        V03Json.SetMetadata(o, artifact.Metadata);
        if (artifact.Extensions is { Count: > 0 }) o["extensions"] = V03Json.StrArray(artifact.Extensions);
        return o;
    }

    public Artifact ReadArtifact(JsonNode? node)
    {
        var o = V03Json.Obj(node, "artifact");
        return new Artifact
        {
            ArtifactId = V03Json.ReqStr(o, "artifactId"),
            Name = V03Json.Str(o, "name"),
            Description = V03Json.Str(o, "description"),
            Parts = V03Json.List(o, "parts", ReadPartNode) ?? [],
            Metadata = V03Json.Metadata(o),
            Extensions = V03Json.StrList(o, "extensions"),
        };
    }

    public JsonObject WriteTask(AgentTask task)
    {
        var o = new JsonObject();
        if (Tagged) o["kind"] = "task";
        o["id"] = task.Id;
        if (Tagged || task.ContextId.Length > 0) o["contextId"] = task.ContextId;
        o["status"] = WriteStatus(task.Status);
        if (task.Artifacts is { Count: > 0 } artifacts)
        {
            var array = new JsonArray();
            foreach (var a in artifacts) V03Json.Append(array, WriteArtifact(a));
            o["artifacts"] = array;
        }
        if (task.History is { Count: > 0 } history)
        {
            var array = new JsonArray();
            foreach (var m in history) V03Json.Append(array, WriteMessage(m));
            o["history"] = array;
        }
        V03Json.SetMetadata(o, task.Metadata);
        return o;
    }

    public AgentTask ReadTask(JsonNode? node)
    {
        var o = V03Json.Obj(node, "task");
        return new AgentTask
        {
            Id = V03Json.ReqStr(o, "id"),
            ContextId = V03Json.Str(o, "contextId") ?? string.Empty,
            Status = o["status"] is { } status ? ReadStatus(status) : throw new JsonException("status is required"),
            Artifacts = V03Json.List(o, "artifacts", ReadArtifact),
            History = V03Json.List(o, "history", ReadMessage),
            Metadata = V03Json.Metadata(o),
        };
    }

    // ---- stream event bodies (without the dialect's wrapper) ----

    /// <summary><c>final</c> of a status update: the core closes the stream on terminal and interrupted states.</summary>
    protected static bool IsFinal(TaskState state) => state.IsTerminal() || state.IsInterrupted();

    protected JsonObject WriteStatusUpdateBody(TaskStatusUpdateEvent e)
    {
        var o = new JsonObject();
        if (Tagged) o["kind"] = "status-update";
        o["taskId"] = e.TaskId;
        o["contextId"] = e.ContextId;
        o["status"] = WriteStatus(e.Status);
        var final = IsFinal(e.Status.State);
        if (Tagged || final) o["final"] = final;
        V03Json.SetMetadata(o, e.Metadata);
        return o;
    }

    protected TaskStatusUpdateEvent ReadStatusUpdateBody(JsonObject o, out bool final)
    {
        final = V03Json.Bool(o, "final") ?? false;
        return new TaskStatusUpdateEvent
        {
            TaskId = V03Json.ReqStr(o, "taskId"),
            ContextId = V03Json.Str(o, "contextId") ?? string.Empty,
            Status = o["status"] is { } status ? ReadStatus(status) : throw new JsonException("status is required"),
            Metadata = V03Json.Metadata(o),
        };
    }

    protected JsonObject WriteArtifactUpdateBody(TaskArtifactUpdateEvent e)
    {
        var o = new JsonObject();
        if (Tagged) o["kind"] = "artifact-update";
        o["taskId"] = e.TaskId;
        o["contextId"] = e.ContextId;
        o["artifact"] = WriteArtifact(e.Artifact);
        if (e.Append) o["append"] = true;
        if (e.LastChunk) o["lastChunk"] = true;
        V03Json.SetMetadata(o, e.Metadata);
        return o;
    }

    protected TaskArtifactUpdateEvent ReadArtifactUpdateBody(JsonObject o) => new()
    {
        TaskId = V03Json.ReqStr(o, "taskId"),
        ContextId = V03Json.Str(o, "contextId") ?? string.Empty,
        Artifact = o["artifact"] is { } artifact ? ReadArtifact(artifact) : throw new JsonException("artifact is required"),
        Append = V03Json.Bool(o, "append") ?? false,
        LastChunk = V03Json.Bool(o, "lastChunk") ?? false,
        Metadata = V03Json.Metadata(o),
    };

    // ---- push notification config ----

    /// <summary>The v0.3 <c>PushNotificationConfig</c> object (same in both dialects).</summary>
    protected static JsonObject WritePushNotificationConfig(TaskPushNotificationConfig config)
    {
        var o = new JsonObject();
        V03Json.Set(o, "id", config.Id);
        o["url"] = config.Url;
        V03Json.Set(o, "token", config.Token);
        if (config.Authentication is { } auth)
        {
            var a = new JsonObject { ["schemes"] = V03Json.StrArray([auth.Scheme]) };
            V03Json.Set(a, "credentials", auth.Credentials);
            o["authentication"] = a;
        }
        return o;
    }

    /// <summary>Reads a v0.3 <c>PushNotificationConfig</c>. <c>authentication.schemes</c> beyond the first are dropped.</summary>
    protected static TaskPushNotificationConfig ReadPushNotificationConfig(JsonObject o)
    {
        var config = new TaskPushNotificationConfig
        {
            Id = V03Json.Str(o, "id"),
            Url = V03Json.ReqStr(o, "url"),
            Token = V03Json.Str(o, "token"),
        };
        if (V03Json.OptObj(o, "authentication") is { } auth)
        {
            var schemes = V03Json.StrList(auth, "schemes") ?? [];
            if (schemes.Count > 1) V03LossReporter.Drop("pushNotificationConfig.authentication.schemes");
            config.Authentication = new AuthenticationInfo
            {
                Scheme = schemes.Count > 0 ? schemes[0] : "Bearer",
                Credentials = V03Json.Str(auth, "credentials"),
            };
        }
        return config;
    }

    // ---- send message request ----

    /// <summary>The v0.3 send request: <c>message</c>, <c>configuration</c> and <c>metadata</c>. The tenant is never sent.</summary>
    public JsonObject WriteSendRequest(SendMessageRequest request)
    {
        var o = new JsonObject { ["message"] = WriteMessage(request.Message) };
        var configuration = new JsonObject();
        if (request.Configuration is { } c)
        {
            if (c.AcceptedOutputModes is { Count: > 0 }) configuration["acceptedOutputModes"] = V03Json.StrArray(c.AcceptedOutputModes);
            if (c.HistoryLength is { } h) configuration["historyLength"] = h;
            if (c.TaskPushNotificationConfig is { } push) configuration[SendPushConfigName] = WritePushNotificationConfig(push);
        }
        // Absent means blocking in v0.3; the value is always sent so the intent is clear.
        configuration["blocking"] = request.Configuration?.ReturnImmediately != true;
        o["configuration"] = configuration;
        V03Json.SetMetadata(o, request.Metadata);
        if (!string.IsNullOrEmpty(request.Tenant)) V03LossReporter.Drop("request.tenant");
        return o;
    }

    /// <summary>
    /// Reads a v0.3 send request. <c>blocking: false</c> becomes <c>ReturnImmediately</c>; absent means blocking (not on HTTP+JSON, see <see cref="BlockingWhenAbsent"/>). A push config in
    /// the request is carried in <c>Configuration.TaskPushNotificationConfig</c>: the server compat layer refuses it.
    /// </summary>
    public SendMessageRequest ReadSendRequest(JsonNode? node)
    {
        var o = V03Json.Obj(node, "params");
        var request = new SendMessageRequest
        {
            Message = o["message"] is { } m ? ReadMessage(m) : throw new JsonException("message is required"),
            Metadata = V03Json.Metadata(o),
        };
        if (V03Json.OptObj(o, "configuration") is { } c)
        {
            request.Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = V03Json.StrList(c, "acceptedOutputModes"),
                HistoryLength = V03Json.Int(c, "historyLength"),
                ReturnImmediately = (V03Json.Bool(c, "blocking") ?? BlockingWhenAbsent) == false,
                TaskPushNotificationConfig = V03Json.OptObj(c, SendPushConfigName) is { } push ? ReadPushNotificationConfig(push) : null,
            };
        }
        else if (!BlockingWhenAbsent) request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        return request;
    }

    /// <summary>
    /// What a missing <c>blocking</c> means. JSON-RPC: blocking. HTTP+JSON is ProtoJSON, where a false bool is not written, so
    /// missing means false, like the a2a-sdk 0.3 Python server and client (which never send <c>blocking: false</c>).
    /// </summary>
    protected virtual bool BlockingWhenAbsent => true;

    // ---- shared by the dialects ----

    protected static TaskState ParseStateFrom(string name, IReadOnlyDictionary<string, TaskState> map) =>
        map.TryGetValue(name, out var state) ? state : throw new JsonException($"Unknown task state '{name}'");

    protected static void DropPartExtras(Part part, string defaultMediaType, bool keepFilename, bool keepMediaType)
    {
        if (part.Filename is not null && !keepFilename) V03LossReporter.Drop("part.filename");
        if (part.MediaType is not null && !keepMediaType && part.MediaType != defaultMediaType) V03LossReporter.Drop("part.mediaType");
    }
}
