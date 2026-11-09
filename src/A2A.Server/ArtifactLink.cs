using System.Diagnostics.CodeAnalysis;
using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>
/// The stable link to an offloaded object: <c>{base}/artifacts/{tenant or _}/{taskId}/{objectId}</c>. The link holds no secret and
/// never expires, so the event that carries it can be encoded once and kept as it is. An inbound upload has its own form,
/// <c>{base}/uploads/{tenant or _}/{objectId}</c> (<see cref="CreateUpload"/>), which holds no owner either.
/// </summary>
internal static class ArtifactLink
{
    private const string Segment = "artifacts";
    private const string UploadSegment = "uploads";

    /// <summary>Builds the link of one object. Tenant and task id are percent-encoded, so no id can change the path.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c>, the tenant or task id is <c>.</c> or <c>..</c>, or the object id is not valid.</exception>
    public static Uri Create(Uri baseUrl, string tenant, string taskId, string objectId)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(taskId);
        // A URL parser removes "." and ".." segments even when they are written %2E, so such an id would change the path.
        if (tenant is "." or ".." || taskId is "." or "..")
            throw new ArgumentException("The ids '.' and '..' cannot be part of an artifact link.");
        // The key builder checks the ids, and its prefix is exactly the encoded {tenant}/{taskId}/ of the link.
        var path = ArtifactObjectKey.Create(tenant, taskId, objectId);
        var root = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{root}/{Segment}/{path}", UriKind.Absolute);
    }

    /// <summary>Reads the object id from a link made by <see cref="Create"/>. Returns false for any other URL.</summary>
    public static bool TryGetObjectId(Uri url, [NotNullWhen(true)] out string? objectId)
    {
        objectId = null;
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) return false;
        var segments = url.AbsolutePath.Split('/');
        if (segments.Length < 4) return false;
        var last = segments[^1];
        if (segments[^4] != Segment || !ArtifactObjectKey.IsValidObjectId(last)) return false;
        objectId = last;
        return true;
    }

    /// <summary>Builds the link of one inbound upload: <c>{base}/uploads/{tenant or _}/{objectId}</c>. The tenant is percent-encoded.</summary>
    /// <exception cref="ArgumentException">The tenant is the literal <c>_</c> or is <c>.</c> or <c>..</c>, or the object id is not valid.</exception>
    public static Uri CreateUpload(Uri baseUrl, string tenant, string objectId)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(tenant);
        // See Create: a URL parser would remove such a segment.
        if (tenant is "." or "..") throw new ArgumentException("The ids '.' and '..' cannot be part of an upload link.");
        if (!ArtifactObjectKey.IsValidObjectId(objectId)) throw new ArgumentException("Not a valid object id.", nameof(objectId));
        return new Uri($"{UploadRoot(baseUrl)}{ArtifactObjectKey.TenantSegment(tenant)}/{objectId}", UriKind.Absolute);
    }

    /// <summary>
    /// True when <paramref name="url"/> is under <c>{base}/uploads/</c> (same origin, same path prefix). Such a URL is meant as an
    /// upload link, so the server must accept it as the exact link of one of the caller's uploads or refuse it.
    /// </summary>
    public static bool IsUnderUploads(Uri url, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(baseUrl);
        // AbsoluteUri has the scheme and host in lower case and the default port removed, on both sides.
        return url.IsAbsoluteUri && url.AbsoluteUri.StartsWith(new Uri(UploadRoot(baseUrl)).AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>Reads the object id from a link made by <see cref="CreateUpload"/>. Returns false for any other URL. It does not check the rest of the link.</summary>
    public static bool TryGetUploadObjectId(Uri url, [NotNullWhen(true)] out string? objectId)
    {
        objectId = null;
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) return false;
        var segments = url.AbsolutePath.Split('/');
        if (segments.Length < 4) return false;
        var last = segments[^1];
        if (segments[^3] != UploadSegment || !ArtifactObjectKey.IsValidObjectId(last)) return false;
        objectId = last;
        return true;
    }

    private static string UploadRoot(Uri baseUrl) => $"{baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{UploadSegment}/";
}
