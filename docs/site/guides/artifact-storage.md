# Artifact storage

By default a task is one row, and every artifact byte lives inside it. That is fine for text and small files. It is a poor fit for large files: every event rewrites the whole task, and `GetTask` returns all the bytes. An artifact object store keeps large bytes outside the task. The task holds a normal `url` part instead.

This guide covers the size caps, when to use a store, how to turn it on, how a caller downloads a file, how a client sends a large file in, and how the bytes are cleaned up.

## Size caps come first

Two caps apply to every task, with or without a store (see [Limits and rate limiting](limits-and-rate-limiting.md)):

- <xref:A2A.Server.A2ALimits.MaxTaskBytes> (64 MiB) is the total content of a stored task: history and inline artifacts.
- <xref:A2A.Server.A2ALimits.MaxArtifactsPerTask> (1024) is the number of artifacts.

An agent event past a cap fails the task and stops the agent. A follow-up message past `MaxTaskBytes` is refused with `UnsupportedOperation`. A `url` part counts as its URL length, so a file in a store costs the task almost nothing.

## When to use a store

Use a store when an agent produces files that are larger than a few hundred KiB, or larger than `MaxEventBytes` (4 MiB). Skip it for text, JSON and small images. With no store registered, nothing changes except the caps.

| Without a store | With a store |
|---|---|
| Bytes are in the task row, in the event log and once per webhook. | Only a short link is in all three. |
| One event holds at most `MaxEventBytes`. | A file can be up to `MaxObjectBytes` (5 GiB). |
| `GetTask` returns all bytes. | `GetTask` returns links. The caller downloads what it needs. |

## Options

Set them on <xref:A2A.Server.A2AServerOptions.ArtifactStorage>. They are members of <xref:A2A.Server.ArtifactStorageOptions>.

| Option | Default | Meaning |
|---|---|---|
| `InlineThresholdBytes` | 256 KiB | A raw part of this size or smaller stays inline. |
| `MaxObjectBytes` | 5 GiB | Largest single object. A longer upload throws `ArtifactObjectTooLargeException`. |
| `PublicBaseUrl` | none | Base of artifact links. Required when a store is registered. |
| `PublicBaseUrlResolver` | none | A function from tenant to base URL, for tenants on different origins. It wins over `PublicBaseUrl`. |
| `Delivery` | `Redirect` | `Redirect` or `Proxy`. See [Serving downloads](#serving-downloads). |
| `DownloadUrlLifetime` | 5 minutes | How long a presigned URL (a time-limited storage URL that carries its own signature) works. |
| `OrphanGracePeriod` | 1 hour | How old an unreferenced object must be before the sweep deletes it. Must be greater than zero. |
| `OrphanSweepInterval` | 6 hours | How often one instance runs the orphan step. Must be greater than zero. |

## Providers

A store is a class that derives from <xref:A2A.Server.Storage.ArtifactObjectStore>. Register one instance with DI. A provider implements `PutAsync`, `OpenReadAsync`, `DeleteAsync` and `ListAsync`. It can override `ExistsAsync` (the default opens the object and closes it) and `TryCreateDownloadUrlAsync` (the default gives no presigned URL).

| Package | Backend |
|---|---|
| `RomanAmbinder.A2A.Server` | `InMemoryArtifactObjectStore`: tests and development. Objects are lost on restart. |
| `RomanAmbinder.A2A.Server.Storage.S3` | `S3ArtifactObjectStore`: Amazon S3 and S3-compatible services such as MinIO. Register with `AddA2AS3ArtifactStore`. |
| `RomanAmbinder.A2A.Server.Storage.AzureBlob` | `AzureBlobArtifactObjectStore`: Azure Blob Storage block blobs. Register with `AddA2AAzureBlobArtifactStore`. |

Both cloud providers use a `KeyPrefix` option. A bucket or container plus prefix belongs to exactly one task store: a prefix is normalized to end with `/`, so `prod` and `prod2` do not see each other, but two task stores must not share one prefix, or the sweep deletes the other store's objects.

Both cloud providers upload 8 MiB at a time, so a stream of unknown length never needs a full buffer, and a failed or over-limit upload leaves no object. Both give a presigned URL (S3 presigned URL, Azure SAS URL) that forces `Content-Disposition: attachment`, so a stored HTML file downloads and never renders from the storage origin. The Azure provider makes SAS URLs only when its client holds the account key; with another credential it gives no URL and the server proxies the download.

Presigned S3 URLs are built from `ServiceUrl`. With an internal endpoint (for example `http://minio:9000`), clients cannot reach the URL. Use `Delivery = Proxy`, or give a `ServiceUrl` that clients can reach.

The S3 credentials need `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`, `s3:AbortMultipartUpload`, and `s3:PutObjectTagging` / `s3:GetObjectTagging`. The tagging permissions are required: the provider stamps the completion time of a multipart object as a tag, because S3 dates it from the start of the upload and the orphan sweep ages objects by that date. The Azure credentials need to read, write, list and delete blobs in the container.

Startup logs a warning (event 255) when a durable task store is combined with `InMemoryArtifactObjectStore`: stored tasks would keep links that no longer download.

## Turn it on

A runnable version with S3 (MinIO) and Azure Blob (Azurite) in Docker is in [samples/ArtifactStorage](https://github.com/roman-ambinder/a2a/blob/main/samples/ArtifactStorage/README.md).

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#Register)]

Startup fails with `InvalidOperationException` when a store is registered and neither `PublicBaseUrl` nor `PublicBaseUrlResolver` is set. The link base must be the origin that clients send credentials to. Usually that is the origin of the agent card. A client sends credentials only to the card's origin, so a link on another origin cannot be downloaded.

Map the download route next to `MapA2A`, with the same pattern and the same authorization. With `AddApiKeyAuthentication`, as here, both get the API-key policy; with your own policy, call `RequireAuthorization(...)` on both:

[!code-csharp[](../../../samples/ArtifactStorage/Program.cs#Map)]

<xref:A2A.Server.AspNetCore.A2AEndpointRouteBuilderExtensions.MapA2AArtifacts*> adds `GET {pattern}/artifacts/{tenant}/{taskId}/{objectId}`. Like `MapA2A`, it needs a policy, or the startup authorization check fails the app.

When tenants are served from different origins, give the base per tenant:

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#Resolver)]

## Produce a file

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#Produce)]

<xref:A2A.Server.TaskUpdater.AddArtifactFromStreamAsync*> is the way to produce a large file. It uploads the stream directly, and hashes while it uploads. The bytes never pass through an event, so `MaxEventBytes` does not apply. It emits one sealed artifact with one `url` part and returns the artifact id. It throws `InvalidOperationException` when no store is registered.

`AddArtifactAsync` and `AddHashedArtifactAsync` also offload. A new artifact whose raw part is larger than `InlineThresholdBytes` is uploaded, and the part is replaced by a `url` part. The media type and file name carry over. This path first needs the whole file in memory as a `RawPart`, so it suits files that are only a little over the threshold.

**Appended chunks stay inline.** A call with `append: true` is never offloaded. Offloading each chunk would give the client many links instead of one file. They count toward `MaxEventBytes` and `MaxTaskBytes`.

**Do not chunk a large file.** If the first chunk is over the threshold, it is offloaded, and the later `append` chunks stay inline. The client then gets `[url, raw, raw...]`. For a large file, use `AddArtifactFromStreamAsync`.

The upload happens before the event. A failed upload throws at the call and emits nothing. A crash after the upload and before the commit leaves an object that no task refers to. The [sweep](#cleanup) removes it. Never the other way round: a task never points at an object that was not written.

## What the part looks like

A part made by the store is an ordinary `url` part. Its metadata has two keys:

| Key | Value |
|---|---|
| `sha256` | `sha256:` and the lowercase hex hash of the object's bytes. |
| `sizeBytes` | The size of the object in bytes. |

The link is `{base}/artifacts/{tenant}/{taskId}/{objectId}`. `{tenant}` is the URL-encoded tenant, or `_` for the default tenant. The object id is random. The link holds no secret and never expires, so the same bytes of the event can go to the event log, streams and webhooks. A tenant of `_`, and a tenant or task id of `.` or `..`, cannot be part of a link: the upload throws `ArgumentException`.

## Two kinds of `sha256`

There are two hashes. They answer different questions.

- The **part** `sha256` is the hash of the object's bytes. The downloader checks it.
- The **artifact** `sha256` is the integrity hash from [Streaming](streaming.md#the-optional-hash). It covers the delivered parts, so a `url` part counts as its URL string, not the file behind it. `ArtifactVerifier` checks it. `AddArtifactFromStreamAsync` and `AddHashedArtifactAsync` set it. The hash API is `[Experimental]` (`A2AEXP003`).

Verify both. The artifact hash tells you the link was not changed. The part hash tells you the file was not changed.

## Download a file

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#Download)]

<xref:A2A.Client.A2AArtifactDownloader> is for .NET 10 (not netstandard2.0). `IA2AClient` does not change.

- It takes the **handler** that adds your A2A credentials, not an `HttpClient`. That handler must not follow redirects. The constructor throws `ArgumentException` for a `SocketsHttpHandler` or `HttpClientHandler` that does, also nested under a `DelegatingHandler`. It cannot see inside a handler of another type. Such a handler must not follow redirects either. As a backstop, a response that shows a followed redirect throws `InvalidOperationException`. The handler is not disposed.
- A URL on any other origin than the agent's throws `InvalidOperationException`, and no request is sent. The class never sends credentials to another origin.
- On a 302 or 307, it fetches the `Location` once, with a separate client that has no credentials and the `SafeHttp` address guard. A second redirect throws. By default only public HTTPS hosts are allowed. A private store such as MinIO needs an option:

[!code-csharp[](../../../samples/ArtifactStorage/Program.cs#PrivateStore)]

- It checks `sizeBytes` and the part `sha256` when they are in the metadata, and throws `InvalidDataException` on a mismatch or when the body is larger than `maxBytes`. On a failure the destination can hold a partial write, so discard it.

### Clients from other SDKs

Downloads need the caller's A2A credentials. There are no signed links that work without them. A client from another SDK that just follows the `url` gets a 401. Its app must attach the same credentials it uses for A2A calls, to the link's origin.

Turn off automatic redirects in that client. In `Redirect` mode the server answers 302 to the storage host. Most HTTP libraries strip only the `Authorization` header on a cross-origin redirect, so a header API key (for example `X-API-Key`) would go to the storage host. If your clients use header API keys, set `Delivery = Proxy`.

## Serving downloads

The route answers every miss with HTTP 404 (a `Range` start past the end of the object is 416): no such task, a task of another owner or tenant, a deleted task, an object id of another task, a missing object. A caller learns nothing about which case it was.

1. The tenant comes from the link. When your tenant resolver returns another non-empty tenant, the answer is 404.
2. The read is authorized as `GetTask`. A host that denies `GetTask` denies the bytes. There is no new operation to allow.
3. The task is loaded with the caller's key. An object is out of reach once its task is gone.

Then, by `Delivery`:

| Mode | What happens |
|---|---|
| `Redirect` (default) | The server checks the object exists, then answers 302 to a presigned URL when the provider has one. A missing object is a 404, never a redirect to a dead URL. A provider with no presigned URLs, such as the in-memory store, streams through the server. |
| `Proxy` | Always streams through the server. |

A request with a `Range` header is always streamed by the server, in both modes, because a presigned URL cannot be limited to the range. A single `bytes=first-last` or `bytes=first-` range is honored. Any other form is ignored and the whole object is sent.

Every response has `X-Content-Type-Options: nosniff` and `Cache-Control: private, no-store`. A streamed response also has `Content-Disposition: attachment`. A redirected download has no file name, because the presigned URL forces a plain `attachment`. A presigned URL is the store's own and never reaches the caller except as the redirect target.

## Cleanup

An object lives and dies with its task. There are no separate retention periods.

- `A2ATaskAdmin.DeleteTaskAsync` deletes the task row first, then every object under the task. Objects first would risk a task that points at nothing. A failed delete is logged (event 236) and left to the sweep.
- The [retention sweep](retention.md#what-a-sweep-does) has a step for orphan objects:
  - It lists objects older than `OrphanGracePeriod`. It deletes those whose task is gone or no longer refers to them.
  - It reads each task without its owner, so a live task of anyone keeps its objects.
  - Any doubt keeps the object: a task it cannot read, a key that is not the SDK's own.
  - At most `BatchSize` objects go per sweep.
  - Listing a bucket is slow and costs money, so one instance runs this step at most once per `OrphanSweepInterval` (6 hours by default), not on every sweep. A run that stops at `BatchSize` does not count: the next sweep continues, so a backlog drains.

The sweep runs only with `AddRetention`. Without it, orphans stay. Keep `OrphanGracePeriod` longer than your slowest upload plus commit: a younger object may belong to a task that is not committed yet.

Encryption at rest is the bucket's job. The SDK stores the bytes as given. Turn on server-side encryption and access logging at the storage service, and keep the bucket private. In `Proxy` mode the SDK never gives a client a bucket name. In `Redirect` mode the client sees a presigned URL that names it, for a few minutes.

## Accept large files from clients

The store holds agent output. A file that a client sends stays inline in the message, so it is bound by `MaxRequestBodyBytes` (10 MiB). Inbound uploads lift that. The client uploads the file first, gets a normal `url` part, and puts that part in its message. The A2A wire format does not change.

### Turn it on

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#EnableUploads)]

Uploads are off by default. `MapA2AUploads` throws `InvalidOperationException` when `Uploads.Enabled` is false, when no store is registered, or when an option is out of range. It maps `POST {pattern}/uploads/{tenant}` and `GET {pattern}/uploads/{tenant}/{objectId}`, where `{tenant}` is `_` for the default tenant. Like `MapA2A`, it needs a policy, or the startup authorization check fails the app. It uses the `"a2a"` rate limit policy.

Set the options on `ArtifactStorage.Uploads` (<xref:A2A.Server.ArtifactUploadOptions>):

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | false | Turns on the upload routes' checks, the message check, the agent read and the purge. |
| `MaxUploadBytes` | 1 GiB | Largest file. Must be at most `MaxObjectBytes`. |
| `MaxStoredBytesPerCaller` | 10 GiB | Stored bytes per tenant and principal. |
| `MaxUploadsPerCaller` | 100 | Stored files per tenant and principal. Also the most distinct upload links in one message. |
| `MaxConcurrentUploadsPerCaller` | 2 | Uploads running at once, per caller, per instance. |
| `UnusedUploadTtl` | 24 hours | A new message can refer to an upload only this long after the write. |

Startup logs a warning (event 682) when uploads are on and the retention sweep is off, because nothing would purge the unused files.

### The flow

1. The client sends `POST {pattern}/uploads/{tenant}` with the raw file as the body. `Content-Type` is the media type. `Content-Disposition: attachment; filename*=UTF-8''report.pdf` is the optional file name.
2. The server streams the body to the store. It never reads the body into memory, and the body may be larger than `MaxRequestBodyBytes`. It answers 201 with the JSON of a `url` part: `url`, `mediaType`, `filename`, and `sha256` and `sizeBytes` in `metadata`. The `Location` header holds the link.
3. The client puts the part in a message and sends it. Before the message is stored, the server checks the link and writes a reference to it.
4. The agent reads the file with `OpenUploadAsync`.
5. The owner can read the file back with `GET {pattern}/uploads/{tenant}/{objectId}`.

The link is `{base}/uploads/{tenant}/{objectId}`. It holds no secret and no owner. The upload is stored under the caller's own tenant and principal, so nobody else can find it.

### Client helper

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#UploadFile)]

<xref:A2A.Client.A2AFileUploader> is for .NET 10, like the downloader.

- It takes the **handler** that adds your A2A credentials, and refuses one that follows redirects, for the same reason as the downloader.
- It hashes the bytes while it sends them. It throws `InvalidDataException` when the `sha256` or `sizeBytes` in the answer is missing or differs.
- It throws `InvalidOperationException` when the returned link is on another origin than the upload URL, so credentials never go elsewhere.
- It sets no client timeout, because large files take time. Cancel with the token.
- It reads the stream once. A stream that cannot seek is fine.

### Read the file in the agent

[!code-csharp[](../../../samples/Snippets/ArtifactStorage.cs#ReadUpload)]

<xref:A2A.Server.AgentRequestContext.OpenUploadAsync*> returns null when the part is not the link of one of the caller's own uploads. That covers an ordinary URL, a file of another owner or tenant, and a missing file. It also returns null when uploads are not enabled. It throws `A2AException` with `Unauthenticated` for an anonymous caller. The bytes stream from the store. Dispose the result.

### Security rules

- **Authentication.** Upload, read and agent read always need an authenticated caller, also when `AllowAnonymous` is set. All anonymous callers share one principal, so they could read each other's files. An anonymous caller gets `Unauthenticated` (HTTP 401).
- **Authorization.** The calls run through the operation pipeline as the extension operations `a2a.files.upload` and `a2a.files.read`. The route's policy decides at the HTTP edge, and the default authorization handler then allows what the route admitted. **A custom `A2AAuthorizationHandler` must allow these two names**, or every upload and owner read is denied. The agent read needs no name of its own: the call was authorized as `SendMessage`.
- **Owner and tenant.** The storage key is built from the caller's own tenant and principal. Another owner's file is not found: there is no owner field to get wrong. The route tenant must agree with your tenant resolver. Every miss on `GET` is 404.
- **Confused deputy.** A message must not make the agent read another owner's file. A URL under `{base}/uploads/` must be the exact link of an upload that the caller owns and that is not older than `UnusedUploadTtl`. Otherwise the message is refused with `InvalidParams` and one fixed text, whatever the reason. Other URLs are ordinary URLs and are left alone.
- **Size.** A `Content-Length` over `MaxUploadBytes` is refused with 413 before the body is read. A chunked body is cut off at the same limit. When `Content-Length` was sent and the body has another length, the file is deleted and the call is refused.
- **Input.** An unparsable `Content-Type` is `InvalidParams`. The file name loses any path. A name of more than 255 characters (or 1024 when percent-encoded), with a control character, or that is `.` or `..` is refused.

Downloads use the same `attachment`, `nosniff` and `no-store` headers as artifact downloads, and honor a single `Range`.

### Quota

The store is the source of truth, so the quota holds across instances and needs no table.

1. The upload takes a concurrency slot for the caller on this instance. With none free, the answer is 429 with `Retry-After: 1`.
2. It lists the caller's uploads and sums their count and size. At either limit, the answer is 429 with `Retry-After` set to `OrphanSweepInterval`.
3. A `Content-Length` larger than the bytes left in the quota is 429 before the body is read. The write is capped at the smaller of `MaxUploadBytes` and the bytes left in the quota. A stream past `MaxUploadBytes` is 413. A stream past the quota is 429.
4. After the write it lists again. If the total is over a limit, it deletes the new file and answers 429.

**The over-limit race.** Two uploads that run at the same time, on one instance or on two, can both pass step 2. Step 4 sees both files, so both can delete theirs. That fails safe: neither upload is kept, and the caller retries. A refused upload never leaves a file behind. Cleanup deletes do not stop when the caller disconnects.

### Purge

Nothing deletes an upload while a message can still use it. When the server accepts a message, it writes a small reference object for each upload link: `_uploadrefs/{tenant}/{ownerHash}/{objectId}/{taskId}`. The upload itself is `_uploads/{tenant}/{ownerHash}/{objectId}`. The owner hash is the SHA-256 of the principal, so bucket keys hold no user ids. Both key forms have more segments than an artifact key, so the orphan step of the sweep leaves them alone.

With `AddRetention`, the sweep has a step for unused uploads. It runs at most once per `OrphanSweepInterval`, and deletes at most `BatchSize` objects per sweep. A run that fails or stops at `BatchSize` does not count, so a backlog drains.

- It looks at uploads older than `UnusedUploadTtl + OrphanGracePeriod`. The window to attach a new message closes at `UnusedUploadTtl`, so a reference written at the last moment is always seen.
- A reference younger than `OrphanGracePeriod` keeps the upload, because its message may be committing.
- A task that exists and still refers to the link keeps the upload. Otherwise the reference is deleted.
- An upload with no live reference is deleted. References go first, so a crash leaves an upload with no reference, and the next sweep deletes it.
- A task that cannot be read keeps the upload.
- References older than `OrphanGracePeriod` whose upload is gone are deleted.

Deleting a task does not touch its uploads. The next sweep finds the task gone, and purges them. Hosts that turn `Enabled` off keep their old uploads until they clear them. The counter `a2a.server.artifact.uploads.purged` counts the uploads the sweep deleted.

Out of scope: an owner delete, resumable or multipart uploads, a virus scan, a media type allow-list, a gRPC upload, and an entry on the agent card (it would change the wire format).

## Observability

On the `A2A.Server` meter (see [Observability](observability.md#metrics)):

| Counter | Counts |
|---|---|
| `a2a.server.artifact.objects.written` | Objects written to the store, inbound uploads included. |
| `a2a.server.artifact.bytes.written` | Bytes written to the store, inbound uploads included. |
| `a2a.server.artifact.bytes.served` | Bytes the server streamed to callers. Redirects are not counted. |
| `a2a.server.artifact.orphans.deleted` | Orphan objects the sweep deleted. |
| `a2a.server.artifact.uploads.purged` | Unused inbound uploads the sweep deleted. |

Each write to the store has a span, `a2a.artifact.upload`.

## What can go wrong

- **Startup fails.** A store is registered with no `PublicBaseUrl` or resolver.
- **The client gets 401 on the link.** The link origin is not the card origin, or a client from another SDK sent no credentials.
- **The downloader throws on the origin.** The link base does not match the origin you passed. Fix `PublicBaseUrl`.
- **404 on a file that exists.** The caller is another owner or tenant, the task is deleted, or the resolver tenant differs from the link tenant.
- **Links break after a restart.** A durable task store with the in-memory object store. Look for warning 255.
- **Objects pile up.** Retention is off, so the sweep never runs.
- **Every link is 404.** A store is registered but `MapA2AArtifacts` was never called.
- **The sweep deletes objects of live tasks.** The task database was restored from an old backup, or points at the wrong database. The sweep sees no task for those objects and treats them as orphans.
- **A redirected download has no file name.** Only streamed responses carry `Content-Disposition` with the name.
- **A link 404s for one tenant or task.** Its tenant or task id contains `/`. Links split the path on `/`, so use ids without it.

## See also

- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Streaming](streaming.md)
- [Retention](retention.md)
- [Auth and ownership](auth-and-ownership.md)
- [Client credentials](client-credentials.md)
- [Durable stores](durable-stores.md)
