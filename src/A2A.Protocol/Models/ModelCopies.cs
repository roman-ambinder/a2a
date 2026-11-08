using System.Text.Json;

namespace A2A;

/// <summary>
/// Structural copies of the task models. They replace JSON round-trip cloning: the cost depends on the
/// copy depth you choose, not on serialization.
/// </summary>
public static class ModelCopies
{
    /// <summary>
    /// Copies a task for one write. The copy has a new task, status, <c>History</c> list, <c>Artifacts</c> list
    /// and <c>Metadata</c> dictionary. It shares the <see cref="Message"/> and <see cref="Artifact"/> instances
    /// (and the status message) with the original: callers must not mutate them. Null collections stay null.
    /// </summary>
    public static AgentTask CopyForUpdate(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = CopyStatus(task.Status, deep: false),
            Artifacts = task.Artifacts is null ? null : new List<Artifact>(task.Artifacts),
            History = task.History is null ? null : new List<Message>(task.History),
            Metadata = CopyMap(task.Metadata, clone: false),
        };
    }

    /// <summary>
    /// Copies an artifact with <paramref name="extraParts"/> added after the existing parts. The copy has a new
    /// <c>Parts</c> list, a new <c>Metadata</c> dictionary and a new <c>Extensions</c> list. The existing
    /// <see cref="Part"/> instances are shared. The original is not changed.
    /// </summary>
    public static Artifact CopyWithParts(this Artifact artifact, IEnumerable<Part> extraParts)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(extraParts);
        var parts = new List<Part>(artifact.Parts);
        parts.AddRange(extraParts);
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Description = artifact.Description,
            Parts = parts,
            Metadata = CopyMap(artifact.Metadata, clone: false),
            Extensions = artifact.Extensions is null ? null : new List<string>(artifact.Extensions),
        };
    }

    /// <summary>
    /// Copies everything reachable from a task. Every message, artifact, part, list and dictionary is new,
    /// <see cref="JsonElement"/> values are cloned, and raw bytes are copied. Only immutable values
    /// (strings, URIs, enums, numbers) are shared. Serializes to the same JSON as the original.
    /// </summary>
    public static AgentTask DeepCopy(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new AgentTask
        {
            Id = task.Id,
            ContextId = task.ContextId,
            Status = CopyStatus(task.Status, deep: true),
            Artifacts = task.Artifacts?.ConvertAll(DeepArtifact),
            History = task.History?.ConvertAll(DeepMessage),
            Metadata = CopyMap(task.Metadata, clone: true),
        };
    }

    /// <summary>
    /// Copies everything reachable from a message: the parts, lists and dictionaries are new, <see cref="JsonElement"/>
    /// values are cloned and raw bytes are copied. Only immutable values are shared. Serializes to the same JSON as the
    /// original. This is the copy <see cref="DeepCopy(AgentTask)"/> makes of each history message.
    /// </summary>
    public static Message DeepCopy(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return DeepMessage(message);
    }

    /// <summary>
    /// Copies a push notification config and its <see cref="TaskPushNotificationConfig.Authentication"/>. Every value is a
    /// string, so the copy shares nothing mutable with the original. Serializes to the same JSON as the original.
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
        };
    }

    /// <summary>
    /// Copies a stream event and everything reachable from it, whichever of the four kinds it is: a task snapshot
    /// (<see cref="DeepCopy(AgentTask)"/>), a message (<see cref="DeepCopy(Message)"/>), a status update (new status, status
    /// message and metadata) or an artifact update (new artifact, parts and metadata). Only immutable values are shared.
    /// Serializes to the same JSON as the original.
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
                Status = CopyStatus(s.StatusUpdate.Status, deep: true),
                Metadata = CopyMap(s.StatusUpdate.Metadata, clone: true),
            }),
            ArtifactUpdateEvent a => new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
            {
                TaskId = a.ArtifactUpdate.TaskId,
                ContextId = a.ArtifactUpdate.ContextId,
                Artifact = DeepArtifact(a.ArtifactUpdate.Artifact),
                Append = a.ArtifactUpdate.Append,
                LastChunk = a.ArtifactUpdate.LastChunk,
                Metadata = CopyMap(a.ArtifactUpdate.Metadata, clone: true),
            }),
            _ => throw new NotSupportedException($"Unknown stream event type {response.GetType().Name}."),
        };
    }

    private static AgentTaskStatus CopyStatus(AgentTaskStatus s, bool deep) => new()
    {
        State = s.State,
        Message = deep && s.Message is not null ? DeepMessage(s.Message) : s.Message,
        Timestamp = s.Timestamp,
    };

    private static Message DeepMessage(Message m) => new()
    {
        MessageId = m.MessageId,
        ContextId = m.ContextId,
        TaskId = m.TaskId,
        Role = m.Role,
        Parts = m.Parts.ConvertAll(DeepPart),
        Metadata = CopyMap(m.Metadata, clone: true),
        Extensions = m.Extensions is null ? null : new List<string>(m.Extensions),
        ReferenceTaskIds = m.ReferenceTaskIds is null ? null : new List<string>(m.ReferenceTaskIds),
    };

    private static Artifact DeepArtifact(Artifact a) => new()
    {
        ArtifactId = a.ArtifactId,
        Name = a.Name,
        Description = a.Description,
        Parts = a.Parts.ConvertAll(DeepPart),
        Metadata = CopyMap(a.Metadata, clone: true),
        Extensions = a.Extensions is null ? null : new List<string>(a.Extensions),
    };

    private static Part DeepPart(Part p)
    {
        var metadata = CopyMap(p.Metadata, clone: true);
        return p switch
        {
            TextPart t => t with { Metadata = metadata },
            RawPart r => r with { Raw = r.Raw.ToArray(), Metadata = metadata },
            UrlPart u => u with { Metadata = metadata },
            DataPart d => d with { Data = d.Data.Clone(), Metadata = metadata },
            _ => throw new NotSupportedException($"Unknown part type {p.GetType().Name}."),
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
