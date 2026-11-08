# Artifact storage

By default a task is one row, and every artifact byte lives inside it. That is fine for text and small files. It is a poor fit for large files: every event rewrites the whole task, and `GetTask` returns all the bytes. An artifact object store keeps large bytes outside the task. The task holds a normal `url` part instead.

This guide covers the size caps, when to use a store, how to turn it on, how a caller downloads a file, and how the bytes are cleaned up.

## Size caps come first

Two caps apply to every task, with or without a store (see [Limits and rate limiting](limits-and-rate-limiting.md)):

- <xref:A2A.Server.A2ALimits.MaxTaskBytes> (64 MiB) is the total content of a stored task: history and inline artifacts.
- <xref:A2A.Server.A2ALimits.MaxArtifactsPerTask> (1024) is the number of artifacts.

An agent event past a cap fails the task and stops the agent. A follow-up message past `MaxTaskBytes` is refused with `UnsupportedOperation`. A `url` part counts as its URL length, so a file in a store costs the task almost nothing.

## When to use a store

Use a store when an agent produces files that are larger than a few hundred KiB, or larger than `MaxEventBytes` (4 MB). Skip it for text, JSON and small images. With no store registered, nothing changes except the caps.

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
| `DownloadUrlLifetime` | 5 minutes | How long a presigned URL works. |
| `OrphanGracePeriod` | 1 hour | How old an unreferenced object must be before the sweep deletes it. Must be greater than zero. |
| `OrphanSweepInterval` | 6 hours | How often one instance runs the orphan step. Must be greater than zero. |

## Providers

A store is a class that derives from <xref:A2A.Server.Storage.ArtifactObjectStore>. Register one instance with DI. A provider implements `PutAsync`, `OpenReadAsync`, `DeleteAsync` and `ListAsync`. It can override `ExistsAsync` (the default opens the object and closes it) and `TryCreateDownloadUrlAsync` (the default gives no presigned URL).

| Package | Backend |
|---|---|
| `A2A.Server` | `InMemoryArtifactObjectStore`: tests and development. Objects are lost on restart. |
| `A2A.Server.Storage.S3` | `S3ArtifactObjectStore`: Amazon S3 and S3-compatible services such as MinIO. Register with `AddA2AS3ArtifactStore`. |
| `A2A.Server.Storage.AzureBlob` | `AzureBlobArtifactObjectStore`: Azure Blob Storage block blobs. Register with `AddA2AAzureBlobArtifactStore`. |

Both cloud providers use a `KeyPrefix` option. A bucket or container plus prefix belongs to exactly one task store: a prefix is normalized to end with `/`, so `prod` and `prod2` do not see each other, but two task stores must not share one prefix, or the sweep deletes the other store's objects.

Both cloud providers upload 8 MiB at a time, so a stream of unknown length never needs a full buffer, and a failed or over-limit upload leaves no object. Both give a presigned URL (S3 presigned URL, Azure SAS URL) that forces `Content-Disposition: attachment`, so a stored HTML file downloads and never renders from the storage origin. The Azure provider makes SAS URLs only when its client holds the account key; with another credential it gives no URL and the server proxies the download.

Presigned S3 URLs are built from `ServiceUrl`. With an internal endpoint (for example `http://minio:9000`), clients cannot reach the URL. Use `Delivery = Proxy`, or give a `ServiceUrl` that clients can reach.

The S3 credentials need `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`, `s3:AbortMultipartUpload`, and `s3:PutObjectTagging` / `s3:GetObjectTagging`. The tagging permissions are required: the provider stamps the completion time of a multipart object as a tag, because S3 dates it from the start of the upload and the orphan sweep ages objects by that date. The Azure credentials need to read, write, list and delete blobs in the container.

Startup logs a warning (event 255) when a durable task store is combined with `InMemoryArtifactObjectStore`: stored tasks would keep links that no longer download.

## Turn it on

[!code-csharp[](../snippets/ArtifactStorage.cs#Register)]

Startup fails with `InvalidOperationException` when a store is registered and neither `PublicBaseUrl` nor `PublicBaseUrlResolver` is set. The link base must be the origin that clients send credentials to. Usually that is the origin of the agent card. A client sends credentials only to the card's origin, so a link on another origin cannot be downloaded.

Map the download route next to `MapA2A`, with the same pattern and the same authorization:

[!code-csharp[](../snippets/ArtifactStorage.cs#Map)]

<xref:A2A.Server.AspNetCore.A2AEndpointRouteBuilderExtensions.MapA2AArtifacts*> adds `GET {pattern}/artifacts/{tenant}/{taskId}/{objectId}`. Like `MapA2A`, it needs a policy, or the startup authorization check fails the app.

When tenants are served from different origins, give the base per tenant:

[!code-csharp[](../snippets/ArtifactStorage.cs#Resolver)]

## Produce a file

[!code-csharp[](../snippets/ArtifactStorage.cs#Produce)]

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

[!code-csharp[](../snippets/ArtifactStorage.cs#Download)]

<xref:A2A.Client.A2AArtifactDownloader> is for .NET 8 and later. `IA2AClient` does not change.

- It takes the **handler** that adds your A2A credentials, not an `HttpClient`. That handler must not follow redirects. The constructor throws `ArgumentException` for a `SocketsHttpHandler` or `HttpClientHandler` that does, also nested under a `DelegatingHandler`. It cannot see inside a handler of another type. Such a handler must not follow redirects either. As a backstop, a response that shows a followed redirect throws `InvalidOperationException`. The handler is not disposed.
- A URL on any other origin than the agent's throws `InvalidOperationException`, and no request is sent. The class never sends credentials to another origin.
- On a 302 or 307, it fetches the `Location` once, with a separate client that has no credentials and the `SafeHttp` address guard. A second redirect throws. By default only public HTTPS hosts are allowed. A private store such as MinIO needs an option:

[!code-csharp[](../snippets/ArtifactStorage.cs#PrivateStore)]

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
- The [retention sweep](retention.md#what-a-sweep-does) has a last step for orphan objects.
- It lists objects older than `OrphanGracePeriod`. It deletes those whose task is gone or no longer refers to them.
- It reads each task without its owner, so a live task of anyone keeps its objects.
- Any doubt keeps the object: a task it cannot read, a key that is not the SDK's own.
- At most `BatchSize` objects go per sweep.
- Listing a bucket is slow and costs money, so one instance runs this step at most once per `OrphanSweepInterval` (6 hours by default), not on every sweep. A run that stops at `BatchSize` does not count: the next sweep continues, so a backlog drains.

The sweep runs only with `AddA2ARetention`. Without it, orphans stay. Keep `OrphanGracePeriod` longer than your slowest upload plus commit: a younger object may belong to a task that is not committed yet.

Encryption at rest is the bucket's job. The SDK stores the bytes as given. Turn on server-side encryption and access logging at the storage service, and keep the bucket private. In `Proxy` mode the SDK never gives a client a bucket name. In `Redirect` mode the client sees a presigned URL that names it, for a few minutes.

## Observability

On the `A2A.Server` meter (see [Observability](observability.md#metrics)):

| Counter | Counts |
|---|---|
| `a2a.server.artifact.objects.written` | Objects written to the store. |
| `a2a.server.artifact.bytes.written` | Bytes written to the store. |
| `a2a.server.artifact.bytes.served` | Bytes the server streamed to callers. Redirects are not counted. |
| `a2a.server.artifact.orphans.deleted` | Orphan objects the sweep deleted. |

Each upload has a span, `a2a.artifact.upload`.

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
