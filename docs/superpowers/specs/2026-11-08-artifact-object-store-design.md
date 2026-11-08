# Artifact object store and task size caps

Status: proposed. Closes the storage side of gap S40. Replaces "item 9, large artifact offload" from the 2026-11-08 suggestions.

## Why

A task is one row. In Postgres, `a2a_tasks.task` is one JSON `text` column holding status, full history and every artifact. Every event loads that record, changes it and rewrites the column (`TaskExecutionEngine.ApplyAndStoreAsync`, `TaskStore.CommitAsync`).

That causes real problems:

- Streaming an artifact in N chunks rewrites all earlier chunks each time, so write cost grows with N². This design bounds that with the cap, and gives large files a path that avoids it (`AddArtifactFromStreamAsync`). Appended chunks themselves stay inline.
- Nothing caps a task's total size. `A2ALimits.MaxEventBytes` (4 MB) caps one event only.
- The same bytes sit in the task row, in `a2a_events`, and in `a2a_push_outbox` once per webhook.
- `GetTask` returns every artifact. A caller who wants the status still gets all the bytes.
- Large files cannot be produced at all without the agent uploading them itself.

No one chose this. It was the simplest path, and the gap analysis already said to prefer `url` parts for large files.

## Goals

1. Stop any one task from growing without bound (caps).
2. Keep large artifact bytes out of the database, in an object store.
3. Let an agent produce a file larger than any event limit.
4. Let a caller download one artifact, and verify it.
5. Stay on the A2A v1.0 wire format: a large artifact is a normal `url` part.

## Non-goals

- A byte-range or partial-artifact protocol extension.
- Offloading text or data parts. They stay inline, under the caps.
- Signed links that work without credentials. Decided for this version: downloads need the caller's normal A2A credentials. A client from another SDK that sends none gets a 401, and its app has to attach them. Can be added later if someone needs it.
- Moving existing inline artifacts to the object store.
- Deduplicating content across tasks.
- The client-side work ledger (separate proposal).
- Separate retention periods for results and history. Objects live and die with their task.

## Anti-goals

These would make this a failure even if it works:

- A task that points at an object that does not exist because of a crash.
- Any caller other than the task's owner reading an object through our endpoint.
- Credentials sent to the object store host, or to any origin other than the agent's.
- Storage URLs or bucket names leaking to clients.
- Any behavior change for someone who configures no object store, other than the caps.

## Constraints from the code

- Events are encoded once, and the same bytes go to the log, streams and webhooks (`TaskExecutionEngine`, "Encoded once"). Links therefore cannot be created at read time without giving this up. **Links must be stable.**
- `ArtifactHash` hashes a URL part as its URL string, not the content behind it. `TaskUpdater.AddHashedArtifactAsync` computes the artifact hash (`AgentExecutor.cs`), under a synchronous lock in `Track`. **Offload must finish before that step**, outside the lock, or client verification breaks.
- Our client transports refuse redirects, and credentials must never cross origins (S37, CVE-2026-111763).
- `A2A.Protocol` and `A2A.Client` also target netstandard2.0. Build all target frameworks. `SafeHttp` is not available on netstandard2.0.
- `A2ACredentialHandler` throws for a foreign origin; it does not skip credentials.
- `TaskStore.GetAsync` needs the owner and treats a wrong owner as missing. Object keys have no owner.
- Public API is tracked in `PublicAPI.Unshipped.txt`. Stores are abstract classes in this repo.
- New A2A endpoints need the `"A2A "` display-name prefix so the startup auth check covers them.

## Part 1: size caps (ships first, separate PR)

Add to `A2ALimits`:

| Option | Default | Meaning |
|---|---|---|
| `MaxTaskBytes` | 64 MiB | Total serialized size of a stored task, history and inline artifacts included |
| `MaxArtifactsPerTask` | 1024 | Number of distinct artifacts on a task |

An event that would pass a cap is never stored. The task fails and the agent is stopped, exactly as for `MaxEventBytes` today (generic status text, a log entry, `CancelAll`). The failure event itself must always be storable, even at the cap.

A task already over a cap fails on its next event, not on load.

A client follow-up message is not an event: `A2ARequestHandler` adds it to history with `UpdateAsync`. A follow-up that would pass `MaxTaskBytes` is refused with `UnsupportedOperation` and the task is left unchanged, as `MaxTurnsPerTask` does. That makes two enforcement points: the engine for events and the handler for follow-ups.

History gets no cap of its own: it counts toward `MaxTaskBytes`. (`MaxTurnsPerTask` is off by default, so the byte cap is what limits history.)

## Part 2: object store

### Abstraction

`ArtifactObjectStore`, an abstract class in `A2A.Server`:

- `PutAsync(key, Stream content, ObjectInfo info, ct)`, returning size and SHA-256. It reads a stream and must not buffer the whole object.
- `OpenReadAsync(key, ct)`, returning stream, content type, length, or null.
- `DeleteAsync(key, ct)`, idempotent.
- `ListAsync(prefix, ct)`, with each object's creation time, for the sweeper.
- `ExistsAsync(key, ct)`, a virtual member that defaults to `OpenReadAsync`. `Redirect` mode calls it first, because a presigned URL does not prove the object exists.
- `TryCreateDownloadUrlAsync(key, TimeSpan lifetime, ct)`, returning a presigned URL or null when the provider has none.

Object key layout: `{tenant}/{taskId}/{objectId}`. `objectId` is a random 128-bit value. Tenant and task ids must be encoded so that no id can change the path. Key layout is internal to the SDK. The ids `.` and `..` are refused as tenant or task id when a link is built, because a URL parser would remove them from the path.

Providers:

| Package | Backend |
|---|---|
| `A2A.Server` | `InMemoryArtifactObjectStore`: tests and development |
| `A2A.Server.Storage.S3` | S3 and S3-compatible (MinIO) |
| `A2A.Server.Storage.AzureBlob` | Azure Blob |

No object store registered means today's behavior: everything stays inline.

### Options

`A2AServerOptions.ArtifactStorage`:

| Option | Default | Meaning |
|---|---|---|
| `InlineThresholdBytes` | 256 KiB | A raw part at or below this stays inline |
| `MaxObjectBytes` | 5 GiB | Largest single object |
| `PublicBaseUrl` | none | Required when a store is registered. Base of artifact links. Startup fails without it |
| `PublicBaseUrlResolver` | none | Optional `Func<string tenant, Uri>`, for tenants served from different origins. Wins over `PublicBaseUrl` |
| `Delivery` | `Redirect` | `Redirect` or `Proxy`. See "Serving" |
| `DownloadUrlLifetime` | 5 minutes | Lifetime of a presigned URL |
| `OrphanGracePeriod` | 1 hour | How old an unreferenced object must be before the sweeper deletes it |

### Write path

Offload happens in `TaskUpdater`, before the artifact hash runs. This is the key decision. The event store, outbox and push code do not change. The engine changes only to give `TaskUpdater` what it now needs (tenant, store, options) when it creates one (`TaskExecutionEngine.cs`, near line 323).

1. `AddArtifactAsync` and `AddHashedArtifactAsync` look at each `RawPart` larger than `InlineThresholdBytes`, unless `append` is true. Upload runs before `Track` and outside its lock.
2. It uploads the bytes and replaces the part with a `UrlPart` holding the stable link.
3. It adds part metadata: `sha256` (the content hash) and `sizeBytes`. Media type and file name carry over.
4. For `AddHashedArtifactAsync`, the artifact hash then runs as usual, over the delivered parts.
5. The event goes to the engine like any other.

An upload failure throws to the agent's code at the call.

**Appended chunks are never offloaded.** `append: true` stays inline, under `MaxEventBytes` and `MaxTaskBytes`. Offloading per chunk would give the client many links in place of one file. To produce a large file, use the stream method below.

New method for files of any size:

```csharp
await updater.AddArtifactFromStreamAsync(stream, mediaType, name: "report.pdf", cancellationToken: ct);
```

It uploads the stream directly, without passing through events or `MaxEventBytes`, hashes while uploading, and enforces `MaxObjectBytes`. It emits one artifact with one `UrlPart`, sealed, with part-level `sha256` and `sizeBytes` and the artifact-level `sha256` of the delivered parts. When no store is registered it throws `InvalidOperationException` with a clear message.

Order is object first, then commit. A crash between them leaves an orphaned object, never a dangling reference. An event dropped later (a sealed artifact, a terminal task) also leaves an orphan. The sweeper handles both.

### Links

Stored form, and what every reader sees:

```
{base}/artifacts/{tenant}/{taskId}/{objectId}
```

`{base}` is `PublicBaseUrlResolver(tenant)` or `PublicBaseUrl`. `{tenant}` is the URL-encoded tenant, or `_` for the default tenant. It should be the origin of that tenant's card, or clients will not send credentials.

The URL holds no secret and never expires, so the encoded event bytes can be kept as they are. It also goes into `a2a_events` and the push outbox as ordinary text. The caller uses the same credentials as for A2A calls.

### Serving

`MapA2AArtifacts()` in `A2A.Server.AspNetCore`: `GET {pattern}/artifacts/{artifactTenant}/{taskId}/{objectId}`. It has an `"A2A "` display name, the `"a2a"` rate limit policy, and is covered by the startup auth check.

1. Build the call context like any A2A request. The tenant comes from the link. If `ITenantResolver` returns a non-empty tenant that differs from the link's, answer 404.
2. Authorize through the operation pipeline **as `GetTask`**. Downloading is reading the task, so a host that denies `GetTask` denies the bytes, and no new `A2AOperation` value is needed.
3. Load the task with the caller's `TaskKey`. No task means 404, the same as for a missing object. An object is unreachable once its task is gone.
4. Build the object key from the tenant, task id and object id. An id from another task finds nothing.

Then, by `Delivery`:

- `Redirect`: when the object exists (`ExistsAsync`, otherwise 404), 302 to a presigned URL when the provider gives one, otherwise proxy.
- `Proxy`: stream through this server.

Responses set `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` and `Cache-Control: private, no-store`. Proxy mode supports `Range`.

### Client

`A2AArtifactDownloader`, a new class, net8+ only (like `ArtifactVerifier`, since `SafeHttp` is not on netstandard2.0). `IA2AClient` does not change.

```csharp
var downloader = new A2AArtifactDownloader(agentHandler, agentOrigin);
await downloader.DownloadAsync(urlPart, destination, maxBytes, ct);
```

- `agentHandler` is the caller's `HttpMessageHandler` stack that adds credentials, the one used for A2A calls. It works for gRPC clients too, because it is passed in separately. `agentOrigin` comes from the card.
- A URL on any other origin throws: this class never sends a request with credentials off-origin.
- `agentHandler` must not follow redirects. The constructor throws `ArgumentException` for a `SocketsHttpHandler` or `HttpClientHandler` that does, also nested under a `DelegatingHandler`, and the class throws when a response shows a followed redirect. On 302, the class requests the `Location` once with a separate `SafeHttp` client that has no credentials. `SafeHttpOptions` lets an operator allow a private store such as MinIO.
- Checks `sizeBytes` and `sha256` from part metadata when present, and throws on a mismatch.

### Integrity

Two levels, each with a clear meaning:

- The artifact-level `sha256` (S27) is unchanged and covers the delivered parts, so a `url` part counts as its URL string.
- The part-level `sha256` is the hash of the object's bytes.

`ArtifactVerifier` needs no change. Document both.

### Retention and cleanup

- `A2ATaskAdmin.PurgeAsync` deletes the task row first, then its objects under `{tenant}/{taskId}/`. Deleting objects first could leave a task pointing at nothing. A failed object delete is logged and left to the sweeper.
- `A2ARetentionService` gains an orphan step: list objects older than `OrphanGracePeriod`, and delete those whose task no longer exists or whose task JSON does not reference the object id. It runs only with `AddA2ARetention`.
- The sweep needs a new `TaskStore` member that reads a task by tenant and id without an owner, in every `TaskStore`. `GetAsync` cannot be used: it would report live tasks as missing and delete their objects. Any error reading a task means skip that object.

### Other

- Startup warning when a durable `TaskStore` is used with `InMemoryArtifactObjectStore`.
- Telemetry: counters for objects written, bytes written, bytes served, orphans deleted; a span per upload.
- v0.3 compat needs no change: `V03JsonCodec` already maps a `UrlPart` to `file.uri`.
- Encryption at rest is the bucket's job; document it.

## Testing

- Part 1: each cap at, under and over the limit; the failure event is stored at the cap; on all bindings.
- A shared contract suite for every `ArtifactObjectStore`, with MinIO and Azurite through Testcontainers, added to the durable stores CI job.
- Write path: under, at and over the threshold; hash verifies end to end; upload failure reaches the agent; `AddArtifactFromStreamAsync` over 4 MB; `MaxObjectBytes`.
- Serving: another owner gets 404; another tenant gets 404; a resolver tenant that differs from the link gets 404; a purged task gets 404; object id from another task gets 404; anonymous rejected; a handler that denies `GetTask` denies the download; startup check covers the route.
- Client: foreign origin throws with no request sent; redirect followed once without credentials; size and hash mismatch throw.
- Follow-up message over `MaxTaskBytes` is refused and the task is unchanged.
- Orphan sweep never deletes an object of a live task, whoever owns it.
- Crash window: object written, no commit, swept after grace period and not before.
- A task with no store registered behaves as before.
- Conformance and interop stay green.

## Rollout

1. Part 1, caps. Small, independent.
2. Object store core, in-memory provider, write path, serving, client, cleanup.
3. S3 and Azure providers.

Parts 2 and 3 touch `TaskUpdater`, retention and `TaskStore`, and add one route that calls the pipeline without changing it, so they can run alongside per-skill authorization and typed skills.

## Settled while writing (the author's calls, open to change)

- 256 KiB inline threshold; 64 MiB `MaxTaskBytes`; 1024 `MaxArtifactsPerTask`; 5 GiB `MaxObjectBytes`.
- `Redirect` is the default delivery, because proxying large files loads the app server.
- Appended chunks stay inline.
- Follow-ups over `MaxTaskBytes` are refused with `UnsupportedOperation`.
- Downloads are authorized as `GetTask`.
- No separate history cap.
- `A2AArtifactDownloader` is net8+ only and takes the caller's `HttpMessageHandler`, not an `HttpClient`: a client cannot be checked for redirect following, a handler can.
- Objects share their task's retention.

## Left to the builder

- How `MaxTaskBytes` is measured cheaply, without serializing the task one extra time per event.
- Internal class layout, log event ids, metric names.
- Batch sizes and paging in the orphan sweep.
- S3 and Azure client configuration surface, following the Postgres and Redis packages.
