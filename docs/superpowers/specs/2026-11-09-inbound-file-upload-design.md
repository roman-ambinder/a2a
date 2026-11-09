# Inbound file upload

The object store holds agent output only. A client file stays inline in the message, so it is bound by `MaxRequestBodyBytes` (10 MiB). This adds an opt-in upload on the same store. The client uploads first, gets a normal `url` part, and sends that part in its message. The A2A wire format does not change.

## Flow

1. `POST {pattern}/uploads/{tenant or _}` with the raw file as the body. `Content-Type` is the media type. `Content-Disposition: attachment; filename*=...` is the optional file name.
2. The server streams the body to the store and answers 201 with the JSON of a `UrlPart`: `url`, `mediaType`, `filename`, and `metadata.sha256` and `metadata.sizeBytes`. `Location` is the link.
3. The client puts the part in a message. Before commit the server checks the link and records a reference.
4. The agent reads it with `AgentRequestContext.OpenUploadAsync(part, range, ct)`.
5. The owner can read it back with `GET {pattern}/uploads/{tenant or _}/{objectId}`.

The link is `{base}/uploads/{tenant or _}/{objectId}`. It holds no secret and no owner.

## Opt-in

`A2AServerOptions.ArtifactStorage.Uploads` (`ArtifactUploadOptions`):

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | false | Turns on the message check, the agent read and the purge. `MapA2AUploads` throws without it. |
| `MaxUploadBytes` | 1 GiB | Largest file. Must be at most `MaxObjectBytes`. |
| `MaxStoredBytesPerCaller` | 10 GiB | Stored bytes per tenant and principal. |
| `MaxUploadsPerCaller` | 100 | Stored files per tenant and principal. |
| `MaxConcurrentUploadsPerCaller` | 2 | Uploads running at once, per caller, per instance. |
| `UnusedUploadTtl` | 24 hours | A new message can refer to an upload only this long after the write. |

`MapA2AUploads(pattern)` throws `InvalidOperationException` when `Enabled` is false, no store is registered, or an option is out of range. It maps POST and GET, applies the rate limit policy, and is covered by the startup authorization check, as `MapA2AArtifacts` is. Startup logs a warning when uploads are on and retention is off, because nothing would be purged.

## Keys

| Object | Key |
|---|---|
| Upload | `_uploads/{tenant}/{ownerHash}/{objectId}` |
| Reference | `_uploadrefs/{tenant}/{ownerHash}/{objectId}/{taskId}`, zero bytes |

`{tenant}` and `{taskId}` use the encoding of `ArtifactObjectKey`, with `_` for the default tenant. `{ownerHash}` is the lowercase SHA-256 hex of the UTF-8 principal, so bucket keys hold no user ids.

These keys have 4 and 5 segments. `ArtifactObjectKey.TryParse` needs exactly 3, so the task orphan sweep skips them. A tenant really named `_uploads` makes 3-segment keys, which the upload parser refuses.

No provider needs a new method, so ADR-001 is respected. `ArtifactObjectEntry` gets an optional `long? SizeBytes { get; init; }`. In-memory, S3 and Azure fill it. When a third-party store leaves it null, the quota code gets the size with a `(0, 0)` range read.

## Security

- **Authentication.** Upload, read and agent read always need an authenticated caller, even with `AllowAnonymous`. Anonymous callers share one principal, so they could read each other's files. Anonymous gets `Unauthenticated`.
- **Authorization.** Runs through the pipeline with `A2AOperationDescriptor.Extension("a2a.files.upload")` and `Extension("a2a.files.read")`, via `RunExtensionAsync`. The endpoint sets `PolicySatisfiedItem`, so the default handler allows what the route's policy admitted. A custom `A2AAuthorizationHandler` decides by name. The agent read skips the pipeline: the call was authorized as SendMessage, and the key rule confines it to the caller's files.
- **Owner and tenant.** The key is built from the caller's own tenant and principal, so another owner's object is simply not found. There is no owner field to get wrong. The route tenant must agree with the resolver, as in `ArtifactEndpoint`. Every miss on GET is 404.
- **Confused deputy.** A message that names another owner's link must not reach the agent. Under `{base}/uploads/` for the caller's tenant, a URL must be the exact canonical link, and its object must exist under the caller's key and be within `UnusedUploadTtl`. Otherwise `InvalidParams` with one fixed text for all cases. Other URLs are ordinary URLs and are left alone. `OpenUploadAsync` applies the same key rule and returns null for anything else.
- **Size.** `Content-Length` above the cap is refused before reading. The stream is capped by the store's `maxBytes`, so chunked bodies are bound too. The endpoint raises `IHttpMaxRequestBodySizeFeature` for this request only, to `MaxUploadBytes`. When `Content-Length` was sent and differs from the bytes written, the object is deleted and the call is refused.
- **Quota.** See below.
- **Input.** An unparsable media type is `InvalidParams`. The file name has any path removed, must be at most 255 characters, and must have no control characters. Downloads use the existing `attachment`, `nosniff` and `no-store` headers.

Errors: above `MaxUploadBytes` is 413 (`PayloadTooLarge`). Quota or concurrency is 429 (`RateLimited`), with `Retry-After` set to `OrphanSweepInterval` for a quota and 1 second for concurrency.

## Quota

The store is the source of truth, so the quota works across instances with no new table.

1. Enter the per-caller concurrency slot for this instance.
2. List `_uploads/{tenant}/{ownerHash}/`, summing size and count. Refuse when either is at its limit.
3. `maxBytes` for the write is the smaller of `MaxUploadBytes` and the bytes left in the quota. A stream past it is 413 when the limit was `MaxUploadBytes`, otherwise 429.
4. After the write, list again. If the total is over a limit, delete this object and answer 429.

Two instances racing past the limit both see both objects and both delete. That fails safe: the caller retries. Cleanup deletes use `CancellationToken.None`.

## References and purge

`AttachAsync` runs in `PrepareAsync` after task ownership is checked and before anything is committed. It does nothing for a duplicate. For each distinct upload link in the message it validates the upload and writes the reference. More distinct links than `MaxUploadsPerCaller` is `InvalidParams`. The agent must not see a message whose references were not written.

Retention has a new step, "unused uploads", when `Enabled` and a store exist. It runs at most once per `OrphanSweepInterval`, like the orphan step, and deletes at most `BatchSize` objects per sweep.

- Consider uploads older than `UnusedUploadTtl + OrphanGracePeriod`.
- For each reference: younger than `OrphanGracePeriod` keeps the upload, because the commit may be in flight. A task that exists and refers to the link keeps the upload. Otherwise delete the reference.
- With no live reference, delete the upload. References go first, so a crash leaves an upload with no references, which the next sweep deletes.
- A task that cannot be read keeps the upload.
- Then delete references older than `OrphanGracePeriod` whose upload is gone.

The attach window closes at `UnusedUploadTtl` and the purge starts later by `OrphanGracePeriod`, so a reference written at the last moment is always seen. Deleting a task does not touch uploads: the sweep finds the task gone.

Hosts that turn `Enabled` off keep their old objects until they clear them.

## Client

`A2AFileUploader` in `A2A.Client`, .NET 8 and later like `A2AArtifactDownloader`.

`new A2AFileUploader(agentHandler, uploadUrl)`; `UploadAsync(content, mediaType, fileName, ct)` returns the `UrlPart`.

- Uses the handler with credentials. Refuses one that follows redirects.
- Hashes while sending, and throws `InvalidDataException` when the answer's `sha256` or `sizeBytes` differs.
- Throws `InvalidOperationException` when the returned link is on another origin than `uploadUrl`, so credentials never go elsewhere.
- No client timeout: large files take time. Cancellation is the caller's.

## Not in scope

No owner delete, no resumable or multipart upload, no virus scan, no media type allow-list, no gRPC upload, and no advertisement on the agent card, which would change the wire format.
