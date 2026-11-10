using Amazon.S3;

namespace A2A.Server.Storage.S3;

/// <summary>Settings of the <see cref="S3ArtifactObjectStore" />.</summary>
public sealed class S3ArtifactStoreOptions
{
    /// <summary>The bucket that holds the objects. Required. The bucket must already exist.</summary>
    public string BucketName { get; set; } = "";

    /// <summary>
    ///     Text put in front of every object key, for example <c>a2a/</c>, so one bucket can be shared. A prefix that
    ///     does not end with <c>/</c> gets one. A bucket plus prefix belongs to exactly one task store: two task stores must
    ///     not share it, or the orphan sweep deletes the other store's objects. Default empty.
    /// </summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>
    ///     The endpoint of an S3-compatible service (for example MinIO). Null uses AWS S3 with the default region.
    ///     Ignored when <see cref="Client" /> is set.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    ///     Puts the bucket name in the path instead of the host name. Most S3-compatible services need this. Ignored when
    ///     <see cref="Client" /> is set.
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    ///     A client you own and dispose. When null, the store makes its own client with the default AWS credential chain and
    ///     <see cref="ServiceUrl" /> / <see cref="ForcePathStyle" />, and disposes it.
    /// </summary>
    public IAmazonS3? Client { get; set; }
}