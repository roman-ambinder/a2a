using System.Text;
using System.Text.Json;

namespace A2A.Server;

/// <summary>
/// Estimates how large a task is, without serializing it. Counts the content of the parts of the history and of the
/// artifacts: UTF-8 bytes of text, base64 length of raw bytes, the length of a URL, and the JSON text length of data. Also counts
/// the length of every metadata key and the JSON text length of every metadata value (task, message, artifact, part), and the length of artifact names, descriptions
/// and file names. Ids and media types are not counted. Keys the server overwrites or owns are skipped only at the level where it does (the seal on artifacts, the recorded-at time on messages, the approval, cancel, answered and usage markers on the task).
/// </summary>
internal static class TaskSize
{
    public static long Measure(AgentTask task)
    {
        long total = Measure(task.Metadata, Level.Task);
        if (task.History is { } history)
            foreach (var message in history)
                total += Measure(message.Metadata, Level.Message) + Measure(message.Parts);
        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
            {
                total += Measure(artifact.Metadata, Level.Artifact) + Measure(artifact.Parts) + (artifact.Name?.Length ?? 0) + (artifact.Description?.Length ?? 0);
            }
        return total;
    }

    /// <summary>
    /// The bytes a stored event adds to the measured size of the task, when that is exactly the size of the parts it carries: an
    /// append to an artifact that changes nothing else (no artifact name, description or metadata, no event metadata). Then
    /// <c>Measure(after) == Measure(before) + AppendedBytes(event)</c>, and the caller need not walk the whole task again. Null
    /// for every other event (the caller measures the task).
    /// </summary>
    public static long? AppendedBytes(StreamResponse evt) =>
        evt is ArtifactUpdateEvent { ArtifactUpdate: { Append: true, Metadata: null or { Count: 0 }, Artifact: { Name: null, Description: null, Metadata: null or { Count: 0 } } artifact } }
            ? Measure(artifact.Parts)
            : null;

    /// <summary>
    /// The length of the key names and the JSON text length of the values of <paramref name="metadata"/>. Only a key the server
    /// overwrites or owns at that level is skipped (<paramref name="level"/>); the same key anywhere else is the caller's content.
    /// </summary>
    private static long Measure(Dictionary<string, JsonElement>? metadata, Level level)
    {
        long total = 0;
        if (metadata is null) return total;
        foreach (var (key, value) in metadata)
        {
            if (IsServerKey(key, level)) continue;
            total += key.Length + value.GetRawText().Length;
        }
        return total;
    }

    private enum Level { Task, Message, Artifact, Part }

    private static bool IsServerKey(string key, Level level) => level switch
    {
        Level.Message => key == A2AProtocol.MessageRecordedAtKey, // the server stamps it over whatever the caller sent
        Level.Artifact => key == A2AProtocol.ArtifactSealedKey, // only the server sets it
        Level.Task => key is A2AProtocol.ApprovalConsumedKey or A2AProtocol.CancelRequestedKey or A2A.Elicitations.Elicitation.AnsweredKey
            or A2ARequestHandler.PendingStartKey or Execution.TaskUsageMeter.MetadataKey,
        _ => false,
    };

    private static long Measure(List<Part> parts)
    {
        long total = 0;
        foreach (var part in parts)
        {
            total += part switch
            {
                TextPart text => Encoding.UTF8.GetByteCount(text.Text),
                RawPart raw => ((long)raw.Raw.Length + 2) / 3 * 4,
                UrlPart url => url.Url.OriginalString.Length,
                DataPart data => data.Data.GetRawText().Length,
                _ => 0,
            };
            total += Measure(part.Metadata, Level.Part) + (part.Filename?.Length ?? 0);
        }
        return total;
    }
}
