using System.Diagnostics.CodeAnalysis;
using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>
/// The stable link to an offloaded object: <c>{base}/artifacts/{tenant or _}/{taskId}/{objectId}</c>. The link holds no secret and
/// never expires, so the event that carries it can be encoded once and kept as it is.
/// </summary>
internal static class ArtifactLink
{
    private const string Segment = "artifacts";

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
}
