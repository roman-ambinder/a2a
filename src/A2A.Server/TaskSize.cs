using System.Text;
using System.Text.Json;

namespace A2A.Server;

/// <summary>
/// Estimates how large a task is, without serializing it. Counts the content of the parts of the history and of the
/// artifacts: UTF-8 bytes of text, base64 length of raw bytes, the length of a URL, and the JSON text length of data. Also counts
/// the JSON text length of every metadata value (task, message, artifact, part) and the length of artifact names, descriptions
/// and file names. Ids and media types are not counted.
/// </summary>
internal static class TaskSize
{
    public static long Measure(AgentTask task)
    {
        long total = Measure(task.Metadata);
        if (task.History is { } history)
            foreach (var message in history)
                total += Measure(message.Metadata) + Measure(message.Parts);
        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
            {
                total += Measure(artifact.Metadata) + Measure(artifact.Parts) + (artifact.Name?.Length ?? 0) + (artifact.Description?.Length ?? 0);
            }
        return total;
    }

    /// <summary>The JSON text length of the values of <paramref name="metadata"/>, without the keys the server sets itself (the artifact seal, the time a message was recorded and so on).</summary>
    internal static long Measure(Dictionary<string, JsonElement>? metadata)
    {
        long total = 0;
        if (metadata is null) return total;
        foreach (var (key, value) in metadata)
        {
            if (IsServerKey(key)) continue; // markers the server sets, not the agent's content
            total += value.GetRawText().Length;
        }
        return total;
    }

    private static bool IsServerKey(string key) =>
        key is A2AProtocol.ArtifactSealedKey or A2AProtocol.MessageRecordedAtKey or A2AProtocol.ApprovalConsumedKey
            or A2AProtocol.CancelRequestedKey or A2A.Delegation.ActorChain.MetadataKey or A2A.Elicitations.Elicitation.AnsweredKey;

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
            total += Measure(part.Metadata) + (part.Filename?.Length ?? 0);
        }
        return total;
    }
}
