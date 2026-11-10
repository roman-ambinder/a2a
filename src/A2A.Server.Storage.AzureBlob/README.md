# RomanAmbinder.A2A.Server.Storage.AzureBlob

An Azure Blob Storage object store for large A2A artifacts. The task store keeps a small reference; the bytes live in
your container.

```csharp
builder.Services.AddA2AAzureBlobArtifactStore(o =>
{
    o.ConnectionString = builder.Configuration["Storage:ConnectionString"];  // needs the account key for download links
    o.ContainerName = "a2a-artifacts";                                       // must exist
    o.KeyPrefix = "prod/";                                                   // optional, to share a container; one container + prefix = one task store
});
```

To use your own client, set `o.ContainerClient` (`ContainerName` and `ConnectionString` are then ignored).

## How it works

- Uploads are block blobs. The store stages one 8 MiB block at a time and commits the block list at the end, so a
  non-seekable stream never needs a full buffer and nothing is visible before the commit. A failed or over-limit upload
  leaves no blob. Staged blocks that were never committed are discarded by the service after a week.
- Content type and file name are stored with the blob (the file name is percent-encoded, so it can be any text).
- Downloads redirect to a SAS URL (read only, valid for `DownloadUrlLifetime`). The URL sets
  `Content-Disposition: attachment`, so a stored HTML file downloads and never renders from the storage origin. A SAS
  needs a shared key: with a client that cannot sign, the store gives no URL and the server proxies the download.
- `CreatedAt` is the `Last-Modified` date of the blob, which `Put Block List` sets when the commit finishes. Staging
  blocks does not change it, so a slow upload is not dated from its start.

## Permissions

Read, write, list and delete blobs in the container (for example the `Storage Blob Data Contributor` role, or an account
key).

## Tests

The contract runs against Azurite (`mcr.microsoft.com/azure-storage/azurite`, pinned tag) with Testcontainers.
