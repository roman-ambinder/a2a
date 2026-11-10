# RomanAmbinder.A2A.Server.Storage.S3

An Amazon S3 (or S3-compatible, such as MinIO) object store for large A2A artifacts. The task store keeps a small
reference; the bytes live in your bucket.

```csharp
builder.Services.AddA2AS3ArtifactStore(o =>
{
    o.BucketName = "my-a2a-artifacts";   // must exist
    o.KeyPrefix = "prod/";               // optional, to share a bucket; one bucket + prefix = one task store
});

// S3-compatible service:
builder.Services.AddA2AS3ArtifactStore(o =>
{
    o.BucketName = "artifacts";
    o.ServiceUrl = "http://minio:9000";
    o.ForcePathStyle = true;
});
```

Credentials come from the default AWS chain. To use your own client, set `o.Client` (you dispose it).

## How it works

- Uploads buffer one 8 MiB part at a time, so a non-seekable stream never needs a full buffer. A stream shorter than one
  part (including zero bytes) is one PUT; a longer one is a multipart upload. A failed or over-limit upload is aborted
  and leaves no object.
- Content type and file name are stored as object metadata (the file name is percent-encoded, so it can be any text).
- Downloads redirect to a presigned URL. The URL sets `Content-Disposition: attachment`, so a stored HTML file downloads
  and never renders from the bucket origin.
- `ExistsAsync` is a HEAD request.
- Keys with an empty segment (an empty task id) are written with the marker `%-` in that segment, because some
  S3-compatible services refuse `//`. Callers never see it.
- `CreatedAt` is the time the write finished. S3 dates a multipart object from when the upload started, so after a
  multipart upload completes the store tags the object with its completion time and uses that. If the stamp cannot be
  written, the upload fails and the object is deleted. A listing uses the S3 date only when the object has no stamp; any
  other tag read error is thrown (the sweep retries).

## Permissions

`s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`, `s3:AbortMultipartUpload`, and
`s3:PutObjectTagging` / `s3:GetObjectTagging` (required: for `CreatedAt` of large objects). Add a bucket lifecycle rule
that aborts incomplete multipart uploads, as a safety net for a crashed process.

## Tests

The contract runs against MinIO (`cgr.dev/chainguard/minio`) with Testcontainers.
