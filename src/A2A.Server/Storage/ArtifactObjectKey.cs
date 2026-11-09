using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace A2A.Server.Storage;

/// <summary>
/// The key layout of artifact objects: <c>{tenant}/{taskId}/{objectId}</c>. Tenant and task id are percent-encoded (and every
/// <c>.</c> too), so no id can add a segment or become <c>.</c> or <c>..</c>. An empty tenant is written <c>_</c>.
/// </summary>
internal static class ArtifactObjectKey
{
    private const string EmptyTenant = "_";
    private const int ObjectIdLength = 32;

    /// <summary>Builds the key of one object.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, or the object id is not valid.</exception>
    public static string Create(string tenant, string taskId, string objectId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (!IsValidObjectId(objectId)) throw new ArgumentException("Not a valid object id.", nameof(objectId));
        return TaskPrefix(tenant, taskId) + objectId;
    }

    /// <summary>The prefix shared by every object of one task. It ends with <c>/</c>, so it never matches another task's keys.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>.</exception>
    public static string TaskPrefix(string tenant, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return TenantSegment(tenant) + "/" + TaskSegment(taskId) + "/";
    }

    /// <summary>The key segment of a tenant: percent-encoded, and <c>_</c> for the default tenant.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>.</exception>
    public static string TenantSegment(string tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant == EmptyTenant) throw new ArgumentException("The tenant '_' is reserved for the default tenant.", nameof(tenant));
        return tenant.Length == 0 ? EmptyTenant : Encode(tenant);
    }

    /// <summary>The key segment of a task id: percent-encoded, so it can add no segment and is never <c>.</c> or <c>..</c>.</summary>
    public static string TaskSegment(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return Encode(taskId);
    }

    /// <summary>Reads a <see cref="TenantSegment"/>. Returns false when the segment would decode to the reserved tenant <c>_</c>.</summary>
    public static bool TryReadTenantSegment(string segment, [NotNullWhen(true)] out string? tenant)
    {
        tenant = segment == EmptyTenant ? string.Empty : Uri.UnescapeDataString(segment);
        if (tenant == EmptyTenant)
        {
            tenant = null;
            return false;
        }
        return true;
    }

    /// <summary>Reads a <see cref="TaskSegment"/>.</summary>
    public static string ReadTaskSegment(string segment) => Uri.UnescapeDataString(segment);

    /// <summary>Splits a key made by <see cref="Create"/>. Returns false for any other string.</summary>
    public static bool TryParse(string key, [NotNullWhen(true)] out string? tenant, [NotNullWhen(true)] out string? taskId, [NotNullWhen(true)] out string? objectId)
    {
        tenant = taskId = objectId = null;
        var parts = key.Split('/');
        if (parts.Length != 3 || !IsValidObjectId(parts[2])) return false;
        // Only the exact form Create writes is a key: this rejects "%2e", "%5F" and other spellings of the same ids.
        if (!TryReadTenantSegment(parts[0], out var t)) return false;
        var id = ReadTaskSegment(parts[1]);
        if (Create(t, id, parts[2]) != key) return false;
        (tenant, taskId, objectId) = (t, id, parts[2]);
        return true;
    }

    /// <summary>A new random object id: 32 lowercase hex digits (128 bits).</summary>
    public static string NewObjectId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(ObjectIdLength / 2)).ToLowerInvariant();

    /// <summary>True for exactly 32 lowercase hex digits.</summary>
    public static bool IsValidObjectId(string objectId)
    {
        if (objectId is null || objectId.Length != ObjectIdLength) return false;
        foreach (var c in objectId)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
        return true;
    }

    private static string Encode(string s) => Uri.EscapeDataString(s).Replace(".", "%2E", StringComparison.Ordinal);
}
