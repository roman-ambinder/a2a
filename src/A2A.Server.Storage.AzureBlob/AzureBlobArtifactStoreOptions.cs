using Azure.Storage.Blobs;

namespace A2A.Server.Storage.AzureBlob;

/// <summary>Settings of the <see cref="AzureBlobArtifactObjectStore"/>.</summary>
public sealed class AzureBlobArtifactStoreOptions
{
    /// <summary>The container that holds the blobs. Required unless <see cref="ContainerClient"/> is set. The container must already exist.</summary>
    public string ContainerName { get; set; } = "";

    /// <summary>
    /// The storage account connection string. Required unless <see cref="ContainerClient"/> is set. It must hold the account key
    /// (or a SAS) for the store to read and write; download links (SAS) can only be made from an account key.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Text put in front of every blob name, for example <c>a2a/</c>, so one container can be shared. A prefix that does not end with <c>/</c> gets one. A container plus prefix belongs to exactly one task store: two task stores must not share it, or the orphan sweep deletes the other store's objects. Default empty.</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>
    /// A container client you own. When set, <see cref="ContainerName"/> and <see cref="ConnectionString"/> are ignored. Download
    /// links are made only when this client was created with a shared key credential; otherwise the store serves no links and
    /// downloads are proxied.
    /// </summary>
    public BlobContainerClient? ContainerClient { get; set; }
}
