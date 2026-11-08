namespace A2A.Server;

/// <summary>Settings for keeping large artifact bytes in an <see cref="Storage.ArtifactObjectStore"/>. Set them in <see cref="A2AServerOptions.ArtifactStorage"/>.</summary>
public sealed class ArtifactStorageOptions
{
    /// <summary>A raw part of this many bytes or fewer stays inline in the task. Default 256 KiB.</summary>
    public int InlineThresholdBytes { get; set; } = 256 * 1024;

    /// <summary>Largest single object, in bytes. Default 5 GiB.</summary>
    public long MaxObjectBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Base of artifact links. Required when an object store is registered. Tenants served from other origins use <see cref="PublicBaseUrlResolver"/>.</summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>Returns the base of artifact links for a tenant. When set, it wins over <see cref="PublicBaseUrl"/>.</summary>
    public Func<string, Uri>? PublicBaseUrlResolver { get; set; }

    /// <summary>How a download is served. Default <see cref="ArtifactDelivery.Redirect"/>.</summary>
    public ArtifactDelivery Delivery { get; set; } = ArtifactDelivery.Redirect;

    /// <summary>How long a presigned download URL works. Default 5 minutes.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How old an object that no task refers to must be before the retention sweep deletes it. Default 1 hour.</summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>How the server delivers an artifact download.</summary>
public enum ArtifactDelivery
{
    /// <summary>Answer with a redirect to a presigned URL when the store has one, otherwise stream through the server.</summary>
    Redirect,
    /// <summary>Always stream the bytes through the server.</summary>
    Proxy,
}
