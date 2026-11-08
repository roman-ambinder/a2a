using System.Security.Cryptography;
using System.Text.Json;
using A2A.Integrity;

namespace A2A.Client;

/// <summary>The result of checking one artifact against its <c>sha256</c> metadata (<see cref="ArtifactVerifier"/>).</summary>
public enum ArtifactVerificationStatus
{
    /// <summary>The metadata has no <c>sha256</c> value.</summary>
    NoHash,

    /// <summary>The hash matches the content.</summary>
    Verified,

    /// <summary>The hash is well formed and different from the content's hash. <see cref="ArtifactVerification.Expected"/> and <see cref="ArtifactVerification.Actual"/> are set.</summary>
    Mismatch,

    /// <summary>
    /// The value is not a string, or not <c>sha256:</c> and 64 lowercase hex digits (an unknown scheme fails closed), or the
    /// content could not be hashed (for example a data part with a number outside the double range).
    /// </summary>
    Unverifiable,
}

/// <summary>The outcome of checking one artifact, or one artifact chunk, against its <c>sha256</c> metadata.</summary>
public sealed class ArtifactVerification
{
    /// <summary>Creates a result.</summary>
    /// <param name="artifactId">The artifact checked.</param>
    /// <param name="status">The outcome.</param>
    /// <param name="expected">The hash the metadata states, when it is a string.</param>
    /// <param name="actual">The hash of the content, when it could be computed.</param>
    public ArtifactVerification(string artifactId, ArtifactVerificationStatus status, string? expected = null, string? actual = null)
    {
        ArtifactId = artifactId;
        Status = status;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The artifact checked.</summary>
    public string ArtifactId { get; }

    /// <summary>The outcome.</summary>
    public ArtifactVerificationStatus Status { get; }

    /// <summary>The hash the metadata states. Null when there is none, or it is not a string.</summary>
    public string? Expected { get; }

    /// <summary>The hash of the content, as <see cref="ArtifactHash.Format"/> writes it. Null when the content could not be hashed.</summary>
    public string? Actual { get; }
}

/// <summary>
/// Checks artifacts against the optional integrity hash a producer sets with <c>TaskUpdater.AddHashedArtifactAsync</c>
/// (<c>metadata["sha256"]</c>, see <see cref="ArtifactHash"/>). It detects corruption and bad reassembly of chunks. It is not
/// authenticity: anyone who can change the content can change an unsigned hash.
/// </summary>
/// <remarks>
/// <para>
/// Feed <see cref="Observe"/> every event of one task, in order. Use one verifier per task. It keeps one running hash for each
/// artifact that is still open, and no content. It never throws on bad content and never ends the stream: the caller decides what
/// to do with a <see cref="ArtifactVerificationStatus.Mismatch"/>. It does not enforce that a sealed artifact stays closed.
/// </para>
/// <para>This class is not thread safe.</para>
/// </remarks>
public sealed class ArtifactVerifier : IDisposable
{
    private const string Prefix = "sha256:";
    private readonly Dictionary<string, IncrementalHash?> _running = new(); // null: the content so far could not be hashed

    /// <summary>
    /// Checks one event. An artifact update returns one result: the hash of the artifact so far against the chunk's
    /// <c>sha256</c>. A task snapshot resets what the verifier knows and returns one result per artifact in it. Any other event
    /// returns an empty list.
    /// </summary>
    /// <param name="evt">The next event of the task.</param>
    public IReadOnlyList<ArtifactVerification> Observe(StreamResponse evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case ArtifactUpdateEvent { ArtifactUpdate: var update }:
                return [ObserveChunk(update)];
            case TaskEvent { Task: var task }:
                Reset();
                var results = new List<ArtifactVerification>();
                foreach (var artifact in task.Artifacts ?? [])
                {
                    var result = Check(artifact, out var hash);
                    results.Add(result);
                    // A finished artifact gets no more chunks: keep a running hash only for an open one.
                    if (IsSealed(artifact)) hash?.Dispose();
                    else _running[artifact.ArtifactId] = hash;
                }
                return results;
            default:
                return [];
        }
    }

    /// <summary>
    /// Checks a whole artifact, for example one of a task from <c>GetTask</c>, against its <c>sha256</c> metadata. Never throws on bad content.
    /// </summary>
    /// <param name="artifact">The artifact.</param>
    public static ArtifactVerification Verify(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var result = Check(artifact, out var hash);
        hash?.Dispose();
        return result;
    }

    /// <summary>Releases the running hashes.</summary>
    public void Dispose() => Reset();

    private ArtifactVerification ObserveChunk(TaskArtifactUpdateEvent update)
    {
        var artifact = update.Artifact;
        var id = artifact.ArtifactId;
        IncrementalHash? hash = null;
        var hashable = true;
        if (update.Append && _running.TryGetValue(id, out var running))
        {
            hash = running;
            hashable = running is not null;
        }
        else
        {
            // A chunk without append starts the artifact over. An append to one never seen starts it here.
            Drop(id);
            hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        if (hashable)
        {
            try
            {
                ArtifactHash.Update(hash!, artifact.Parts);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                hash!.Dispose();
                hash = null;
                hashable = false;
            }
        }

        var result = Compare(id, artifact.Metadata, hashable ? Format(hash!) : null);
        if (update.LastChunk)
        {
            hash?.Dispose();
            _running.Remove(id);
        }
        else
        {
            _running[id] = hash;
        }
        return result;
    }

    private static ArtifactVerification Check(Artifact artifact, out IncrementalHash? hash)
    {
        hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            ArtifactHash.Update(hash, artifact.Parts);
            return Compare(artifact.ArtifactId, artifact.Metadata, Format(hash));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            hash.Dispose();
            hash = null;
            return Compare(artifact.ArtifactId, artifact.Metadata, actual: null);
        }
    }

    private static string Format(IncrementalHash hash) => ArtifactHash.Format(hash.GetCurrentHash());

    /// <summary>Compares the stated hash in <paramref name="metadata"/> with <paramref name="actual"/> (null: the content could not be hashed).</summary>
    private static ArtifactVerification Compare(string artifactId, Dictionary<string, JsonElement>? metadata, string? actual)
    {
        if (metadata is null || !metadata.TryGetValue(ArtifactHash.MetadataKey, out var value))
            return new ArtifactVerification(artifactId, ArtifactVerificationStatus.NoHash, actual: actual);
        var expected = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (expected is null || !IsWellFormed(expected) || actual is null)
            return new ArtifactVerification(artifactId, ArtifactVerificationStatus.Unverifiable, expected, actual);
        return new ArtifactVerification(artifactId,
            string.Equals(expected, actual, StringComparison.Ordinal) ? ArtifactVerificationStatus.Verified : ArtifactVerificationStatus.Mismatch,
            expected, actual);
    }

    private static bool IsWellFormed(string value)
    {
        if (value.Length != Prefix.Length + 64 || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        foreach (var c in value.AsSpan(Prefix.Length))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        }
        return true;
    }

    private static bool IsSealed(Artifact artifact) =>
        artifact.Metadata is { } m && m.TryGetValue(A2AProtocol.ArtifactSealedKey, out var v) && v.ValueKind == JsonValueKind.True;

    private void Drop(string id)
    {
        if (_running.Remove(id, out var old)) old?.Dispose();
    }

    private void Reset()
    {
        foreach (var hash in _running.Values) hash?.Dispose();
        _running.Clear();
    }
}
