using System.Text.Json;

namespace A2A.Server;

/// <summary>
///     The seal of a stored artifact: <see cref="A2AProtocol.ArtifactSealedKey" /> in its metadata. Only the server writes
///     it.
/// </summary>
internal static class ArtifactSeal
{
    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();

    /// <summary>Whether <paramref name="artifact" /> carries the seal.</summary>
    public static bool IsSealed(Artifact artifact)
    {
        return artifact.Metadata is { } metadata
               && metadata.TryGetValue(A2AProtocol.ArtifactSealedKey, out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>Whether <paramref name="artifacts" /> holds a sealed artifact with this id (false when it is missing or open).</summary>
    public static bool IsSealed(List<Artifact>? artifacts, string artifactId)
    {
        if (artifacts is null) return false;
        foreach (var a in artifacts)
            if (a.ArtifactId == artifactId)
                return IsSealed(a);
        return false;
    }

    /// <summary>
    ///     The artifact to store for <paramref name="artifact" />: with the seal when <paramref name="sealed" /> is true, and
    ///     without
    ///     it when not (an agent cannot set the seal itself). Returns <paramref name="artifact" /> when nothing changes;
    ///     otherwise a
    ///     new artifact that shares the parts, so the event's own instance is never changed.
    /// </summary>
    public static Artifact Stamp(Artifact artifact, bool @sealed)
    {
        if (!@sealed && !(artifact.Metadata?.ContainsKey(A2AProtocol.ArtifactSealedKey) ?? false)) return artifact;
        var metadata = artifact.Metadata is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(artifact.Metadata);
        if (@sealed) metadata[A2AProtocol.ArtifactSealedKey] = True;
        else metadata.Remove(A2AProtocol.ArtifactSealedKey);
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Description = artifact.Description,
            Parts = artifact.Parts,
            Metadata = metadata.Count == 0 ? null : metadata,
            Extensions = artifact.Extensions,
            StartGeneration = artifact.StartGeneration,
            EndGeneration = artifact.EndGeneration,
            Role = artifact.Role
        };
    }

    /// <summary>
    ///     The artifact to store with its place in the task's history (A2A 1.1 §3.2.8): <paramref name="start" /> as
    ///     <c>startGeneration</c>, and <paramref name="end" /> as <c>endGeneration</c> (null while it is still open). Returns
    ///     <paramref name="artifact" /> when both are already set to those values; otherwise a shallow copy (parts shared), so
    ///     an
    ///     artifact shared with an event is never changed.
    /// </summary>
    public static Artifact Place(Artifact artifact, long start, long? end)
    {
        if (artifact.StartGeneration == start && artifact.EndGeneration == end) return artifact;
        return new Artifact
        {
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Description = artifact.Description,
            Parts = artifact.Parts,
            Metadata = artifact.Metadata,
            Extensions = artifact.Extensions,
            StartGeneration = start,
            EndGeneration = end,
            Role = artifact.Role
        };
    }

    /// <summary>Sets or removes the seal on <paramref name="artifact" /> itself, which the caller owns (a fresh copy).</summary>
    public static void Set(Artifact artifact, bool @sealed)
    {
        if (@sealed)
            (artifact.Metadata ??= new Dictionary<string, JsonElement>())[A2AProtocol.ArtifactSealedKey] = True;
        else if (artifact.Metadata is { } m && m.Remove(A2AProtocol.ArtifactSealedKey) && m.Count == 0)
            artifact.Metadata = null;
    }
}