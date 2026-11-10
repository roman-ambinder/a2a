using System.Text.Json;

namespace A2A;

/// <summary>
///     Structural copies of the task models. They replace JSON round-trip cloning: the cost depends on the
///     copy depth you choose, not on serialization.
/// </summary>
public static class ModelCopies
{
    /// <summary>
    ///     Copies a task for one write. The copy has a new task, status, <c>History</c> list, <c>Timeline</c> list,
    ///     <c>Artifacts</c>
    ///     list and <c>Metadata</c> dictionary. It shares the <see cref="Message" />, <see cref="Artifact" /> and
    ///     <see cref="TimelineEntry" /> instances
    ///     (and the status message) with the original: callers must not mutate them. Null collections stay null.
    /// </summary>
    public static AgentTask CopyForUpdate(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = CopyStatus(task.Status, false),
            Artifacts = task.Artifacts is null ? null : new List<Artifact>(task.Artifacts),
            History = task.History is null ? null : new List<Message>(task.History),
            Metadata = CopyMap(task.Metadata, false),
            Generation = task.Generation,
            Timeline = task.Timeline is null ? null : new List<TimelineEntry>(task.Timeline)
        };
    }

    /// <summary>
    ///     Copies an artifact with <paramref name="extraParts" /> added after the existing parts. The copy has a new
    ///     <c>Parts</c> list, a new <c>Metadata</c> dictionary and a new <c>Extensions</c> list. The existing
    ///     <see cref="Part" /> instances are shared. The original is not changed.
    /// </summary>
    public static Artifact CopyWithParts(this Artifact artifact, IEnumerable<Part> extraParts)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(extraParts);
        // Sized once: a plain copy followed by AddRange would grow (copy) the list a second time.
        var extra = extraParts as ICollection<Part>;
        var parts = new List<Part>(artifact.Parts.Count + (extra?.Count ?? 4));
        parts.AddRange(artifact.Parts);
        parts.AddRange(extraParts);
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Description = artifact.Description,
            Parts = parts,
            Metadata = CopyMap(artifact.Metadata, false),
            Extensions = artifact.Extensions is null ? null : new List<string>(artifact.Extensions),
            StartGeneration = artifact.StartGeneration,
            EndGeneration = artifact.EndGeneration,
            Role = artifact.Role
        };
    }

    /// <summary>
    ///     Copies everything reachable from a task. Every message, artifact, part, list and dictionary is new,
    ///     <see cref="JsonElement" /> values are cloned, and raw bytes are copied. Only immutable values
    ///     (strings, URIs, enums, numbers) are shared. Serializes to the same JSON as the original.
    /// </summary>
    public static AgentTask DeepCopy(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = CopyStatus(task.Status, true),
            Artifacts = task.Artifacts?.ConvertAll(DeepArtifact),
            History = task.History?.ConvertAll(DeepMessage),
            Metadata = CopyMap(task.Metadata, true),
            Generation = task.Generation,
            Timeline = task.Timeline?.ConvertAll(DeepTimelineEntry)
        };
    }

    /// <summary>
    ///     Copies everything reachable from a message: the parts, lists and dictionaries are new, <see cref="JsonElement" />
    ///     values are cloned and raw bytes are copied. Only immutable values are shared. Serializes to the same JSON as the
    ///     original. This is the copy <see cref="DeepCopy(AgentTask)" /> makes of each history message.
    /// </summary>
    public static Message DeepCopy(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DeepMessage(message);
    }

    /// <summary>
    ///     Copies a push notification config and its <see cref="TaskPushNotificationConfig.Authentication" />. Every value is
    ///     a
    ///     string, so the copy shares nothing mutable with the original. Serializes to the same JSON as the original.
    /// </summary>
    public static TaskPushNotificationConfig DeepCopy(this TaskPushNotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TaskPushNotificationConfig
        {
            Tenant = config.Tenant,
            Id = config.Id,
            TaskId = config.TaskId,
            Url = config.Url,
            Token = config.Token,
            Authentication = config.Authentication is { } auth
                ? new AuthenticationInfo { Scheme = auth.Scheme, Credentials = auth.Credentials }
                : null,
            ProtocolVersion = config.ProtocolVersion
        };
    }

    /// <summary>
    ///     Copies a stream event and everything reachable from it, whichever kind it is: a task snapshot
    ///     (<see cref="DeepCopy(AgentTask)" />), a message (<see cref="DeepCopy(Message)" />), a status update (new status,
    ///     status
    ///     message and metadata), an artifact update (new artifact, parts and metadata), or one of the A2A 1.1 kinds (message
    ///     update,
    ///     stream error). Only immutable values are shared.
    ///     Serializes to the same JSON as the original.
    /// </summary>
    public static StreamResponse DeepCopy(this StreamResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response switch
        {
            TaskEvent t => new TaskEvent(t.Task.DeepCopy()),
            MessageEvent m => new MessageEvent(DeepMessage(m.Message)),
            StatusUpdateEvent s => new StatusUpdateEvent(new TaskStatusUpdateEvent
            {
                TaskId = s.StatusUpdate.TaskId,
                ContextId = s.StatusUpdate.ContextId,
                Status = CopyStatus(s.StatusUpdate.Status, true),
                Metadata = CopyMap(s.StatusUpdate.Metadata, true),
                Generation = s.StatusUpdate.Generation
            }),
            ArtifactUpdateEvent a => new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
            {
                TaskId = a.ArtifactUpdate.TaskId,
                ContextId = a.ArtifactUpdate.ContextId,
                Artifact = DeepArtifact(a.ArtifactUpdate.Artifact),
                Append = a.ArtifactUpdate.Append,
                LastChunk = a.ArtifactUpdate.LastChunk,
                Metadata = CopyMap(a.ArtifactUpdate.Metadata, true),
                Generation = a.ArtifactUpdate.Generation
            }),
            MessageUpdateEvent u => new MessageUpdateEvent(new TaskMessageUpdateEvent
            {
                TaskId = u.MessageUpdate.TaskId,
                ContextId = u.MessageUpdate.ContextId,
                Message = DeepMessage(u.MessageUpdate.Message),
                Metadata = CopyMap(u.MessageUpdate.Metadata, true),
                Generation = u.MessageUpdate.Generation
            }),
            StreamErrorEvent e => new StreamErrorEvent(new StreamError
            {
                TaskId = e.Error.TaskId,
                Code = e.Error.Code,
                Message = e.Error.Message,
                ReferenceId = e.Error.ReferenceId,
                Metadata = CopyMap(e.Error.Metadata, true)
            }),
            _ => throw new NotSupportedException($"Unknown stream event type {response.GetType().Name}.")
        };
    }

    private static AgentTaskStatus CopyStatus(AgentTaskStatus s, bool deep)
    {
        return new AgentTaskStatus
        {
            State = s.State,
            Message = deep && s.Message is not null ? DeepMessage(s.Message) : s.Message,
            Timestamp = s.Timestamp
        };
    }

    private static Message DeepMessage(Message m)
    {
        return new Message
        {
            MessageId = m.MessageId,
            ContextId = m.ContextId,
            TaskId = m.TaskId,
            Role = m.Role,
            Parts = m.Parts.ConvertAll(DeepPart),
            Metadata = CopyMap(m.Metadata, true),
            Extensions = m.Extensions is null ? null : new List<string>(m.Extensions),
            ReferenceTaskIds = m.ReferenceTaskIds is null ? null : new List<string>(m.ReferenceTaskIds)
        };
    }

    private static Artifact DeepArtifact(Artifact a)
    {
        return new Artifact
        {
            ArtifactId = a.ArtifactId,
            Name = a.Name,
            Description = a.Description,
            Parts = a.Parts.ConvertAll(DeepPart),
            Metadata = CopyMap(a.Metadata, true),
            Extensions = a.Extensions is null ? null : new List<string>(a.Extensions),
            StartGeneration = a.StartGeneration,
            EndGeneration = a.EndGeneration,
            Role = a.Role
        };
    }

    private static TimelineEntry DeepTimelineEntry(TimelineEntry e)
    {
        return new TimelineEntry
        {
            Message = e.Message is null ? null : DeepMessage(e.Message),
            Status = e.Status is null ? null : CopyStatus(e.Status, true),
            Generation = e.Generation
        };
    }

    private static Part DeepPart(Part p)
    {
        var metadata = CopyMap(p.Metadata, true);
        return p switch
        {
            TextPart t => t with { Metadata = metadata },
            RawPart r => r with { Raw = r.Raw.ToArray(), Metadata = metadata },
            UrlPart u => u with { Metadata = metadata },
            DataPart d => d with { Data = d.Data.Clone(), Metadata = metadata },
            _ => throw new NotSupportedException($"Unknown part type {p.GetType().Name}.")
        };
    }

    private static Dictionary<string, JsonElement>? CopyMap(Dictionary<string, JsonElement>? map, bool clone)
    {
        if (map is null) return null;
        var copy = new Dictionary<string, JsonElement>(map.Count, map.Comparer);
        foreach (var (k, v) in map) copy[k] = clone ? v.Clone() : v;
        return copy;
    }
}