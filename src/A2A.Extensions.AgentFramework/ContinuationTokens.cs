using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>
///     Where a background run stands: the remote task, how many parts of each artifact the caller has had, and the
///     status message it has had. The server appends artifact chunks as parts, so a count is an exact resume point.
/// </summary>
internal sealed record ContinuationState(string TaskId, IReadOnlyDictionary<string, int> Artifacts, string? StatusMessageId)
{
    internal static ContinuationState Start(string taskId) =>
        new(taskId, new Dictionary<string, int>(StringComparer.Ordinal), null);
}

/// <summary>
///     The continuation token of a background run: UTF-8 JSON
///     <c>{"taskId":"…","artifacts":{"id":parts,…},"status":"messageId"}</c>, the last two optional. It names a remote
///     task the caller can read anyway; it is not a secret.
/// </summary>
internal static class ContinuationTokens
{
    private const string TaskIdProperty = "taskId";
    private const string ArtifactsProperty = "artifacts";
    private const string StatusProperty = "status";

    internal static ResponseContinuationToken For(ContinuationState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(TaskIdProperty, state.TaskId);
            if (state.Artifacts.Count > 0)
            {
                writer.WriteStartObject(ArtifactsProperty);
                foreach (var (id, parts) in state.Artifacts.OrderBy(a => a.Key, StringComparer.Ordinal))
                    writer.WriteNumber(id, parts);
                writer.WriteEndObject();
            }

            if (state.StatusMessageId is { } status) writer.WriteString(StatusProperty, status);
            writer.WriteEndObject();
        }

        return ResponseContinuationToken.FromBytes(buffer.WrittenMemory.ToArray());
    }

    /// <exception cref="ArgumentException">The token was not made by <see cref="For" />.</exception>
    internal static ContinuationState Read(ResponseContinuationToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        try
        {
            using var document = JsonDocument.Parse(token.ToBytes());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            string? taskId = null, status = null;
            var artifacts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                switch (property.Name)
                {
                    case TaskIdProperty when property.Value.ValueKind == JsonValueKind.String:
                        taskId = property.Value.GetString();
                        break;
                    case StatusProperty when property.Value.ValueKind == JsonValueKind.String:
                        status = property.Value.GetString();
                        break;
                    case ArtifactsProperty when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var artifact in property.Value.EnumerateObject())
                        {
                            if (artifact.Value.ValueKind != JsonValueKind.Number || !artifact.Value.TryGetInt32(out var parts) ||
                                parts < 0) throw Invalid();
                            artifacts[artifact.Name] = parts;
                        }

                        break;
                    default:
                        throw Invalid();
                }

            if (string.IsNullOrEmpty(taskId)) throw Invalid();
            return new ContinuationState(taskId, artifacts, status);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        static ArgumentException Invalid() =>
            new("The continuation token was not made by an A2A remote agent.", nameof(token));
    }
}
