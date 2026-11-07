# P0 design: security and correctness baseline for the A2A .NET SDK

Date: 2026-11-07. Status: draft for review.
Source of gaps: `docs/2026-11-07-a2a-gap-analysis.md` (IDs like S12, D2 refer to that doc).
Wire contract: A2A v1.0, `docs/a2a.proto`, JSON per ProtoJSON (camelCase names).

## 1. Intent

Make the first usable release of this SDK safe to deploy without the host app re-inventing security and correctness plumbing. The official `a2a-dotnet` SDK treats task ownership, auth, idempotency, stream resume and card verification as "application responsibility". Real deployments then ship IDOR bugs, duplicate tasks and lost stream events. This SDK makes the safe behaviour the default and keeps every addition wire-compatible with other v1.0 peers.

## 2. Goals

1. A host app that wires the SDK with defaults cannot leak one caller's task, context or push config to another caller.
2. A client that retries `SendMessage` after a network failure gets the same task, not a duplicate.
3. A client that loses an SSE connection can resume without missing or duplicating events.
4. A client can verify a signed Agent Card against keys it trusts, and a forged card using an attacker-chosen `jku` fails.
5. Push webhooks cannot be aimed at internal addresses by default, and stored secrets are never returned on read.
6. Every request has bounded size and rate by default.
7. The same request produces the same result over JSON-RPC and HTTP+JSON, proven by tests.

## 3. Non-goals for P0

- Durable stores (SQL, Redis). P0 defines the contracts and ships in-memory implementations that obey them. P1 adds durable providers.
- Multi-replica event fan-out (`IEventBus` providers). P0 contracts must allow it; P0 ships single-process only.
- gRPC binding. Contracts must not block it.
- Durable push outbox, HMAC webhook signatures, retry policy. P1.
- v0.3 compatibility of any kind.
- Agent registry, identity standard (DID/SPIFFE), delegation/actorChain.

## 4. Anti-goals (failure even if it works)

- Any extra wire field or header that a plain v1.0 peer would reject. Additions must be ignorable.
- Security default that is off and must be turned on. Opt-out is allowed; opt-in is not.
- Returning 403 or a distinct error for a task that exists but belongs to someone else. That reveals existence.
- Public interfaces that force every future member addition to be a breaking change (lesson from a2a-dotnet #133).
- Divergent behaviour between bindings.

## 5. Constraints

- Targets: net8.0 and net10.0 (existing `Directory.Build.props`). `TreatWarningsAsErrors` on.
- Serialization: System.Text.Json with source generation. Must stay AOT/trim-safe. No reflection-based serializer paths.
- Dependencies: only `Microsoft.Extensions.*` abstractions and ASP.NET Core shared framework in the libraries. No third-party runtime dependency in `A2A.Protocol`, `A2A.Client`, `A2A.Server`. Exception: none needed for P0; JWS uses `System.Security.Cryptography`.
- Existing code: about 5,100 lines across `A2A.Protocol`, `A2A.Client`, `A2A.Server`, `A2A.Server.AspNetCore` and `tests/A2A.Tests`. It builds clean on .NET SDK 10.0.401 and its 46 tests pass (checked 2026-11-07 17:22). **Decision (user): refactor this code toward this design, do not rewrite.** Section 10 maps each existing type to its target.
- Tests: xUnit, `Microsoft.AspNetCore.TestHost`.

## 6. Cross-cutting decisions

### 6.1 Extension-point style (my call)
Extension points are **abstract classes** with virtual members where the SDK may add members later (`TaskStore`, `EventStore`, `IdempotencyStore`, `A2AAuthorizationHandler`). Small single-method contracts that will not grow are interfaces (`ITenantResolver`, `IPrincipalResolver`, `IClock`). Options are sealed classes bound via `IOptions<T>`.

### 6.2 Caller identity
Every server operation runs with a `CallerContext`:

```csharp
public sealed record CallerContext(string Tenant, string Principal, ClaimsPrincipal? User);
```

- `Tenant`: from `ITenantResolver`. Default resolver: tenant from route value `{tenant}` if the endpoint was mapped with a tenant template, else from request body `tenant` field if present, else `""` (default tenant). If route and body both carry a tenant and they differ → `InvalidParams`.
- `Principal`: from `IPrincipalResolver`. Default: `ClaimTypes.NameIdentifier`, then `sub`, of the authenticated user. If the endpoint allows anonymous and the user is not authenticated → `Principal = "anonymous"` and a startup warning is logged once ("anonymous callers share one owner; any anonymous caller can read any anonymous task").
- `CallerContext` is created once per request in the binding layer and passed to the transport-agnostic core. The core never reads `HttpContext`.

### 6.3 Error mapping
One error table in `A2A.Protocol` maps `A2AErrorCode` to JSON-RPC code, HTTP status, and `google.rpc.ErrorInfo.reason` with domain `a2a-protocol.org`. Both bindings use it. New P0 situations map as:

| Situation | Error | JSON-RPC | HTTP |
|---|---|---|---|
| Task missing or owned by another caller | TaskNotFound | -32001 | 404 |
| Context owned by another caller | TaskNotFound-equivalent: `InvalidParams` with generic text "contextId not found" | -32602 | 400 |
| Version conflict on write | internal; retried by core, surfaces as `InternalError` after retries | -32603 | 500 |
| Idempotency key reused with different payload | InvalidRequest, reason `IDEMPOTENCY_KEY_MISMATCH` | -32600 | 422 |
| Request over size limit | InvalidRequest, reason `PAYLOAD_TOO_LARGE` | -32600 | 413 |
| Rate limited | reason `RATE_LIMITED`, `retryAfter` metadata | -32000 (server error range) | 429 + `Retry-After` |
| Not authenticated | — | HTTP 401 before JSON-RPC parsing | 401 |
| Authenticated, operation denied by policy | TaskNotFound for task-scoped ops; `UnsupportedOperation`-style generic denial for non-task ops | as row | 404 / 403 |

Every error carries `ErrorInfo.metadata.retryable` (`"true"`/`"false"`). Unexpected exceptions return generic "Internal error" text; exception message and stack only when `A2AServerOptions.IncludeExceptionDetails = true` (default true only when `IHostEnvironment.IsDevelopment()`). (S30, D15)

## 7. Component designs

### 7.1 Owner-scoped, versioned task store (S12, S13, S14, S16, D2, D4, D13)

**Record model** (server-internal, not wire):

```csharp
public sealed record TaskRecord(
    string Tenant, string Owner, A2ATask Task,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ContextRecord(string Tenant, string Owner, string ContextId, DateTimeOffset CreatedAt);
```

**Contract:**

```csharp
public abstract class TaskStore
{
    public abstract ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct);
    public abstract ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct);          // throws DuplicateTaskException
    public abstract ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct); // throws ConcurrencyException
    public abstract ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct);               // query always carries Tenant+Owner
    public abstract ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct);
    public abstract ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct); // atomic
}
public readonly record struct TaskKey(string Tenant, string Owner, string TaskId);
```

Rules:
- `TaskKey` includes `Owner`. A lookup with the wrong owner returns `null`, identical to a missing task. There is no store method that fetches by task id alone. This makes IDOR impossible to write by accident in the core.
- Task ids are server-generated, 128-bit random, base64url (not GUID v4 format requirement, but unguessable). Client-supplied `taskId` on a new message must refer to an existing owned task.
- `UpdateAsync` succeeds only if stored `Version == expectedVersion`; new version = old + 1.
- `ListAsync` pages with an opaque cursor; page size capped by `A2AServerOptions.Limits.MaxListPageSize` (default 100). `totalSize` is the count for that owner+tenant+filter.
- Contexts: on `SendMessage` without `contextId`, server creates one owned by caller. With a `contextId`: if no record exists, behaviour by `A2AServerOptions.ClientContextIdPolicy`: `Reject` (default, error "contextId not found") or `AdoptIfUnknown` (create owned by caller). If record exists and owner differs → same "not found" error. No sharing policy in P0. (S13)
- In-memory implementation: `InMemoryTaskStore` using a `ConcurrentDictionary<TaskKey, TaskRecord>` plus compare-and-swap on version. Deep-copies on read and write so callers cannot mutate stored state.

**State machine guard** (D4): a single `TaskStateMachine.Validate(from, to)` used by the core before every update. Terminal states (`COMPLETED`, `FAILED`, `CANCELED`, `REJECTED`) accept no transition. First terminal write wins: a losing writer gets `ConcurrencyException`, reloads, sees terminal, and drops its update with a debug log. Follow-up messages to a terminal task → `UnsupportedOperation` ("task is terminal; start a new task with referenceTaskIds"). (S26)

**Follow-up policy** (S23): `A2AServerOptions.FollowUpPolicy` = `InterruptedOnly` (default, strict 1.0: accepts messages to tasks in `INPUT_REQUIRED` or `AUTH_REQUIRED`, since v1.0 allows continuing an auth-required task in-band) or `AllowSteering` (also accepts `SUBMITTED` and `WORKING`). Message to a non-terminal task in a state the policy rejects → `UnsupportedOperation`.

**Concurrency inside one process**: the core holds a per-task async lock (keyed by `TaskKey`) around read-modify-write of a single execution step. Version checks remain the real guard so the same code works across replicas later.

### 7.2 Authorization on every binding (S12, D12)

- Bindings call `RequireAuthorization` semantics through standard ASP.NET Core endpoint metadata. `MapA2A(...)` returns an `IEndpointConventionBuilder` that applies conventions to **all** endpoints it creates (JSON-RPC, HTTP+JSON, SSE, extended card). Lesson from Agent Framework #3645 where auth applied to one group only.
- Startup check: if no authorization policy is configured on the A2A endpoints and `A2AServerOptions.AllowAnonymous` is not explicitly `true`, the app fails at startup with a clear message. Anonymous is allowed, but only by explicit choice. The public Agent Card endpoint is always anonymous.
- Per-operation policy hook:

```csharp
public abstract class A2AAuthorizationHandler
{
    public virtual ValueTask<bool> AuthorizeAsync(CallerContext caller, A2AOperation op, object request, CancellationToken ct)
        => ValueTask.FromResult(true);
}
```

  `A2AOperation` is an enum of the 11 v1.0 methods. Denied on task-scoped op → TaskNotFound. Ownership checks (7.1) run regardless of this hook; the hook can only further restrict.
- Extended Agent Card: requires authenticated caller always; content from `AgentCardProvider.GetExtendedCardAsync(caller)`.
- SSE re-check (S15): a stream checks `A2AAuthorizationHandler.AuthorizeAsync` again every `Limits.StreamReauthInterval` (default 5 min) and closes on denial. Streams also close at `Limits.MaxStreamDuration` (default 1 h); clients resume via 7.4.
- Samples: `EchoAgent` gets API-key auth and JWT bearer variants. `EchoClient` shows per-request credentials.

### 7.3 Idempotent SendMessage (S21, D10)

Server:
- Key = `Idempotency-Key` header if present, else `message.messageId`. Both scoped by `(tenant, principal)`.
- Store: `IdempotencyStore` abstract class: `TryBeginAsync(scope, key, requestHash)` → `New` | `InProgress` | `Completed(resultRef)` | `Mismatch`. In-memory implementation with TTL `Idempotency.Retention` (default 24 h) and a size cap (default 100k entries, LRU eviction with a warning metric).
- `requestHash` = SHA-256 over canonical JSON of the request message and configuration. Same key, different hash → `IDEMPOTENCY_KEY_MISMATCH`.
- `New`: proceed; on first persisted task or message response, record `resultRef` (task id or the response message).
- `Completed` with task: return current task state (blocking send) or same task (non-blocking); streaming send: replay as `SubscribeToTask` from the start of the retained event log (7.4).
- `InProgress`: blocking send waits up to the request timeout for completion then behaves as `Completed`; streaming send attaches to the live stream.
- Direct message responses (no task) are cached for the retention window and replayed.

Client:
- `SendMessageAsync` always sets `messageId` (generated if caller left it empty) and sends `Idempotency-Key` = `messageId` unless the caller set one in `A2ARequestOptions`.
- Automatic retry reuses the same message and key.

Peers that ignore `Idempotency-Key` are unaffected; this is a header the spec does not forbid.

### 7.4 Resumable streams (S22, D5, D30)

- Every event persisted for a task gets a per-task monotonic `sequence` (long, starting at 1). `EventStore` abstract class: `AppendAsync(TaskKey, StreamResponse) → sequence`, `ReadAsync(TaskKey, afterSequence) → IAsyncEnumerable<(long, StreamResponse)>`, `TrimAsync`. In-memory implementation keeps the last `Streaming.ReplayBufferSize` events per task (default 1000) and drops buffers of terminal tasks after `Streaming.TerminalRetention` (default 10 min).
- SSE frames carry `id: <taskId>:<sequence>`. Messages without a task (direct `Message` response) carry no id.
- `SubscribeToTask` and reconnecting `SendStreamingMessage` honour `Last-Event-ID`:
  - cursor present and within buffer → replay events after it, then live.
  - cursor missing, unknown, or trimmed → send current `Task` snapshot first (as v1.0 requires for SubscribeToTask), then live. Never silently skip.
  - task terminal and cursor at or past last event → send `Task` snapshot and close. Spec says resubscribe to terminal task errors; we return snapshot over SSE only when `Last-Event-ID` is present (a resume), and the spec error otherwise. (my call: keeps fresh subscribe spec-exact)
- Keep-alive: SSE comment line `: ka` every `Streaming.KeepAliveInterval` (default 15 s).
- Max single event size `Limits.MaxEventBytes` (default 4 MiB); larger artifact chunks must be split by the handler; violation → handler error.
- Client: `ObserveTaskAsync(taskId, options)` returns `IAsyncEnumerable<StreamResponse>`. It subscribes, tracks last id, reconnects with backoff on transport failure using `Last-Event-ID`, and on terminal-task errors falls back to `GetTask` and yields the final snapshot. `SendStreamingMessageAsync` uses the same reconnect logic once it has a task id.

### 7.5 Agent Card signing and verification (S1, S2, S3, S6, D14)

Canonicalization:
- RFC 8785 (JCS) implemented in `A2A.Protocol` over `JsonNode`. Before canonicalizing: remove `signatures`; prune members whose value is `null`, `""`, `[]`, or `{}` recursively (an array element that becomes `{}` is removed). This matches a2a-python 1.2.1 and a2a-js 1.3.0 per A2A#2122. If the TSC rules differently, the pruning rule is a single isolated function.
- Signer refuses to sign a card that contains values the pruning would remove, so signed cards never depend on the pruning disagreement.

Signing (`AgentCardSigner`): JWS detached-payload per RFC 7515 with protected header `{alg, kid, typ: "JOSE", jku?}`. Algorithms: ES256, ES384, RS256, PS256, EdDSA (Ed25519 only on net10.0 where available; else not offered). Keys via `System.Security.Cryptography` (`ECDsa`, `RSA`).

Verification (`AgentCardVerifier`):
- Input: card JSON as received (bytes), plus `CardTrustPolicy`:
  - `TrustedKeys`: pinned JWK sets per issuer (issuer = card origin, scheme+host+port).
  - `AllowedJkuHosts`: hosts from which `jku` may be fetched. Default empty → `jku` ignored.
  - `RequireSignature` (default true in `A2AClientOptions` when any trusted keys configured; false otherwise, with a warning metric for unsigned cards).
  - `RequiredTyp` (default `"JOSE"`; accept `"JOSE+JSON"` too).
- Rules: key must come from `TrustedKeys` for that origin or from an allowlisted `jku` host fetched over HTTPS with SSRF guard (7.6). At least one signature must verify. Result type: `CardVerificationResult { Status, Kid, Issuer, Reason }` with status:
  - `Verified`: a signature verified with a trusted key.
  - `Unsigned`: no signatures.
  - `Unverified`: signatures present, but no trusted key applies (no pinned key for the origin and `jku` not allowlisted). A forged card with an attacker `jku` lands here, never in `Verified`.
  - `Failed`: a trusted key applies and the signature does not verify, or `typ`/`alg` invalid.
- Resolver behaviour: `Failed` always stops resolution. `Unsigned` and `Unverified` stop resolution only when `RequireSignature = true`; otherwise the card is returned with the status attached so callers can decide. Goal 4 therefore means: a forged card is never reported `Verified`, and it is rejected whenever the client has trust configured. (my call)
- Card pinning: `AgentCardResolver` stores SHA-256 of the canonical card (signatures excluded) per origin in an `ICardPinStore` (in-memory default). A "change" is a different content hash. Key rotation that re-signs identical content is not a change; a new `kid` is logged at information level. On change: raise `CardChanged`, log a warning, increment a metric. `PinPolicy` values: `Notify` (default), `Strict` (fail resolution), `Off`. Default is `Notify` because pinning is detection, not protection; protection comes from `TrustedKeys`, and `Strict` with an in-memory store would break clients on every legitimate card edit. (my call)

Untrusted text (S6): resolved cards expose description, skill descriptions and examples through `UntrustedText` (wraps string, `ToString()` returns sanitized form: control chars stripped, length capped at `MaxCardTextLength` default 2000, `Raw` property for explicit access). Model types used for wire serialization keep plain strings; the resolver returns a `ResolvedAgentCard` view. Peer identity = `(origin, kid)`; `name` is display only.

Test vectors: first plan step for 7.5 is to confirm against a2a-python 1.2.1 source exactly which values it prunes (in particular whether it also drops proto defaults `false`, `0`, enum zero). The pruning function and the signer's refuse rule follow what it does. Vectors are generated once by a developer with a2a-python 1.2.1 and a2a-js 1.3.0, then committed under `tests/A2A.Tests/Vectors/`. Python is not required in CI. If a2a-tck publishes vectors, add them. (my call)

### 7.6 Push webhook safety (S7, S8, S9, D18)

Push notification config CRUD and delivery are in P0 because the v1.0 methods are in scope, but delivery is best-effort in-process (durable outbox is P1).

- **URL validation at create time and again at each delivery:**
  - scheme `https` only (`AllowHttp` opt-in for dev).
  - resolve DNS; reject if any resolved address is loopback, RFC1918, link-local (incl. 169.254.169.254), CGNAT 100.64/10, IPv6 ULA fc00::/7, IPv6 link-local, unspecified, multicast, or IPv4-mapped forms of these.
  - connect to the already-validated IP (custom `SocketsHttpHandler.ConnectCallback`), with SNI/Host set to the original host. Blocks DNS rebinding.
  - no redirects, no cookies, no proxy unless configured.
  - `AllowedHosts` / `AllowedNetworks` operator allowlist overrides the private-range block for internal deployments.
- **Secrets:** `token` and `authentication.credentials` stored via `IPushSecretProtector` (default: ASP.NET Core Data Protection). Get/List return config with `token` and `credentials` replaced by `""` and `metadata`-free. Not round-trippable by design.
- **Delivery headers:** `Authorization: Bearer <token>` (when token set and no `authentication` given) and `X-A2A-Notification-Token: <token>`; when `authentication` is given, use its scheme/credentials in `Authorization` and still send `X-A2A-Notification-Token` if token set. Also `X-A2A-Notification-Sequence: <sequence>` (from 7.4) and `X-A2A-Delivery-Id: <uuid>`.
- **Payload:** `StreamResponse` JSON for the event.
- **Ownership:** configs keyed by `(tenant, owner, taskId, configId)`; same not-found rule as tasks.
- **Retention:** configs are not auto-deleted on terminal state (avoids a2a-dotnet #592 conformance failure).
- **Receiver helper** in `A2A.Client`: `PushNotificationReceiver.ValidateAsync(HttpRequest, expectedToken)` with constant-time compare, dedupe by delivery id (in-memory window), and advice in docs to reconcile with `GetTask`.

### 7.7 Limits and rate limiting (S20, D17)

`A2AServerOptions.Limits` defaults:

| Limit | Default |
|---|---|
| MaxRequestBodyBytes | 10 MiB |
| MaxPartsPerMessage | 100 |
| MaxRawPartBytes (decoded) | 8 MiB |
| MaxMetadataDepth / MaxJsonDepth | 32 |
| MaxHistoryLengthReturned | 100 |
| MaxListPageSize | 100 |
| MaxConcurrentStreamsPerTask | 10 |
| MaxConcurrentStreamsPerPrincipal | 50 |
| MaxEventBytes | 4 MiB |
| MaxStreamDuration | 1 h |
| StreamReauthInterval | 5 min |
| MaxPushConfigsPerTask | 10 |

- Body limit enforced by reading with a counting stream before deserialization (not only Kestrel's limit, so TestHost and other servers behave the same).
- Rate limiting: integrates ASP.NET Core `RateLimiter`. SDK registers a named policy `"a2a"` partitioned by `(tenant, principal)`; default token bucket 60 requests/min, burst 20. Host can replace the policy. Rejections mapped to the error table.
- Duplicate JSON property names rejected (`JsonSerializerOptions.AllowDuplicateProperties = false` on net10; custom check on net8). Lesson from a2a-dotnet #540.

### 7.8 Safe defaults in client and errors (D15, D16)

- Client default `HttpClient` from `IHttpClientFactory` named `"A2A"` with `UseCookies = false`, `AllowAutoRedirect = false`, timeout infinite (per-call timeout from `A2ARequestOptions`, default 100 s for unary, none for streams).
- `A2ARequestOptions` on every client method: headers, credential provider, timeout, extensions, idempotency key. (D28)
- Credentials are never sent after a redirect (redirects disabled; if host enables them, the handler strips `Authorization` on cross-origin redirect).
- Retry: client retries only on transport failure, HTTP 503/429, or `retryable=true`, max 3 attempts, exponential backoff with jitter, honours `Retry-After`. Only `SendMessage` (idempotent by 7.3), `GetTask`, `ListTasks`, `Get/List push config`, `GetExtendedAgentCard` are retried. `CancelTask` retried (idempotent by spec). Create/Delete push config not retried automatically.

### 7.9 Closed `Part` hierarchy and unions (D22)

- Wire model: `Part` JSON has exactly one of `text`, `raw`, `url`, `data` plus optional `metadata`, `filename`, `mediaType`.
- C# model: `abstract record Part` with `sealed` subtypes `TextPart`, `RawPart` (`ReadOnlyMemory<byte>`), `UrlPart` (`Uri`), `DataPart` (`JsonElement`). Custom `JsonConverter<Part>` (source-gen compatible) reads by detecting which member is present; zero or more than one → `InvalidParams` naming the field.
- `SendMessageResponse` and `StreamResponse`: `abstract record` with subtypes per oneof case; same converter rule.
- `SecurityScheme`: same pattern.
- Spec-required fields use C# `required`; a `Validate()` pass before send (client) and after parse (server) checks required strings non-empty and `parts` non-empty. (D29)

### 7.10 Transport parity tests (D21)

- One test suite written against an abstract `A2ATransportFixture`, run twice: JSON-RPC and HTTP+JSON, both over `TestHost`.
- Matrix covers each of the 11 methods × {happy path, metadata round-trip, tenant in route vs body, historyLength, every error row in 6.3, auth denied, foreign owner}.
- Each historical a2a-dotnet failure has a named regression test: stream error after headers (#478), persistence failure mid-stream (#495), JSON-RPC error inside stream (#551), INPUT_REQUIRED continuation over JSON-RPC (#508), REST cancel metadata (#593), REST GET subscribe (#591), duplicate JSON properties (#540), background exception leaving task in WORKING (#350).
- Cross-SDK interop (a2a-python server/client) runs as a separate, optional CI job; not a P0 gate. (my call)

## 8. Request flow (server, SendMessage)

1. Binding: auth middleware → rate limiter → body size check → parse + validate → build `CallerContext`.
2. Core: `A2AAuthorizationHandler.AuthorizeAsync`.
3. Idempotency `TryBeginAsync`; short-circuit on `Completed`/`InProgress`/`Mismatch`.
4. Resolve context (ownership rule) and task (owner-scoped get, follow-up policy, state machine).
5. Invoke `IAgentExecutor.ExecuteAsync(context, taskUpdater, ct)`; each event: state-machine validate → `EventStore.AppendAsync` (sequence) → `TaskStore.UpdateAsync` with version → fan out to live streams and push sender.
6. Record idempotency result; return per blocking / non-blocking / streaming mode. `returnImmediately = true` always honoured: return after step 4 persists the `SUBMITTED` task (S24).

## 9. Public API shape (summary)

```csharp
services.AddA2AServer(o => { o.Limits.MaxRequestBodyBytes = ...; o.FollowUpPolicy = ...; })
        .AddAgentExecutor<MyAgent>()       // IAgentExecutor
        .AddAgentCard(card)                     // or .AddAgentCardProvider<T>()
        .AddAuthorizationHandler<MyPolicy>()    // optional
        .AddInMemoryStores();                    // P1: .AddEntityFrameworkStores<TDb>()

app.MapA2A("/")                                 // JSON-RPC + HTTP+JSON + well-known card
   .RequireAuthorization("a2a-callers");
app.MapA2A("/{tenant}")                         // optional tenant template
   .RequireAuthorization("a2a-callers");

services.AddA2AClient(o => o.CardTrust.TrustedKeys.Add(origin, jwks));
```

## 10. Refactor of the existing code

### 10.1 Ground rules
- **Refactor, not rewrite** (user decision). Keep working code that already meets this design. Change the rest in place.
- **No public API compatibility needed.** Nothing is released (version 0.1.0, not on NuGet). Renames and breaking changes are fine. (my call)
- **Build green and tests passing after every stage.** Existing tests are updated when a contract changes, never deleted to make a stage pass. Exception: a test that asserts behaviour this design forbids gets rewritten to assert the new behaviour.
- **Nothing else writes to `src/` or `tests/` during the refactor.** The existing code was produced by another process during this session, and files were still changing at 17:22. The plan's first step confirms that writer has stopped.
- **Put the folder under git before stage 1** (`git init`, commit the current state as baseline). The folder is not a repository today, and a staged refactor needs a way back. (my call)

### 10.2 Existing type → target

| Existing | Status today | Target in this design | Action |
|---|---|---|---|
| `ServerCallContext` (nullable `CallerId`, `Tenant`) | Built per request in `HttpPlumbing.CreateCallContext` | `CallerContext` semantics (6.2): `Tenant` and `Principal` never null | Keep the class name `ServerCallContext` (it also carries version, extensions, items). Make `Tenant` and `Principal` non-null, add tenant/principal resolvers, anonymous rule from 6.2. Rename `CallerId` → `Principal`. (my call: keep the name, change the contract) |
| `ITaskStore` (`GetAsync(taskId)`, `SaveAsync` upsert, `QueryAsync`) | Lookup by task id alone; ownership checked after fetch by `ITaskAccessPolicy` | `TaskStore` abstract class keyed by `TaskKey`, versioned `CreateAsync`/`UpdateAsync`, context records (7.1) | Replace interface with abstract class. Remove `GetAsync(taskId)` without owner. Add `Version`. Keep `QueryAsync` paging logic and `PageCursor` (keyset cursor is good). |
| `TaskRecord` (mutable class, nullable owner/tenant) | — | Immutable record with `Version`, non-null `Tenant`/`Owner` | Change. |
| `ITaskAccessPolicy` / `OwnerTenantAccessPolicy` | Post-fetch filter | Ownership enforced by the store key; extra restriction via `A2AAuthorizationHandler` (7.2) | Delete both. Ownership moves into `TaskKey`. Add `A2AAuthorizationHandler`. |
| `A2ARequestHandler` (663 lines) | All 11 operations; 64-way striped `SemaphoreSlim` per task; `GetAccessibleAsync`; second unscoped `_tasks.GetAsync` calls at lines ~276 and ~293 | Request flow of section 8 | Keep structure. Route every store call through `TaskKey`; remove the unscoped re-reads. Add idempotency step, context ownership, follow-up policy, state machine, event store append. Keep striped locks as the in-process guard; version check becomes the real guard. |
| `IAgentExecutor` + `AgentRequestContext` + `TaskUpdater` | Working agent model with helpers (`CompleteAsync`, `RequireInputAsync`, `AddArtifactAsync`...) | Agent hook in this design | **Keep as is.** `TaskUpdater` is the event sink this design calls for. Exception to 6.1: `IAgentExecutor` stays an interface; it has one method and will not grow. (my call) |
| `TaskEventBus` | Process-local fan-out, no sequence, no replay | `EventStore` (sequence + replay buffer) feeding live fan-out (7.4) | Keep `TaskEventBus` for live fan-out. Add `EventStore` abstract class and in-memory implementation. Every event is appended first, gets a sequence, then is published with that sequence. |
| `IPushNotificationConfigStore` (keyed `(taskId, configId)`) | Secrets stored and returned in clear | Keyed `(tenant, owner, taskId, configId)`, secrets protected and redacted (7.6) | Change key; add `IPushSecretProtector`; redact on Get/List. |
| `PushUrlValidator`, `HttpPushNotificationSender` | SSRF guard with pinned dial, `AllowedHosts`, `AllowPrivateNetworks`, `AllowInsecureUrls`; retries; sends `X-A2A-Notification-Token`, `X-A2A-Notification-Sequence`, `Authorization` from `authentication` | 7.6 | Mostly keep. Add: `Authorization: Bearer <token>` when no `authentication`; `X-A2A-Delivery-Id`; sequence taken from `EventStore`; re-validate URL at each delivery; `AllowedNetworks` (CIDR). Confirm the private-range list matches 7.6 and add tests per range. |
| `A2AServerOptions` | `RequireAuthenticatedCaller`, `MaxPageSize`, `CancelGracePeriod`, push options, extended card delegate | 7.7 `Limits`, `FollowUpPolicy`, `ClientContextIdPolicy`, `AllowAnonymous`, `IncludeExceptionDetails` | Extend. Fold `MaxPageSize` into `Limits.MaxListPageSize`. Replace `RequireAuthenticatedCaller` with the startup check in 7.2. Keep `TreatMissingVersionAsCurrent`. |
| `A2AHttpOptions` (body limit, keep-alive 15 s) | Keep-alive already implemented | 7.4, 7.7 | Keep keep-alive. Move body limit into `A2AServerOptions.Limits` so the core and every binding share it. |
| `HttpPlumbing` (SSE writer, `ReadBodyAsync(maxBytes)`, `Normalize` error) | SSE frames have no `id:`; error normalization exists | 7.4, 6.3 | Add `id:` frames and `Last-Event-ID` handling. Make `Normalize` follow 6.3 (generic message unless `IncludeExceptionDetails`). |
| `MapA2A` returns `RouteGroupBuilder` | Conventions apply to the group | 7.2: one builder covering all A2A endpoints | Verify `RequireAuthorization` on the returned group reaches JSON-RPC, HTTP+JSON, SSE and extended card endpoints; add the test. Add startup check. Add `{tenant}` template support. |
| `Part` (mutable class, 4 nullable content fields, `PartKind`) | Factories `FromText` etc. exist | Closed hierarchy (7.9) | Replace. Keep factory names as static methods on the base type so call sites change little. Same for `SendMessageResponse`, `StreamResponse`, `SecurityScheme`. |
| `AgentTask`, `Message`, `Artifact` (mutable classes) | — | 7.9 asks for `required` on spec-required fields | Keep class names (`AgentTask`, not `A2ATask`; this design's `A2ATask` means `AgentTask`). Add `required` where the spec says REQUIRED. Mutability stays for P0. (my call) |
| `A2AValidator` | Exists (63 lines) | 7.9 validate pass | Extend to required fields and union cardinality. |
| `JsonCanonicalizer` | RFC 8785 implementation with ECMAScript number formatting tests | 7.5 | Keep; extend tests with RFC 8785 appendix vectors. |
| `AgentCardCanonicalizer` (`Normalized` strips proto defaults but keeps REQUIRED fields; `Verbatim`) | Uses a different pruning rule from 7.5 | 7.5 pruning rule, confirmed against a2a-python 1.2.1 | Rework after the confirmation step in 7.5. Keep `Verbatim` only if a2a-python or a2a-js need it for compatibility; otherwise remove. |
| `AgentCardVerifier`, `IAgentCardKeyResolver`, `StaticKeyResolver`, `JwksKeyResolver`, `CompositeKeyResolver` | `JwksKeyResolver` defaults to `RequireSameHostAsCard = true`, so a `jku` on the card's own host is trusted | `CardTrustPolicy` per origin (7.5) | Keep the resolver chain. Change `StaticKeyResolver` to pin keys **per origin**, not globally by `kid`. Change `JwksKeyResolver`: `jku` keys count as trusted only when the host is in `AllowedJkuHosts`. A same-host `jku` with no allowlist yields `Unverified`, because whoever controls the card also controls that key set. Map `AgentCardVerificationStatus` to the four statuses in 7.5. |
| `A2ACardResolver` (`SignaturePolicy.Ignore` default, caching, HTTPS required) | No pinning, no untrusted-text view | 7.5 `RequireSignature`, `PinPolicy`, `UntrustedText` | Extend. Default: verify when a verifier is configured; reject `Failed`; `RequireSignature` as in 7.5. Add pin store and `UntrustedText` on the `ResolvedAgentCard` view. |
| `IA2AClient`, `A2AClient`, transports, `SseReader` | Interface plus JSON-RPC and HTTP+JSON transports | 7.3 idempotency key, 7.4 `ObserveTaskAsync` with resume, 7.8 safe defaults and `A2ARequestOptions` | Extend. `IA2AClient` stays an interface (callers mock it); implementers are only this SDK. (my call) |
| `tests/A2A.Tests` (`ProtocolTests.cs`, `TestHost.cs`, 46 tests) | — | 7.10 parity suite | Keep and grow. Fix the two existing warnings (`CS0108` in `TestHost`, `xUnit1025` duplicate data) since `TreatWarningsAsErrors` should hold for tests too. |

### 10.3 Stage order
Each stage ends with build green and all tests passing.

0. Confirm no other writer; `git init`; baseline commit.
1. Caller identity (6.2) and error table (6.3).
2. Store contract: `TaskKey`, versioning, context records, state machine, follow-up policy (7.1). Delete `ITaskAccessPolicy`. Push config store re-keyed.
3. Authorization: handler, startup check, all-endpoint conventions, tenant templates (7.2).
4. Limits and rate limiting; duplicate JSON properties (7.7).
5. Closed `Part` hierarchy and unions; validator (7.9).
6. Event store and resumable SSE; client `ObserveTaskAsync` (7.4).
7. Idempotency, server and client (7.3).
8. Push changes: secrets, headers, delivery id, re-validation (7.6).
9. Card trust: confirmation step, canonicalizer, per-origin keys, `jku` rule, pinning, `UntrustedText` (7.5).
10. Client safe defaults and `A2ARequestOptions` (7.8).
11. Parity suite and named regression tests (7.10), then samples.

Stages 4, 5 and 9 depend only on stage 1. They can run in parallel if separate people take them.

## 11. Left to the builder

- Internal class layout, file organization, naming of private types.
- Exact in-memory data structures, as long as they meet the contracts.
- Logging message text and event ids (use source-generated `LoggerMessage`).
- Metric names, provided they live under meter/activity source `"A2A"`.
- Order of implementation inside P0, provided 7.1 and 6.2 land first (everything else depends on them).

## 12. Calls I made (not discussed with the user)

1. Abstract classes for growing extension points; interfaces only for tiny contracts.
2. Anonymous access requires explicit opt-in; startup fails otherwise.
3. Client-supplied unknown `contextId` rejected by default.
4. `FollowUpPolicy` default strict 1.0 (`InterruptedOnly`: INPUT_REQUIRED and AUTH_REQUIRED accept follow-ups).
5. Idempotency key falls back to `messageId`; 24 h retention.
6. Card canonicalization uses "prune empty" reading; signer refuses cards with empty values.
7. `jku` ignored unless host allowlisted.
8. Resume on terminal task with `Last-Event-ID` returns snapshot instead of spec error; fresh subscribe stays spec-exact.
9. Push configs not auto-deleted on terminal state.
10. Default limits and rate (table 7.7).
11. Cross-SDK interop CI not a P0 gate.
12. gRPC and v0.3 out of P0.
13. Unverifiable signatures without configured trust return `Unverified`, not fatal unless `RequireSignature`.
14. Card pin default `Notify`; key rotation with same content is not a change.
15. Test vectors generated once and committed; no Python in CI.
16. Public API of the existing code need not be kept (unreleased).
17. `git init` and a baseline commit before refactoring.
18. Keep names `ServerCallContext`, `AgentTask`, `IAgentExecutor`, `TaskUpdater`, `IA2AClient`; change their contracts as section 10 says.
19. A `jku` on the card's own host is not trusted without an allowlist (existing code trusts it).
