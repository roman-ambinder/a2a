# Artifact Object Store and Task Size Caps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cap how large a task can grow, and keep large artifact bytes in an object store behind a stable, owner-checked link.

**Architecture:** Caps are enforced in the engine for events and the handler for follow-ups. Large raw parts are uploaded in `TaskUpdater`, before hashing, and become ordinary `url` parts holding a stable link, so the event store, outbox and push code do not change. A new route authorizes as `GetTask` and redirects to, or proxies, the object.

**Tech Stack:** .NET 8/10, ASP.NET Core minimal routes, xUnit, Testcontainers, AWSSDK.S3, Azure.Storage.Blobs.

**Spec:** `docs/superpowers/specs/2026-11-08-artifact-object-store-design.md`

## Global Constraints

- Three PRs, in order: Task 1 alone; Tasks 2–7; Tasks 8–9. Each branches from the latest `origin/main`, with the spec commit included in the first.
- `dotnet build A2A.slnx` must give 0 errors and 0 warnings on **all** target frameworks (net8.0, net10.0, netstandard2.0) before each commit.
- Every new public member goes in that project's `PublicAPI.Unshipped.txt`. New store base classes are abstract classes, as in `Storage/`.
- Defaults, verbatim: `MaxTaskBytes` 64 MiB; `MaxArtifactsPerTask` 1024; `InlineThresholdBytes` 256 KiB; `MaxObjectBytes` 5 GiB; `DownloadUrlLifetime` 5 minutes; `OrphanGracePeriod` 1 hour; `Delivery` `Redirect`.
- Part metadata keys, verbatim: `sha256`, `sizeBytes`.
- No object store registered means no behavior change other than the caps.
- Downloads need the caller's A2A credentials. No signed or capability links.
- End commit messages with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Test command unless a step says otherwise: `dotnet test A2A.slnx -f net10.0`. Store tests: `A2A_REQUIRE_DOCKER=1 dotnet test tests/A2A.Stores.Tests -f net10.0`.

## Review Focus

1. A tenant or task id containing `/`, `..` or `%`: must not change the object path or reach another task's objects. (Task 2)
2. An agent whose updater is already terminal calls `AddArtifactAsync` with a large part: must throw before uploading anything. (Task 3)
3. A stream longer than `MaxObjectBytes`, or one that throws midway: no partial object may remain. (Task 2, contract)
4. A client that disconnects during a proxied download: the object stream must be disposed. (Task 4)
5. The store or task read fails during the orphan sweep: nothing is deleted. (Task 6)

---

### Task 1: Task size caps

**Files:**
- Modify: `src/A2A.Server/A2ALimits.cs`, `src/A2A.Server/Execution/TaskExecutionEngine.cs`, `src/A2A.Server/A2ARequestHandler.cs`, `src/A2A.Server/PublicAPI.Unshipped.txt`, `CHANGELOG.md`, `docs/site/guides/limits-and-rate-limiting.md`
- Create: `src/A2A.Server/TaskSize.cs`
- Test: `tests/A2A.Tests/TaskSizeCapTests.cs`

**Interfaces:**
- Produces: `A2ALimits.MaxTaskBytes` (`long`), `A2ALimits.MaxArtifactsPerTask` (`int`); `internal static class TaskSize { static long Measure(AgentTask task); }`.

- [ ] **Step 1: Write failing tests** in `TaskSizeCapTests`, using the `FixedFactory`/`TestAgent` setup from `TaskContinuationTests`, with `o.Limits.MaxTaskBytes` set small (e.g. 4096):
  - `Artifact_within_cap_is_stored`: state ends `Completed`.
  - `Artifact_past_cap_fails_task`: task ends `Failed`, oversized artifact absent, agent's token cancelled.
  - `Too_many_artifacts_fails_task`: `MaxArtifactsPerTask = 2`, three artifacts, ends `Failed`.
  - `Failure_event_is_stored_at_the_cap`: task at exactly the cap still reaches `Failed` and a final event is in the log.
  - `Follow_up_past_cap_is_refused_and_task_unchanged`: `A2AException` with `UnsupportedOperation`; `Version` and history unchanged.
  - `Measure_counts_text_raw_url_and_data`: raw of 3 bytes counts as 4 (base64); text counts UTF-8 bytes.
  - `Caps_apply_on_every_binding`: `[MemberData(nameof(Bindings))]`, as in `PushConfigTests`.

- [ ] **Step 2: Run them, confirm they fail** (`--filter FullyQualifiedName~TaskSizeCapTests`; expected: compile errors on `MaxTaskBytes`).

- [ ] **Step 3: Add the limits** to `A2ALimits`: `public long MaxTaskBytes { get; set; } = 64L * 1024 * 1024;` and `public int MaxArtifactsPerTask { get; set; } = 1024;`, with XML docs saying what happens at the cap.

- [ ] **Step 4: Implement `TaskSize.Measure`.** Walk history messages and artifact parts, summing: UTF-8 byte count for text, `(len + 2) / 3 * 4` for raw, URL string length for url, `GetRawText().Length` for data. Do not serialize the task.

- [ ] **Step 5: Enforce in the engine.** In `ApplyAndStoreAsync`, add parameter `Action? onOverCap = null`. After `ApplyToCopy` gives `next`, if `next`'s state is not terminal and `TaskSize.Measure(next) > MaxTaskBytes` or `next.Artifacts.Count > MaxArtifactsPerTask`: log `ServerLog.TaskTooLarge`, replace `encoded` with `Encode(StreamResponse.Of(FailureEvent(taskId, contextId, OversizedTaskText)))`, call `onOverCap`, and re-apply. Skipping the check for terminal states is what keeps the failure event storable. The main loop passes `() => CancelAll([exec])`.

- [ ] **Step 6: Enforce on follow-ups.** In `A2ARequestHandler`, next to the `_tasks.UpdateAsync` at the follow-up path, build `next` as today, and throw `new A2AException(A2AErrorCode.UnsupportedOperation, ...)` if `TaskSize.Measure(next) > MaxTaskBytes`. Follow `EnsureTurnCapacity`'s shape.

- [ ] **Step 7: Run all tests** (expected: all pass).

- [ ] **Step 8: Update the guide and CHANGELOG, then commit** (`feat(server): cap total task size and artifact count`). Open PR 1.

---

### Task 2: Object store abstraction and in-memory provider

**Files:**
- Create: `src/A2A.Server/Storage/ArtifactObjectStore.cs`, `src/A2A.Server/Storage/ArtifactObjectKey.cs`, `src/A2A.Server/Storage/InMemoryArtifactObjectStore.cs`, `src/A2A.Server/ArtifactStorageOptions.cs`
- Modify: `src/A2A.Server/A2AServerOptions.cs`, `src/A2A.Server/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Stores.Tests/ArtifactObjectStoreContract.cs`, `tests/A2A.Tests/ArtifactObjectKeyTests.cs`

**Interfaces:**
- Produces:

```csharp
public abstract class ArtifactObjectStore
{
    public abstract ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken ct = default);
    public abstract ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken ct = default);
    public abstract ValueTask DeleteAsync(string key, CancellationToken ct = default);
    public abstract IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken ct = default);
    public virtual ValueTask<Uri?> TryCreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default); // default: null
}
public sealed record ArtifactObjectInfo(string? ContentType, string? FileName);
public sealed record ArtifactObjectWriteResult(long SizeBytes, string Sha256);   // Sha256 in ArtifactHash.Format form
public sealed record ArtifactObjectEntry(string Key, DateTimeOffset CreatedAt);
public sealed record ArtifactByteRange(long Offset, long? Length);
public sealed class ArtifactObjectContent : IAsyncDisposable { Stream Content; string? ContentType; string? FileName; long Length; long TotalLength; }
public sealed class ArtifactObjectTooLargeException : Exception;
internal static class ArtifactObjectKey
{
    string Create(string tenant, string taskId, string objectId);
    string TaskPrefix(string tenant, string taskId);   // ends with '/'
    bool TryParse(string key, out string tenant, out string taskId, out string objectId);
    string NewObjectId();                              // 32 lowercase hex chars, random
    bool IsValidObjectId(string objectId);             // ^[0-9a-f]{32}$
}
public sealed class ArtifactStorageOptions { int InlineThresholdBytes; long MaxObjectBytes; Uri? PublicBaseUrl; Func<string, Uri>? PublicBaseUrlResolver; ArtifactDelivery Delivery; TimeSpan DownloadUrlLifetime; TimeSpan OrphanGracePeriod; }
public enum ArtifactDelivery { Redirect, Proxy }
```

- `A2AServerOptions.ArtifactStorage` (never null).

- [ ] **Step 1: Write failing key tests** in `ArtifactObjectKeyTests`: `[Theory]` over tenants and task ids `"a/b"`, `".."`, `"a%2Fb"`, `""` asserts `TryParse(Create(t, id, oid))` returns the same values, and no segment of the key equals `..`. `TaskPrefix("t","a")` is not a prefix of a key for task `"ab"`. `IsValidObjectId` rejects `"../x"`, uppercase, wrong length.

- [ ] **Step 2: Write the contract** `ArtifactObjectStoreContract` (abstract xUnit class with `CreateStore()`, like `TaskStoreContract`), and an in-memory subclass:
  - `Put_then_read_round_trips` with size and SHA-256 equal to computed values.
  - `Put_of_non_seekable_stream_works`.
  - `Put_over_maxBytes_throws_and_leaves_no_object`.
  - `Put_when_stream_throws_leaves_no_object`.
  - `Read_missing_returns_null`.
  - `Delete_is_idempotent`.
  - `List_by_prefix_returns_only_that_task`.
  - `Range_read_returns_requested_bytes` with `Length` and `TotalLength`.

- [ ] **Step 3: Run them, confirm they fail** (compile errors).

- [ ] **Step 4: Implement `ArtifactObjectKey`.** Layout `{tenant}/{taskId}/{objectId}`. Encode each of tenant and task id with `Uri.EscapeDataString`, then replace `.` with `%2E`; empty tenant is `_`, and a literal tenant `_` is rejected with `ArgumentException`.

- [ ] **Step 5: Implement `ArtifactObjectStore` types and `InMemoryArtifactObjectStore`**, with an `internal` helper stream that counts, hashes and enforces `maxBytes`, for providers to share.

- [ ] **Step 6: Implement `ArtifactStorageOptions`** with the Global Constraints defaults.

- [ ] **Step 7: Run tests, confirm pass; commit** (`feat(server): artifact object store abstraction`).

---

### Task 3: Offload in TaskUpdater

**Files:**
- Modify: `src/A2A.Server/AgentExecutor.cs`, `src/A2A.Server/Execution/TaskExecutionEngine.cs`, `src/A2A.Server/PublicAPI.Unshipped.txt`, startup diagnostics (`A2AStartupDiagnostics`), `src/A2A.Server/Telemetry/`
- Create: `src/A2A.Server/ArtifactOffload.cs`, `src/A2A.Server/ArtifactLink.cs`
- Test: `tests/A2A.Tests/ArtifactOffloadTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces:
  - `internal static class ArtifactLink { Uri Create(Uri baseUrl, string tenant, string taskId, string objectId); bool TryGetObjectId(Uri url, out string objectId); }`. Shape: `{base}/artifacts/{tenant or _}/{taskId}/{objectId}`.
  - `internal sealed class ArtifactOffload` (store, options, tenant, task id), passed to the `TaskUpdater` internal constructor as an optional last argument.
  - Public: `ValueTask<string> TaskUpdater.AddArtifactFromStreamAsync(Stream content, string mediaType, string? artifactId = null, string? name = null, string? description = null, Dictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default)`.
  - `TaskExecutionEngine` gets a new optional last constructor parameter `ArtifactObjectStore? objects = null`.

- [ ] **Step 1: Write failing tests** in `ArtifactOffloadTests`, using the `TestAgent` setup and `InMemoryArtifactObjectStore`, with `PublicBaseUrl = new("https://agent.test/a2a")`:
  - `Raw_part_over_threshold_becomes_url_part`: delivered part is `UrlPart`; `sizeBytes` and `sha256` metadata match; media type and file name kept.
  - `Raw_part_at_threshold_stays_inline`, `Text_part_stays_inline`.
  - `Append_is_never_offloaded`.
  - `Hashed_artifact_verifies_end_to_end`: `AddHashedArtifactAsync` with a large part passes `ArtifactVerifier`.
  - `Stream_method_exceeds_MaxEventBytes`: a 5 MB stream completes; one sealed artifact, one `UrlPart`, artifact `sha256` present.
  - `Stream_over_MaxObjectBytes_throws_to_agent`.
  - `Stream_method_without_store_throws_InvalidOperation`.
  - `Upload_failure_reaches_the_agent`.
  - `No_upload_when_updater_is_terminal` (Review Focus 2).
  - `No_store_means_large_part_stays_inline`.
  - `Link_contains_tenant_and_uses_resolver_when_set`, `Default_tenant_segment_is_underscore`.
  - `Store_without_PublicBaseUrl_fails_at_startup`.
  - `Durable_task_store_with_in_memory_objects_warns`.

- [ ] **Step 2: Run them, confirm they fail.**

- [ ] **Step 3: Implement `ArtifactLink` and `ArtifactOffload`.** `ArtifactOffload.PrepareAsync(IReadOnlyList<Part>, ct)` returns the parts with each `RawPart` longer than the threshold uploaded and replaced by `new UrlPart(link) { MediaType, Filename, Metadata }`, plus `sha256` and `sizeBytes`.

- [ ] **Step 4: Wire `TaskUpdater`.** Call `CheckCanEmit` under `_gate` first, then `PrepareAsync` outside any lock, when `!append`, in all `AddArtifactAsync` overloads that take parts and in `AddHashedArtifactAsync`; then proceed to the existing `lock`/`Track` path. `AddArtifactFromStreamAsync` follows the same order and calls the existing hashed path with `lastChunk: true`.

- [ ] **Step 5: Wire the engine** at the `new TaskUpdater(...)` call (near line 323) to build `ArtifactOffload` when `objects` is set. Throw `InvalidOperationException` in the constructor when `objects` is set and both `PublicBaseUrl` and `PublicBaseUrlResolver` are null.

- [ ] **Step 6: Add the startup warning and telemetry** (counters for objects and bytes written; a span per upload), following `ServerTelemetry`.

- [ ] **Step 7: Run all tests, confirm pass; commit** (`feat(server): offload large raw parts to the object store`).

---

### Task 4: Serving route

**Files:**
- Create: `src/A2A.Server.AspNetCore/ArtifactEndpoint.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs`, `src/A2A.Server.AspNetCore/A2AEndpointRouteBuilderExtensions.cs`, both `PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/ArtifactEndpointTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 3.
- Produces:
  - `public ValueTask<ArtifactDownload> A2ARequestHandler.OpenArtifactAsync(string taskId, string objectId, ArtifactByteRange? range, ServerCallContext call, CancellationToken ct = default)`. `IA2ARequestHandler` does not change.
  - `public sealed class ArtifactDownload : IAsyncDisposable { Uri? RedirectTo; ArtifactObjectContent? Content; }`
  - `public static RouteGroupBuilder MapA2AArtifacts(this IEndpointRouteBuilder endpoints, string pattern = "/")`.

- [ ] **Step 1: Write failing tests** in `ArtifactEndpointTests` (use `TestAgentHost`; map the route in `configurePipeline`-style setup; register a store through `services`):
  - `Owner_downloads_bytes`, with `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, `Cache-Control` containing `no-store`.
  - `Another_owner_gets_404`, `Another_tenant_gets_404`, `Resolver_tenant_differing_from_link_gets_404`.
  - `Purged_task_gets_404`, `Object_id_from_another_task_gets_404`, `Malformed_object_id_gets_404`.
  - `Anonymous_is_rejected`.
  - `Handler_denying_GetTask_denies_download`, using a custom `A2AAuthorizationHandler`.
  - `Startup_fails_without_authorization_policy`.
  - `Redirect_mode_returns_302_to_presigned_url`, with a test store whose `TryCreateDownloadUrlAsync` returns a URL and asserts lifetime 5 minutes.
  - `Redirect_mode_proxies_when_store_has_no_presigned_url`.
  - `Proxy_mode_honours_Range`: 206 with `Content-Range`.
  - `Client_abort_disposes_object_stream` (Review Focus 4).

- [ ] **Step 2: Run them, confirm they fail.**

- [ ] **Step 3: Implement `OpenArtifactAsync`.** Run through `_pipeline.RunAsync(A2AOperationDescriptor.For(A2AOperation.GetTask), call, new GetTaskRequest { Id = taskId, Tenant = call.Tenant }, ...)`. Inside: validate `IsValidObjectId`, `_tasks.GetAsync(Key(call, taskId))` or `A2AException.TaskNotFound`, then build the key from `call.Tenant`. For `Redirect`, use `TryCreateDownloadUrlAsync` when no range is asked for; otherwise `OpenReadAsync`. A missing object is `TaskNotFound`.

- [ ] **Step 4: Implement `ArtifactEndpoint` and `MapA2AArtifacts`.** Route `GET /artifacts/{artifactTenant}/{taskId}/{objectId}`. Call `RequireRegistration`, `ApplyRateLimiting`, `WithDisplayName("A2A Artifact")`. Use `HttpPlumbing.CreateCallContext`; decode `_` to `""`; if `call.Tenant` is non-empty and differs from the link's tenant answer 404, else `call.BindTenant(linkTenant)`. Map `TaskNotFound` to 404. Parse a single `bytes=a-b` range; otherwise ignore `Range`. Always dispose the `ArtifactDownload`. Add a bytes-served counter.

- [ ] **Step 5: Run all tests, confirm pass; commit** (`feat(aspnetcore): owner-checked artifact download route`).

---

### Task 5: Client downloader

**Files:**
- Create: `src/A2A.Client/A2AArtifactDownloader.cs`
- Modify: `src/A2A.Client/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/ArtifactDownloaderTests.cs`

**Interfaces:**
- Produces, inside `#if !NETSTANDARD2_0`:

```csharp
public sealed class A2AArtifactDownloader
{
    public A2AArtifactDownloader(HttpClient agentHttpClient, Uri agentOrigin, SafeHttpOptions? storageOptions = null);
    public Task DownloadAsync(UrlPart part, Stream destination, long maxBytes, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 1: Write failing tests** with a fake `HttpMessageHandler`:
  - `Foreign_origin_throws_and_sends_nothing`.
  - `Credentialed_request_goes_to_agent_origin`.
  - `Redirect_is_followed_once_without_credentials`: second request has no `Authorization` and uses a different client.
  - `Second_redirect_throws`.
  - `Size_mismatch_throws`, `Hash_mismatch_throws`.
  - `Over_maxBytes_throws_and_stops_reading`.
  - `Part_without_metadata_still_downloads`.

- [ ] **Step 2: Run them, confirm they fail.**

- [ ] **Step 3: Implement.** Compare origin with `GetLeftPart(UriPartial.Authority)`, case-insensitive. Throw `InvalidOperationException` when the response shows `agentHttpClient` followed a redirect (final URI differs). On 302/307, request `Location` through `SafeHttp.CreateClient(storageOptions)` after `SafeHttp.EnsureAllowed`. Verify after copying with a hashing, counting stream.

- [ ] **Step 4: Confirm the netstandard2.0 build is clean, run tests, commit** (`feat(client): artifact downloader with origin and integrity checks`).

---

### Task 6: Purge and orphan sweep

**Files:**
- Modify: `src/A2A.Server/Storage/TaskStore.cs`, `InMemoryTaskStore.cs`, `src/A2A.Server.Postgres/PostgresTaskStore.cs`, `src/A2A.Server/Retention/A2ATaskAdmin.cs`, `A2ARetentionService.cs`, test `TaskStore` subclasses (`RetentionTests`, `OwnershipCheckingTaskStore`, `ErrorTests`, `TaskContinuationTests`, `TaskStoreTests`), `PublicAPI.Unshipped.txt` files
- Test: `tests/A2A.Stores.Tests/TaskStoreContract.cs`, `tests/A2A.Tests/ArtifactCleanupTests.cs`

**Interfaces:**
- Produces: `public abstract ValueTask<TaskRecord?> TaskStore.GetForMaintenanceAsync(string tenant, string taskId, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing tests.**
  - Contract: `GetForMaintenance_finds_a_task_whoever_owns_it`, `GetForMaintenance_returns_null_when_missing`.
  - `ArtifactCleanupTests`:
    - `Purge_deletes_task_then_objects`, asserting order with a recording store.
    - `Purge_succeeds_when_object_delete_fails`.
    - `Sweep_deletes_object_of_missing_task_after_grace`.
    - `Sweep_keeps_young_orphan`.
    - `Sweep_deletes_object_not_referenced_by_its_task`.
    - `Sweep_keeps_referenced_object_of_any_owner`.
    - `Sweep_deletes_nothing_when_task_read_fails` (Review Focus 5).
    - `Sweep_skips_keys_it_cannot_parse`.
    - `No_sweep_without_a_store`.

- [ ] **Step 2: Run them, confirm they fail.**

- [ ] **Step 3: Add `GetForMaintenanceAsync`** to `TaskStore`, in-memory and Postgres (`WHERE tenant = $1 AND task_id = $2`), and to test subclasses.

- [ ] **Step 4: Update `PurgeAsync`** to delete objects under `ArtifactObjectKey.TaskPrefix` after `_tasks.DeleteAsync`, catching and logging failures. `A2ATaskAdmin` gets an optional `ArtifactObjectStore? objects = null` constructor argument.

- [ ] **Step 5: Add a sweep step** to `A2ARetentionService`, run through `StepAsync` when a store is registered. Use `ListAsync("")`, `CreatedAt` older than `OrphanGracePeriod`, `TryParse`, `GetForMaintenanceAsync`, and `ArtifactLink.TryGetObjectId` over the task's URL parts. Process at most `BatchSize` deletions per sweep. Add an orphans-deleted counter.

- [ ] **Step 6: Run all tests including store tests, confirm pass; commit** (`feat(server): delete artifact objects with their task and sweep orphans`).

---

### Task 7: Docs and PR 2

**Files:**
- Create: `docs/site/guides/artifact-storage.md`
- Modify: `docs/site/guides/toc.yml`, `docs/site/snippets/` (compiled snippet), `docs/site/guides/retention.md`, `docs/site/guides/spec-decisions.md`, `README.md`, `src/A2A.Server/README.md`, `CHANGELOG.md`, `docs/2026-11-07-a2a-gap-analysis.md` (S40 status)

- [ ] **Step 1: Write the guide:** when to use a store, options table, the two kinds of `sha256`, that other SDKs' clients must send credentials to download, that encryption at rest is the bucket's job, that `AddArtifactFromStreamAsync` is the way to produce large files, and that appended chunks stay inline.

- [ ] **Step 2: Build docs** with the same commands as the CI docs job in `.github/workflows/ci.yml`. Expected: no warnings or broken links.

- [ ] **Step 3: Run `bash interop/run.sh` and `bash conformance/run.sh`.** Expected: `INTEROP: all runs passed` and `No unexpected failures`.

- [ ] **Step 4: Commit** (`docs: artifact storage guide`) and open PR 2.

---

### Task 8: S3 provider

**Files:**
- Create: `src/A2A.Server.Storage.S3/` (`A2A.Server.Storage.S3.csproj`, `S3ArtifactObjectStore.cs`, `S3ArtifactStoreOptions.cs`, `ServiceCollectionExtensions.cs`, `README.md`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`)
- Modify: `A2A.slnx`, `Directory.Packages.props`, `tests/A2A.Stores.Tests/A2A.Stores.Tests.csproj`, `tests/A2A.Stores.Tests/Fixtures.cs`, `.github/workflows/release.yml` if it lists packages
- Test: `tests/A2A.Stores.Tests/S3ArtifactObjectStoreTests.cs`

**Interfaces:**
- Consumes: `ArtifactObjectStoreContract`.
- Produces: `IServiceCollection AddA2AS3ArtifactStore(this IServiceCollection services, Action<S3ArtifactStoreOptions> configure)`; options `BucketName`, `KeyPrefix`, `ServiceUrl`, `ForcePathStyle`, and an optional caller-owned `IAmazonS3`.

- [ ] **Step 1: Add project and packages,** copying the shape of `src/A2A.Server.Redis/A2A.Server.Redis.csproj`. Pin latest stable `AWSSDK.S3` and `Testcontainers.Minio` 4.16.0. If the SDK raises AOT or trim warnings, set `IsAotCompatible` to false for this project only and say so in its README.

- [ ] **Step 2: Add `S3ArtifactObjectStoreTests : ArtifactObjectStoreContract`** on MinIO via Testcontainers, plus `Presigned_url_downloads_the_object`.

- [ ] **Step 3: Run it, confirm it fails** (`A2A_REQUIRE_DOCKER=1 dotnet test tests/A2A.Stores.Tests -f net10.0 --filter FullyQualifiedName~S3`).

- [ ] **Step 4: Implement `S3ArtifactObjectStore`.** Upload with multipart, buffering one 8 MiB part at a time, so a non-seekable stream needs no full buffer. Abort the multipart upload on any failure or over-limit, which leaves no object. Store content type and file name as object metadata. Use `GetPreSignedURL` for downloads.

- [ ] **Step 5: Run the contract, confirm pass; commit** (`feat: S3 artifact object store`).

---

### Task 9: Azure Blob provider and PR 3

**Files:**
- Create: `src/A2A.Server.Storage.AzureBlob/` (same set as Task 8)
- Modify: `A2A.slnx`, `Directory.Packages.props`, `tests/A2A.Stores.Tests/A2A.Stores.Tests.csproj`, `Fixtures.cs`, `docs/site/guides/artifact-storage.md`, `CHANGELOG.md`
- Test: `tests/A2A.Stores.Tests/AzureBlobArtifactObjectStoreTests.cs`

**Interfaces:**
- Produces: `IServiceCollection AddA2AAzureBlobArtifactStore(this IServiceCollection services, Action<AzureBlobArtifactStoreOptions> configure)`; options `ContainerName`, `ConnectionString`, `KeyPrefix`, and an optional caller-owned `BlobContainerClient`.

- [ ] **Step 1: Add project and packages:** latest stable `Azure.Storage.Blobs`, `Testcontainers.Azurite` 4.16.0, same AOT rule as Task 8.

- [ ] **Step 2: Add the contract subclass** on Azurite, plus `Presigned_url_downloads_the_object`.

- [ ] **Step 3: Run it, confirm it fails.**

- [ ] **Step 4: Implement** with block blobs (`StageBlockAsync` 8 MiB at a time, `CommitBlockListAsync` at the end), so nothing is visible until the commit. Use a SAS URI for `TryCreateDownloadUrlAsync`; return null when the client cannot sign.

- [ ] **Step 5: Run the store tests, then confirm the CI `stores` job still covers them.**

- [ ] **Step 6: Add both providers to the guide, commit** (`feat: Azure Blob artifact object store`), open PR 3.
