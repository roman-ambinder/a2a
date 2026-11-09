using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace A2A.Server.Storage;

/// <summary>
/// The key layouts of inbound file uploads: an upload is <c>_uploads/{tenant}/{ownerHash}/{objectId}</c>, and the reference a
/// message leaves on it is <c>_uploadrefs/{tenant}/{ownerHash}/{objectId}/{taskId}</c> (zero bytes). Tenant and task id use the
/// encoding of <see cref="ArtifactObjectKey"/>. The owner hash is the lowercase SHA-256 hex of the UTF-8 principal, so a bucket
/// listing shows no user ids. These keys have 4 and 5 segments, while <see cref="ArtifactObjectKey.TryParse"/> needs exactly 3, so
/// the task orphan sweep skips them. Parsing accepts only the exact form the builders write.
/// </summary>
internal static class UploadObjectKey
{
    private const string UploadRoot = "_uploads";
    private const string ReferenceRoot = "_uploadrefs";
    private const int OwnerHashLength = 64;

    /// <summary>The prefix of every upload key (and of nothing else a 3-segment artifact key can have, as it ends with <c>/</c>).</summary>
    public const string UploadsPrefix = UploadRoot + "/";

    /// <summary>The prefix of every reference key.</summary>
    public const string ReferencesPrefix = ReferenceRoot + "/";

    /// <summary>The lowercase SHA-256 hex of the principal's UTF-8 bytes.</summary>
    public static string OwnerHash(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(principal))).ToLowerInvariant();
    }

    /// <summary>True for exactly 64 lowercase hex digits.</summary>
    public static bool IsValidOwnerHash(string ownerHash)
    {
        if (ownerHash is null || ownerHash.Length != OwnerHashLength) return false;
        foreach (var c in ownerHash)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The prefix shared by every upload of one caller. It ends with <c>/</c>.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, or the owner hash is not valid.</exception>
    public static string UploadPrefix(string tenant, string ownerHash) =>
        UploadsPrefix + ArtifactObjectKey.TenantSegment(tenant) + "/" + RequireOwnerHash(ownerHash) + "/";

    /// <summary>Builds the key of one upload.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, or the owner hash or object id is not valid.</exception>
    public static string CreateUpload(string tenant, string ownerHash, string objectId) =>
        UploadPrefix(tenant, ownerHash) + RequireObjectId(objectId);

    /// <summary>The prefix shared by every reference to one upload. It ends with <c>/</c>.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, or the owner hash or object id is not valid.</exception>
    public static string ReferencePrefix(string tenant, string ownerHash, string objectId) =>
        ReferencesPrefix + ArtifactObjectKey.TenantSegment(tenant) + "/" + RequireOwnerHash(ownerHash) + "/" + RequireObjectId(objectId) + "/";

    /// <summary>Builds the key of the reference that task <paramref name="taskId"/> leaves on an upload.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, or the owner hash or object id is not valid.</exception>
    public static string CreateReference(string tenant, string ownerHash, string objectId, string taskId) =>
        ReferencePrefix(tenant, ownerHash, objectId) + ArtifactObjectKey.TaskSegment(taskId);

    /// <summary>Splits a key made by <see cref="CreateUpload"/>. Returns false for any other string.</summary>
    public static bool TryParseUpload(string key, [NotNullWhen(true)] out string? tenant, [NotNullWhen(true)] out string? ownerHash, [NotNullWhen(true)] out string? objectId)
    {
        tenant = ownerHash = objectId = null;
        var parts = key.Split('/');
        if (parts.Length != 4 || parts[0] != UploadRoot) return false;
        if (!ArtifactObjectKey.TryReadTenantSegment(parts[1], out var t) || !IsValidOwnerHash(parts[2]) || !ArtifactObjectKey.IsValidObjectId(parts[3])) return false;
        if (CreateUpload(t, parts[2], parts[3]) != key) return false; // only the exact spelling the builder writes
        (tenant, ownerHash, objectId) = (t, parts[2], parts[3]);
        return true;
    }

    /// <summary>Splits a key made by <see cref="CreateReference"/>. Returns false for any other string.</summary>
    public static bool TryParseReference(string key, [NotNullWhen(true)] out string? tenant, [NotNullWhen(true)] out string? ownerHash,
        [NotNullWhen(true)] out string? objectId, [NotNullWhen(true)] out string? taskId)
    {
        tenant = ownerHash = objectId = taskId = null;
        var parts = key.Split('/');
        if (parts.Length != 5 || parts[0] != ReferenceRoot) return false;
        if (!ArtifactObjectKey.TryReadTenantSegment(parts[1], out var t) || !IsValidOwnerHash(parts[2]) || !ArtifactObjectKey.IsValidObjectId(parts[3])) return false;
        var task = ArtifactObjectKey.ReadTaskSegment(parts[4]);
        if (CreateReference(t, parts[2], parts[3], task) != key) return false;
        (tenant, ownerHash, objectId, taskId) = (t, parts[2], parts[3], task);
        return true;
    }

    private static string RequireOwnerHash(string ownerHash) =>
        IsValidOwnerHash(ownerHash) ? ownerHash : throw new ArgumentException("Not a valid owner hash.", nameof(ownerHash));

    private static string RequireObjectId(string objectId) =>
        ArtifactObjectKey.IsValidObjectId(objectId) ? objectId : throw new ArgumentException("Not a valid object id.", nameof(objectId));
}
