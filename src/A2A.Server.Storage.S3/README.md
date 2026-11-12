# RomanAmbinder.A2A.Server.Storage.S3

An Amazon S3 (or S3-compatible, such as MinIO) object store for large A2A artifacts. The task store keeps a small
reference; the bytes live in your bucket. Use it when your agent sends or receives files too large to keep in the task
store.

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
  multipart upload completes the store tags the object with how long the upload took (`a2a-upload-ms`, timed by the
  uploading process from before the upload started), and a listing dates the object at its S3 date plus that time. The
  date stays on the S3 clock, as a single-part object's does, so a server whose clock is off does not shift it. If the
  stamp cannot be written, the upload fails and the object is deleted. A multipart object listed without a stamp (its
  upload completed a moment ago, or the process died before stamping it) is dated 7 days after its S3 date: the orphan
  sweep leaves it alone until then, and still collects it in the end. Any other tag read error is thrown (the sweep
  retries). The completion time by the server's clock (`a2a-completed-at`) is also written, and read, for earlier
  versions.

## Permissions

`s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`, `s3:AbortMultipartUpload`, and
`s3:PutObjectTagging` / `s3:GetObjectTagging` (required: for `CreatedAt` of large objects).

Add a bucket lifecycle rule that aborts incomplete multipart uploads. A failed upload is aborted, but a process that
crashes mid-upload leaves its parts behind: they are billed, never listed as objects, and nothing else removes them.
For example, with the AWS CLI (`aws s3api put-bucket-lifecycle-configuration`; this replaces the bucket's lifecycle
configuration, so merge it with any rules you have):

```json
{
  "Rules": [
    {
      "ID": "abort-incomplete-multipart-uploads",
      "Status": "Enabled",
      "Filter": { "Prefix": "" },
      "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": 1 }
    }
  ]
}
```

Set `Prefix` to your `KeyPrefix` to limit the rule to the store's objects, and keep `DaysAfterInitiation` longer than
your largest upload takes.

## Tests

The contract runs against MinIO (`cgr.dev/chainguard/minio`) with Testcontainers.

Artifact storage is opt-in; see the [artifact storage guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/artifact-storage.md) for the server options
(`A2AServerOptions.ArtifactStorage`) and the download endpoint (`MapA2AArtifacts`).
