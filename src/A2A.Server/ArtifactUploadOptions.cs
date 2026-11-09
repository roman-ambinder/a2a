namespace A2A.Server;

/// <summary>
/// Settings for inbound file uploads: clients send a large file to the <see cref="Storage.ArtifactObjectStore"/> first, then
/// refer to it with a <c>url</c> part in a message. Off by default. Set them in <see cref="ArtifactStorageOptions.Uploads"/>.
/// </summary>
public sealed class ArtifactUploadOptions
{
    /// <summary>
    /// Turns on the message check, the agent read (<see cref="AgentRequestContext.OpenUploadAsync"/>) and the purge of unused uploads.
    /// <c>MapA2AUploads</c> throws while this is false. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Largest upload, in bytes. Must be greater than zero and at most <see cref="ArtifactStorageOptions.MaxObjectBytes"/>. Default 1 GiB.</summary>
    public long MaxUploadBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Most bytes one caller (tenant and principal) may keep stored. A new upload that would pass it is refused with HTTP 429. Default 10 GiB.</summary>
    public long MaxStoredBytesPerCaller { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Most files one caller (tenant and principal) may keep stored. Default 100. It also caps the distinct upload links of one message.</summary>
    public int MaxUploadsPerCaller { get; set; } = 100;

    /// <summary>Most uploads one caller may have running at once on one instance. More are refused with HTTP 429. Default 2.</summary>
    public int MaxConcurrentUploadsPerCaller { get; set; } = 2;

    /// <summary>
    /// How long after the write a new message may refer to an upload. The retention sweep deletes an upload that no task uses
    /// once it is older than this plus <see cref="ArtifactStorageOptions.OrphanGracePeriod"/>. Default 24 hours.
    /// </summary>
    public TimeSpan UnusedUploadTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Checks the values against the storage options that hold them.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    internal void Validate(ArtifactStorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (MaxUploadBytes <= 0) throw new InvalidOperationException($"ArtifactStorage.Uploads.MaxUploadBytes ({MaxUploadBytes}) must be greater than zero.");
        if (MaxUploadBytes > storage.MaxObjectBytes)
            throw new InvalidOperationException($"ArtifactStorage.Uploads.MaxUploadBytes ({MaxUploadBytes}) must be at most ArtifactStorage.MaxObjectBytes ({storage.MaxObjectBytes}).");
        if (MaxStoredBytesPerCaller <= 0)
            throw new InvalidOperationException($"ArtifactStorage.Uploads.MaxStoredBytesPerCaller ({MaxStoredBytesPerCaller}) must be greater than zero.");
        if (MaxUploadsPerCaller <= 0)
            throw new InvalidOperationException($"ArtifactStorage.Uploads.MaxUploadsPerCaller ({MaxUploadsPerCaller}) must be greater than zero.");
        if (MaxConcurrentUploadsPerCaller <= 0)
            throw new InvalidOperationException($"ArtifactStorage.Uploads.MaxConcurrentUploadsPerCaller ({MaxConcurrentUploadsPerCaller}) must be greater than zero.");
        if (UnusedUploadTtl <= TimeSpan.Zero)
            throw new InvalidOperationException($"ArtifactStorage.Uploads.UnusedUploadTtl ({UnusedUploadTtl}) must be greater than zero.");
        // The purge needs both: its cut-off is the TTL plus the grace period, and it runs once per sweep interval.
        if (storage.OrphanGracePeriod <= TimeSpan.Zero)
            throw new InvalidOperationException($"ArtifactStorage.OrphanGracePeriod ({storage.OrphanGracePeriod}) must be greater than zero.");
        if (storage.OrphanSweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException($"ArtifactStorage.OrphanSweepInterval ({storage.OrphanSweepInterval}) must be greater than zero.");
    }
}
