using System.Buffers;
using System.Text;
using System.Text.Json;
using A2A.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using A2aPb = Lf.A2a.V1;
using Enum = System.Enum;

namespace A2A.Grpc;

/// <summary>
///     Maps the per-message types (stream events and SendMessage responses, with the tasks, timelines, messages, artifacts
///     and parts in them, and, model to proto only, the SendMessage request a client sends)
///     field by field, without going through ProtoJSON. It must give exactly what <see cref="ProtoMapper" />'s ProtoJSON
///     path gives,
///     so it handles only the common shapes and returns null for anything else (an empty required field, an
///     event kind
///     or enum value it does not know, a timestamp finer than the model keeps, ...): the caller then takes the ProtoJSON
///     path, which
///     also produces every error. <c>google.protobuf.Struct</c> and <c>Value</c> (metadata, data parts) still go through
///     ProtoJSON,
///     one value at a time, so numbers and strings come out the same.
/// </summary>
// History is deprecated in the proto but still on the wire, and the model maps it.
#pragma warning disable CS0612
internal static class DirectMapper
{
    private static readonly JsonFormatter
        Formatter = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = A2AJson.Options.Encoder };

    // ---------------------------------------------------------------- helpers

    // The range a model timestamp can hold (DateTimeOffset): years 1 to 9999.
    private static readonly long MinSeconds =
        Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)).Seconds;

    private static readonly long MaxSeconds =
        Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)).Seconds;

    // ---------------------------------------------------------------- proto -> model

    public static StreamResponse? ToModel(A2aPb.StreamResponse p)
    {
        return p.PayloadCase switch
        {
            A2aPb.StreamResponse.PayloadOneofCase.Task => ToTask(p.Task) is { } t ? new TaskEvent(t) : null,
            A2aPb.StreamResponse.PayloadOneofCase.Message => ToMessage(p.Message) is { } m ? new MessageEvent(m) : null,
            A2aPb.StreamResponse.PayloadOneofCase.StatusUpdate => ToStatusUpdate(p.StatusUpdate) is { } s
                ? new StatusUpdateEvent(s)
                : null,
            A2aPb.StreamResponse.PayloadOneofCase.ArtifactUpdate => ToArtifactUpdate(p.ArtifactUpdate) is { } a
                ? new ArtifactUpdateEvent(a)
                : null,
            _ => null
        };
    }

    public static SendMessageResponse? ToModel(A2aPb.SendMessageResponse p)
    {
        return p.PayloadCase switch
        {
            A2aPb.SendMessageResponse.PayloadOneofCase.Task => ToTask(p.Task) is { } t ? new TaskResponse(t) : null,
            A2aPb.SendMessageResponse.PayloadOneofCase.Message => ToMessage(p.Message) is { } m
                ? new MessageResponse(m)
                : null,
            _ => null
        };
    }

    private static AgentTask? ToTask(A2aPb.Task p)
    {
        // Required by the model: missing ones fail on the ProtoJSON path.
        if (p.Id.Length == 0 || p.Status is null) return null;
        if (ToStatus(p.Status) is not { } status) return null;
        List<Artifact>? artifacts = null;
        if (p.Artifacts.Count > 0)
        {
            artifacts = new List<Artifact>(p.Artifacts.Count);
            foreach (var a in p.Artifacts)
            {
                if (ToArtifact(a) is not { } artifact) return null;
                artifacts.Add(artifact);
            }
        }

        List<Message>? history = null;
        if (p.History.Count > 0)
        {
            history = new List<Message>(p.History.Count);
            foreach (var m in p.History)
            {
                if (ToMessage(m) is not { } message) return null;
                history.Add(message);
            }
        }

        List<TimelineEntry>? timeline = null;
        if (p.Timeline.Count > 0)
        {
            timeline = new List<TimelineEntry>(p.Timeline.Count);
            foreach (var e in p.Timeline)
            {
                if (ToTimelineEntry(e) is not { } entry) return null;
                timeline.Add(entry);
            }
        }

        if (!TryMetadata(p.Metadata, out var metadata)) return null;
        return new AgentTask
        {
            Id = p.Id,
            ContextId = p.ContextId,
            Status = status,
            Artifacts = artifacts,
            History = history,
            Metadata = metadata,
            Generation = Optional(p.Generation),
            Timeline = timeline
        };
    }

    private static TimelineEntry? ToTimelineEntry(A2aPb.TimelineEntry p)
    {
        switch (p.EntryCase)
        {
            case A2aPb.TimelineEntry.EntryOneofCase.Message:
                return ToMessage(p.Message) is { } message
                    ? new TimelineEntry { Message = message, Generation = p.Generation }
                    : null;
            case A2aPb.TimelineEntry.EntryOneofCase.Status:
                return ToStatus(p.Status) is { } status
                    ? new TimelineEntry { Status = status, Generation = p.Generation }
                    : null;
            case A2aPb.TimelineEntry.EntryOneofCase.None:
                // No entry kind (or one this SDK's proto does not know): an entry with neither, as on the text path.
                return new TimelineEntry { Generation = p.Generation };
            default:
                return null;
        }
    }

    private static AgentTaskStatus? ToStatus(A2aPb.TaskStatus p)
    {
        Message? message = null;
        if (p.Message is not null && (message = ToMessage(p.Message)) is null) return null;
        DateTimeOffset? timestamp = null;
        if (p.Timestamp is { } ts)
        {
            // The model keeps 100 ns; ProtoJSON text finer than that is left to the text path.
            if (ts.Nanos % 100 != 0 || ts.Seconds < MinSeconds || ts.Seconds > MaxSeconds) return null;
            timestamp = ts.ToDateTimeOffset();
        }

        return new AgentTaskStatus { State = (TaskState)(int)p.State, Message = message, Timestamp = timestamp };
    }

    private static Message? ToMessage(A2aPb.Message p)
    {
        if (p.MessageId.Length == 0 || p.Role == A2aPb.Role.Unspecified || p.Parts.Count == 0) return null;
        if (ToParts(p.Parts) is not { } parts || !TryMetadata(p.Metadata, out var metadata)) return null;
        return new Message
        {
            MessageId = p.MessageId,
            ContextId = NullIfEmpty(p.ContextId),
            TaskId = NullIfEmpty(p.TaskId),
            Role = (Role)(int)p.Role,
            Parts = parts,
            Metadata = metadata,
            Extensions = p.Extensions.Count > 0 ? [.. p.Extensions] : null,
            ReferenceTaskIds = p.ReferenceTaskIds.Count > 0 ? [.. p.ReferenceTaskIds] : null
        };
    }

    private static Artifact? ToArtifact(A2aPb.Artifact p)
    {
        if (p.ArtifactId.Length == 0) return null;
        if (ToParts(p.Parts) is not { } parts || !TryMetadata(p.Metadata, out var metadata)) return null;
        return new Artifact
        {
            ArtifactId = p.ArtifactId,
            Name = NullIfEmpty(p.Name),
            Description = NullIfEmpty(p.Description),
            Parts = parts,
            Metadata = metadata,
            Extensions = p.Extensions.Count > 0 ? [.. p.Extensions] : null,
            StartGeneration = Optional(p.StartGeneration),
            EndGeneration = Optional(p.EndGeneration),
            Role = p.Role == A2aPb.Role.Unspecified ? null : (Role)(int)p.Role
        };
    }

    private static TaskStatusUpdateEvent? ToStatusUpdate(A2aPb.TaskStatusUpdateEvent p)
    {
        // No status reads as the model's default status, as on the text path.
        var status = p.Status is null ? new AgentTaskStatus() : ToStatus(p.Status);
        if (status is null || !TryMetadata(p.Metadata, out var metadata)) return null;
        return new TaskStatusUpdateEvent
        {
            TaskId = p.TaskId, ContextId = p.ContextId, Status = status, Metadata = metadata,
            Generation = Optional(p.Generation)
        };
    }

    private static TaskArtifactUpdateEvent? ToArtifactUpdate(A2aPb.TaskArtifactUpdateEvent p)
    {
        if (p.Artifact is null || ToArtifact(p.Artifact) is not { } artifact ||
            !TryMetadata(p.Metadata, out var metadata)) return null;
        return new TaskArtifactUpdateEvent
        {
            TaskId = p.TaskId,
            ContextId = p.ContextId,
            Artifact = artifact,
            Append = p.Append,
            LastChunk = p.LastChunk,
            Metadata = metadata,
            Generation = Optional(p.Generation)
        };
    }

    private static List<Part>? ToParts(RepeatedField<A2aPb.Part> parts)
    {
        var list = new List<Part>(parts.Count);
        foreach (var p in parts)
        {
            if (ToPart(p) is not { } part) return null;
            list.Add(part);
        }

        return list;
    }

    private static Part? ToPart(A2aPb.Part p)
    {
        if (!TryMetadata(p.Metadata, out var metadata)) return null;
        var filename = NullIfEmpty(p.Filename);
        var mediaType = NullIfEmpty(p.MediaType);
        switch (p.ContentCase)
        {
            case A2aPb.Part.ContentOneofCase.Text:
                return new TextPart(p.Text) { Metadata = metadata, Filename = filename, MediaType = mediaType };
            case A2aPb.Part.ContentOneofCase.Raw:
                return new RawPart(p.Raw.ToByteArray())
                    { Metadata = metadata, Filename = filename, MediaType = mediaType };
            case A2aPb.Part.ContentOneofCase.Url:
                // The same check as the part converter.
                return Uri.TryCreate(p.Url, UriKind.RelativeOrAbsolute, out var uri)
                    ? new UrlPart(uri) { Metadata = metadata, Filename = filename, MediaType = mediaType }
                    : null;
            case A2aPb.Part.ContentOneofCase.Data:
                // A null value reads as an absent member on the text path (and then as a part with no content).
                if (p.Data is null || p.Data.KindCase is Value.KindOneofCase.NullValue or Value.KindOneofCase.None)
                    return null;
                return ToElement(Formatter.Format(p.Data)) is { } data
                    ? new DataPart(data) { Metadata = metadata, Filename = filename, MediaType = mediaType }
                    : null;
            default:
                return null;
        }
    }

    /// <summary>A Struct as the model's metadata, read from its ProtoJSON text. False when the text path must decide.</summary>
    private static bool TryMetadata(Struct? s, out Dictionary<string, JsonElement>? metadata)
    {
        metadata = null;
        if (s is null) return true;
        try
        {
            using var doc = JsonDocument.Parse(Formatter.Format(s));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            metadata = new Dictionary<string, JsonElement>(s.Fields.Count);
            foreach (var property in doc.RootElement.EnumerateObject())
                metadata[property.Name] = property.Value.Clone();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonElement? ToElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- model -> proto

    public static A2aPb.StreamResponse? ToProto(StreamResponse m)
    {
        switch (m)
        {
            case TaskEvent { Task: { } task }
                when FromTask(task) is { } t: return new A2aPb.StreamResponse { Task = t };
            case MessageEvent { Message: { } message }
                when FromMessage(message) is { } x: return new A2aPb.StreamResponse { Message = x };
            case StatusUpdateEvent { StatusUpdate: { } su }
                when FromStatusUpdate(su) is { } s: return new A2aPb.StreamResponse { StatusUpdate = s };
            case ArtifactUpdateEvent { ArtifactUpdate: { } au }
                when FromArtifactUpdate(au) is { } a: return new A2aPb.StreamResponse { ArtifactUpdate = a };
            default: return null;
        }
    }

    public static A2aPb.SendMessageResponse? ToProto(SendMessageResponse m)
    {
        switch (m)
        {
            case TaskResponse { Task: { } task }
                when FromTask(task) is { } t: return new A2aPb.SendMessageResponse { Task = t };
            case MessageResponse { Message: { } message }
                when FromMessage(message) is { } x: return new A2aPb.SendMessageResponse { Message = x };
            default: return null;
        }
    }

    /// <summary>A SendMessage request as a client sends it (the server reads requests through ProtoJSON, with its depth limit).</summary>
    public static A2aPb.SendMessageRequest? ToProto(SendMessageRequest r)
    {
        var p = new A2aPb.SendMessageRequest { Tenant = r.Tenant ?? "" };
        if (r.Message is not null)
        {
            if (FromMessage(r.Message) is not { } message) return null;
            p.Message = message;
        }

        if (r.Configuration is not null)
        {
            if (FromConfiguration(r.Configuration) is not { } configuration) return null;
            p.Configuration = configuration;
        }

        if (r.ArtifactUpdates is not null)
            foreach (var u in r.ArtifactUpdates)
            {
                if (u is null || FromArtifactUpdate(u) is not { } update) return null;
                p.ArtifactUpdates.Add(update);
            }

        if (!TryStruct(r.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static A2aPb.SendMessageConfiguration? FromConfiguration(SendMessageConfiguration c)
    {
        var p = new A2aPb.SendMessageConfiguration { ReturnImmediately = c.ReturnImmediately };
        if (!AddStrings(p.AcceptedOutputModes, c.AcceptedOutputModes)) return null;
        // The optional fields: null is unset, any value (0 too) is set, as ProtoJSON reads a member that is there.
        if (c.HistoryLength is { } history) p.HistoryLength = history;
        if (c.TimelineLength is { } timeline) p.TimelineLength = timeline;
        if (c.IfGenerationMatch is { } generation) p.IfGenerationMatch = generation;
        if (c.TaskPushNotificationConfig is { } push)
        {
            p.TaskPushNotificationConfig = new A2aPb.TaskPushNotificationConfig
            {
                Tenant = push.Tenant ?? "", Id = push.Id ?? "", TaskId = push.TaskId ?? "", Url = push.Url ?? "",
                Token = push.Token ?? "", ProtocolVersion = push.ProtocolVersion ?? ""
            };
            if (push.Authentication is { } auth)
                p.TaskPushNotificationConfig.Authentication = new A2aPb.AuthenticationInfo
                    { Scheme = auth.Scheme ?? "", Credentials = auth.Credentials ?? "" };
        }

        return p;
    }

    private static A2aPb.Task? FromTask(AgentTask t)
    {
        var p = new A2aPb.Task { Id = t.Id ?? "", ContextId = t.ContextId ?? "", Generation = t.Generation ?? 0 };
        if (t.Status is not null)
        {
            if (FromStatus(t.Status) is not { } status) return null;
            p.Status = status;
        }

        if (t.Artifacts is not null)
            foreach (var a in t.Artifacts)
            {
                if (a is null || FromArtifact(a) is not { } artifact) return null;
                p.Artifacts.Add(artifact);
            }

        if (t.History is not null)
            foreach (var m in t.History)
            {
                if (m is null || FromMessage(m) is not { } message) return null;
                p.History.Add(message);
            }

        if (t.Timeline is not null)
            foreach (var e in t.Timeline)
            {
                if (e is null || FromTimelineEntry(e) is not { } entry) return null;
                p.Timeline.Add(entry);
            }

        if (!TryStruct(t.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static A2aPb.TimelineEntry? FromTimelineEntry(TimelineEntry e)
    {
        var p = new A2aPb.TimelineEntry { Generation = e.Generation };
        switch (e)
        {
            // Both set is two members of the oneof: the text path decides (it refuses).
            case { Message: not null, Status: not null }:
                return null;
            case { Message: { } m }:
                if (FromMessage(m) is not { } message) return null;
                p.Message = message;
                break;
            case { Status: { } s }:
                if (FromStatus(s) is not { } status) return null;
                p.Status = status;
                break;
        }

        return p;
    }

    private static A2aPb.TaskStatus? FromStatus(AgentTaskStatus s)
    {
        if (!Enum.IsDefined(s.State)) return null;
        var p = new A2aPb.TaskStatus { State = (A2aPb.TaskState)(int)s.State };
        if (s.Message is not null)
        {
            if (FromMessage(s.Message) is not { } message) return null;
            p.Message = message;
        }

        if (s.Timestamp is { } at)
        {
            // The model's JSON keeps milliseconds (TimestampConverter), so the proto gets the time cut to milliseconds.
            var ticks = at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMillisecond;
            p.Timestamp = Timestamp.FromDateTime(new DateTime(ticks, DateTimeKind.Utc));
        }

        return p;
    }

    private static A2aPb.Message? FromMessage(Message m)
    {
        if (!Enum.IsDefined(m.Role)) return null;
        var p = new A2aPb.Message
        {
            MessageId = m.MessageId ?? "", ContextId = m.ContextId ?? "", TaskId = m.TaskId ?? "",
            Role = (A2aPb.Role)(int)m.Role
        };
        if (!AddParts(p.Parts, m.Parts) || !AddStrings(p.Extensions, m.Extensions) ||
            !AddStrings(p.ReferenceTaskIds, m.ReferenceTaskIds)) return null;
        if (!TryStruct(m.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static A2aPb.Artifact? FromArtifact(Artifact a)
    {
        if (a.Role is { } role && !Enum.IsDefined(role)) return null;
        var p = new A2aPb.Artifact
        {
            ArtifactId = a.ArtifactId ?? "",
            Name = a.Name ?? "",
            Description = a.Description ?? "",
            StartGeneration = a.StartGeneration ?? 0,
            EndGeneration = a.EndGeneration ?? 0,
            Role = a.Role is { } r ? (A2aPb.Role)(int)r : A2aPb.Role.Unspecified
        };
        if (!AddParts(p.Parts, a.Parts) || !AddStrings(p.Extensions, a.Extensions)) return null;
        if (!TryStruct(a.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static A2aPb.TaskStatusUpdateEvent? FromStatusUpdate(TaskStatusUpdateEvent e)
    {
        var p = new A2aPb.TaskStatusUpdateEvent
            { TaskId = e.TaskId ?? "", ContextId = e.ContextId ?? "", Generation = e.Generation ?? 0 };
        if (e.Status is not null)
        {
            if (FromStatus(e.Status) is not { } status) return null;
            p.Status = status;
        }

        if (!TryStruct(e.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static A2aPb.TaskArtifactUpdateEvent? FromArtifactUpdate(TaskArtifactUpdateEvent e)
    {
        var p = new A2aPb.TaskArtifactUpdateEvent
        {
            TaskId = e.TaskId ?? "", ContextId = e.ContextId ?? "", Append = e.Append, LastChunk = e.LastChunk,
            Generation = e.Generation ?? 0
        };
        if (e.Artifact is not null)
        {
            if (FromArtifact(e.Artifact) is not { } artifact) return null;
            p.Artifact = artifact;
        }

        if (!TryStruct(e.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    private static bool AddParts(RepeatedField<A2aPb.Part> target, List<Part>? parts)
    {
        if (parts is null) return true;
        foreach (var part in parts)
        {
            if (part is null || FromPart(part) is not { } p) return false;
            target.Add(p);
        }

        return true;
    }

    private static bool AddStrings(RepeatedField<string> target, List<string>? values)
    {
        if (values is null) return true;
        foreach (var v in values)
        {
            if (v is null) return false; // a null item: the text path decides
            target.Add(v);
        }

        return true;
    }

    private static A2aPb.Part? FromPart(Part part)
    {
        var p = new A2aPb.Part { Filename = part.Filename ?? "", MediaType = part.MediaType ?? "" };
        switch (part)
        {
            case TextPart { Text: { } text }:
                p.Text = text;
                break;
            case RawPart r:
                p.Raw = ByteString.CopyFrom(r.Raw.Span);
                break;
            case UrlPart { Url: { } url }:
                p.Url = url.OriginalString;
                break;
            case DataPart d when d.Data.ValueKind != JsonValueKind.Undefined:
                if (ToValue(d.Data) is not { } value) return null;
                p.Data = value;
                break;
            default:
                return null;
        }

        if (!TryStruct(part.Metadata, out var metadata)) return null;
        p.Metadata = metadata;
        return p;
    }

    /// <summary>
    ///     Model metadata as a Struct, parsed from its JSON text as the ProtoJSON path does. False when that path must
    ///     decide.
    /// </summary>
    private static bool TryStruct(Dictionary<string, JsonElement>? metadata, out Struct? result)
    {
        result = null;
        if (metadata is null) return true;
        var buffer = new ArrayBufferWriter<byte>(256);
        try
        {
            using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            {
                w.WriteStartObject();
                foreach (var (key, value) in metadata)
                {
                    if (key is null || value.ValueKind == JsonValueKind.Undefined) return false;
                    w.WritePropertyName(key);
                    value.WriteTo(w);
                }

                w.WriteEndObject();
            }

            result = Parser.Parse<Struct>(Encoding.UTF8.GetString(buffer.WrittenSpan));
            return true;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    private static Value? ToValue(JsonElement data)
    {
        try
        {
            return Parser.Parse<Value>(data.GetRawText());
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }

    /// <summary>An int64 field that ProtoJSON omits at 0, which the model then reads as null.</summary>
    private static long? Optional(long value)
    {
        return value == 0 ? null : value;
    }
}
#pragma warning restore CS0612