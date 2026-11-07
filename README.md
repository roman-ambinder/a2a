# A2A .NET SDK

A .NET SDK for the [Agent2Agent (A2A) protocol](https://a2a-protocol.org/latest/specification/) **v1.0**.
Client and server. JSON-RPC, HTTP+JSON, and gRPC bindings. SSE streaming. Push notifications. Agent Card signing.

Targets **.NET 8** and **.NET 10**.

```bash
dotnet add package RomanAmbinder.A2A.Server.AspNetCore   # host an agent
dotnet add package RomanAmbinder.A2A.Client              # call agents
```

NuGet IDs carry the `RomanAmbinder.` prefix so they never clash with the official `A2A` packages. Assemblies and namespaces are `A2A.*`. The core packages depend only on `Microsoft.Extensions.*`; the gRPC packages add `Google.Protobuf` and `Grpc.*`.

| Package (NuGet ID: `RomanAmbinder.` + name) | What it is |
|---|---|
| `A2A.Protocol` | Data model (from `a2a.proto`), ProtoJSON serialization, errors, JCS (RFC 8785), JWS card signing/verification |
| `A2A.Client` | `A2AClient`, JSON-RPC + REST transports, SSE reader, card resolver with caching and signature policy, DI |
| `A2A.Server` | Transport-agnostic server core: executor model, task lifecycle, stores, event bus, push sender |
| `A2A.Server.AspNetCore` | Endpoints: JSON-RPC, REST, SSE, `/.well-known/agent-card.json`, JWKS |
| `A2A.Grpc` | Generated types/stubs for `lf.a2a.v1.A2AService` (from `docs/a2a.proto`) and model mapping |
| `A2A.Server.Grpc` | `MapA2AGrpc()`: the gRPC binding on ASP.NET Core |
| `A2A.Client.Grpc` | `GrpcTransport` and `A2AGrpcClientFactory` |
| `A2A.Server.Postgres` | PostgreSQL stores (tasks, events, push configs, idempotency) via Npgsql |
| `A2A.Server.Redis` | Redis pub/sub `TaskEventBus` for running several server instances |

## Server in 20 lines

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = new AgentCard
{
    Name = "My Agent", Description = "...", Version = "1.0.0",
    SupportedInterfaces =
    [
        new() { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        new() { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
    ],
    Capabilities = new() { Streaming = true, PushNotifications = true },
    DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
    Skills = [new() { Id = "x", Name = "X", Description = "...", Tags = ["x"] }],
}).AddAgentExecutor<MyAgent>();
builder.Services.AddA2AServerAspNetCore();   // registers the startup check and the default rate limit (required by MapA2A)

app.UseAuthentication();
app.UseA2A();                               // rate limiter middleware; the app refuses to start without it (or o.EnableRateLimiting = false)
app.UseAuthorization();

app.MapAgentCard();                         // GET /.well-known/agent-card.json (anonymous, cacheable)
app.MapA2A("/a2a").RequireAuthorization();  // JSON-RPC at POST /a2a, REST at /a2a/message:send, /a2a/tasks/...
// Without RequireAuthorization() the app refuses to start, unless you set o.AllowAnonymous = true.
// Limits (o.Limits): body 10 MB, 100 parts per message, JSON depth 32, history 100, page size 100. Rate limit per tenant and
// principal (anonymous: per remote IP): 20 burst, 60 per minute (services.AddA2ARateLimiting(o => ...) to change). Both answer 429/413 with the right headers.
// Tenants: MapA2A("/{tenant}/a2a") or the default /a2a/{tenant} variants set ServerCallContext.Tenant.

class MyAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct)
    {
        await updater.StartWorkAsync(cancellationToken: ct);
        await updater.AddArtifactAsync([Part.FromText($"You said: {ctx.UserText}")], name: "answer", cancellationToken: ct);
        await updater.CompleteAsync(cancellationToken: ct);
    }
}
```

The executor is resolved from a **new DI scope per execution**, so scoped services (DbContext etc.) are safe.
Call `updater.ReplyAsync(...)` instead for a stateless reply with no task.

## Client

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
var reply = await client.SendMessageAsync("hello");

await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("stream this")))
    if (evt is StatusUpdateEvent { StatusUpdate: var s }) Console.WriteLine(s.Status.State);
```

Every client method takes optional `A2ARequestOptions` (headers, timeout, idempotency key, extensions, `ConfigureRequest`). `SendMessage` always sends an `Idempotency-Key` (default: the message id). Unary calls retry up to `MaxRetries` times (default 2, so 3 attempts; set it on `A2AClientOptions` or per call, 0 disables) on 503, 429, errors marked retryable, and transport errors. For `SendMessage` a transport error is retried only when the request never reached the server (connect, DNS, proxy tunnel or TLS failure), so a server without `Idempotency-Key` support cannot create the task twice. Creating or deleting a push config and streams are not retried.

DI: `services.AddA2AClient("weather", o => o.AgentUrl = ...).AddBearerToken(...)`, then `IA2AClientFactory.GetClientAsync("weather")`.

The factory picks the first interface **in the card's order** that the client supports (spec §8.3.2), and sets `tenant` from it.

## gRPC

```csharp
// Server: gRPC needs HTTP/2 (Kestrel: HttpProtocols.Http2, or Http1AndHttp2 over TLS).
builder.Services.AddA2AServerAspNetCore();   // required, as for MapA2A
builder.Services.AddA2AGrpc();
app.UseAuthentication();
app.UseA2A();
app.UseAuthorization();
app.MapA2AGrpc().RequireAuthorization();   // same fail-closed startup check and "a2a" rate limit as MapA2A
// Card: new AgentInterface { Url = "https://agent.example.com:443", ProtocolBinding = ProtocolBindings.Grpc }

// Client
var client = A2AGrpcClientFactory.Create(card);
```

The gRPC service runs on the same `A2ARequestHandler` and call-context plumbing as the HTTP bindings, so ownership, tenants
(the request's `tenant` field), limits, idempotency and errors behave the same; the parity suite runs every method over all three bindings.
Errors are `google.rpc.Status` with an `ErrorInfo` detail (`retryable`/`retryAfter` metadata, plus `RetryInfo` on rate limits).
`A2A-Version`, `A2A-Extensions` and `Idempotency-Key` travel as `a2a-version` / `a2a-extensions` / `idempotency-key` metadata.
gRPC error responses do not carry the `a2a-extensions` (A2A-Extensions) response header; successful responses do.
A message larger than `Limits.MaxRequestBodyBytes` is refused by the gRPC layer with `RESOURCE_EXHAUSTED` (the client reports it as `InvalidRequest` / `PAYLOAD_TOO_LARGE`, as over HTTP).
`GrpcTransport` honours `A2ARequestOptions` (headers as metadata, per-attempt deadline, idempotency key, `MaxRetries` and the same retry rules)
except `ConfigureRequest`, which throws `NotSupportedException`.
**Stream resume is not supported over gRPC yet**: there is no event cursor on the wire, so after a lost stream call `SubscribeToTask` again.
Proto ↔ model conversion goes through ProtoJSON (the model's JSON form *is* ProtoJSON), so all three bindings share one mapping.
`A2AClientFactory` still chooses between JSON-RPC and HTTP+JSON only; use `A2AGrpcClientFactory` for gRPC.

## Behavior and spec decisions

These are places where the spec is loose or self-contradictory. Here is what this SDK does.

**Security (stricter than the spec's SHOULDs)**
- **Task ownership** (§13.1): each task is bound to the caller id (`sub` → NameIdentifier → Name) and tenant. Other callers get `TaskNotFound` and never learn the task exists. `ListTasks` filters before counting. Anonymous callers share one scope, so put `RequireAuthorization()` on the endpoints in production. The rate limit and the open-stream budget are per remote IP address for anonymous callers (`anon:{ip}`); behind a reverse proxy enable the ForwardedHeaders middleware (`app.UseForwardedHeaders()` with the proxy in `KnownProxies`/`KnownNetworks`) before `UseA2A()`, or all anonymous callers share the proxy's budget.
- **Push SSRF guard**: HTTPS required. Loopback, private, link-local (incl. cloud metadata 169.254/16), CGNAT, and ULA targets are rejected, both at registration and **at connect time against the resolved IP** (blocks DNS rebinding). Redirects are disabled.
- **Card verification** (`CardTrustPolicy`): a signature counts only if its key is pinned for the card's origin (`TrustedKeys`) or served from a `jku` host allowlisted for that same origin (`AllowedJkuHosts[origin]` or `AllowJkuHost(origin, host)`, HTTPS, fetched through the push SSRF guard; a host allowed for one origin never verifies cards from another). A key the card points at by itself is never trusted, so a forged card is `Unverified`, never `Verified`. `typ` must be `JOSE` or `JOSE+JSON`; algorithm allowlist (ES256/384/512, RS*, PS*; never `none` or HS*); unknown `crit` is rejected; key type must match `alg`. `Failed` always stops resolution. `Unsigned`/`Unverified` cards are rejected once trust is configured (`RequireSignature` to override); an unsigned card that is accepted counts `a2a.card.unsigned`.
- **Card pinning and untrusted text**: the resolver pins the SHA-256 of each origin's canonical card. A change raises `CardChanged`, logs a warning and counts `a2a.card.changed`; `PinPolicy.Strict` fails instead. Key rotation with the same content is not a change. `ResolvedAgentCard` exposes description and skill text as `UntrustedText` (control and bidi characters stripped, 2000 characters max); identity is `(origin, kid)`, not `name`.
- **Executor errors** become a `Failed` task with a generic message. Exception details are logged, not sent.
- **No redirects on A2A calls**: a response that came through a redirect is refused (`InvalidAgentResponse`). A handler that follows redirects re-sends custom credential headers to the new host (a2a-java CVE-2026-111763), so use `AddA2AClient` (redirects off) or a handler with `AllowAutoRedirect = false` when you pass your own `HttpClient`.
- **Fetching `UrlPart` URLs** (§14.1.1): use `A2A.Net.SafeHttp` (`CreateClient`, `DownloadAsync`). It applies the push SSRF guard (HTTPS only, private ranges blocked by resolved IP, no redirects) and a size cap.
- **Task time limit** (`MaxTaskDuration`, off by default): one execution (one incoming message) that runs longer moves the task to `FAILED` ("Task exceeded its time limit"), sent to streams and webhooks like any final event, and its cancellation token fires. A task waiting in `INPUT_REQUIRED`/`AUTH_REQUIRED` with no agent running is not affected. The clock is the DI `TimeProvider`.
- **Turn cap** (`MaxTurnsPerTask`, off by default): a defense against multi-turn "agent session smuggling" (Unit 42). A follow-up that would push a task over the cap is refused with `UnsupportedOperation` before the agent runs. Turns are the user messages in the stored history, so a custom `TaskStore` must keep the full history. An idempotent retry is not a new turn.
- **Client per-message hook** (`A2AClientOptions.OnMessageReceived`): called for every agent message `A2AClient` receives (direct replies, status update messages, the status message of task snapshots; unary and streamed; all bindings). Throw to abort the call. Use it to check each turn against the user's original intent and to ask a human before sensitive actions. The same snapshot message can arrive more than once, so keep the check idempotent.

**Signing and forward compatibility**
- Canonicalization matches a2a-python 1.2.1 and a2a-js 1.3.0: the card is reduced to the A2A v1.0 Agent Card schema, empty values are pruned, then RFC 8785 (JCS) is applied. **Fields outside the v1.0 schema (newer versions, extensions, v0.3 compatibility fields) and pruned values are not covered by the signature.** The resolver builds a verified card only from the signed content, so unsigned fields never reach it (see `tests/A2A.Tests/Vectors/README.md`).

**Protocol choices**
- **Missing `A2A-Version`** means 0.3 per §3.6.2 and is rejected. Set `TreatMissingVersionAsCurrent = true` to accept curl-style calls.
- **Proto field names**: requests may use proto names (`history_length`) as well as JSON names (`historyLength`), in JSON bodies and REST query strings (ProtoJSON parser rule, A2A #2087). Keys inside `metadata`, data parts, extension `params` and maps keep their spelling. Both spellings of one field in one object is `InvalidParams`.
- **Stream errors**: an error after a REST stream started is sent as an SSE frame named `error` with a `google.rpc.Status` body (A2A #1262; what a2a-python reads). JSON-RPC stream errors stay unnamed JSON-RPC error responses. The client accepts both, and skips stream events of a type it does not know (a newer protocol version) instead of ending the stream (A2A #2269).
- **Extensions**: required extensions are enforced. The server **echoes the activated extensions** in the `A2A-Extensions` response header, so clients can tell what was honored.
- **REST subscribe**: the spec text says `POST /tasks/{id}:subscribe` but the normative proto says `GET`. The server accepts both. The client defaults to POST (`RestSubscribeMethod` to change).
- **Stream closing**: a send stream ends on a terminal state (also one set by another execution, such as CancelTask) or when the executor returns and no other execution of the task is running. `AUTH_REQUIRED` keeps the stream open while the executor waits for out-of-band credentials (§7.6.1). `SubscribeToTask` ends on a terminal state.
- **Push delivery**: events for one task are delivered in order, at least once, with exponential backoff (408/429/5xx retried). Each POST carries `A2A-Version`, `X-A2A-Notification-Token` and `X-A2A-Notification-Sequence` (an SDK extension: a per-task counter for deduplication and ordering; the spec defines neither).
- **Auth errors**: the HTTP layer answers 401/403 with `WWW-Authenticate`. Over JSON-RPC the body uses code `-32603` with an `ErrorInfo` reason of `UNAUTHENTICATED` / `PERMISSION_DENIED`, since the spec assigns no JSON-RPC number.
- **Artifacts**: `append: true` concatenates parts onto the stored artifact; otherwise the artifact is replaced.

## Durable stores and several instances

The default stores are in memory: fine for dev and tests, lost on restart, and one instance only. For production:

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = card)
    .AddAgentExecutor<MyAgent>()
    .AddPostgresStores(postgresConnectionString)   // RomanAmbinder.A2A.Server.Postgres
    .AddRedisEventBus(redisConfiguration);         // RomanAmbinder.A2A.Server.Redis
builder.Services.AddDataProtection();              // persist and share the key ring across instances
builder.Services.AddA2ADataProtectionPushSecrets();
```

- `A2A.Server.Postgres` stores tasks, event logs, push configs and idempotency keys, and creates its tables at startup ([README](src/A2A.Server.Postgres/README.md)).
- `A2A.Server.Redis` fans live events out to every instance ([README](src/A2A.Server.Redis/README.md)).
- Push secrets need a protector with persisted, shared keys; the default one is per process.
- The handler's per-task lock is per process. Across instances, the stores' version checks keep task writes consistent.
- Both packages pass the same contract tests as the in-memory versions (`tests/A2A.Stores.Tests`, Docker via Testcontainers).

For another database, implement `TaskStore`, `EventStore`, `PushConfigStore` and `IdempotencyStore`, and run the contract tests against it.

## Not included yet

- A webhook-receiver helper for clients

## Interop

Tested both ways against the official Python SDK (`a2a-sdk` 1.3.0) over JSON-RPC, HTTP+JSON, and gRPC: .NET client → Python agent and Python client → .NET agent. See [interop/](interop/README.md). CI runs it on every push.

## Build and test

```bash
dotnet build A2A.slnx
dotnet test tests/A2A.Tests
dotnet test tests/A2A.Stores.Tests   # store contract tests; the Postgres/Redis ones need Docker and skip without it
```

Run the sample:

```bash
dotnet run --project samples/EchoAgent
dotnet run --project samples/EchoClient
```

`docs/a2a.proto` is the normative proto this SDK was built against.

## Releasing

Push a version tag. The `Release` workflow builds, tests, packs all `src/` projects (with SourceLink and `.snupkg` symbols), pushes them to nuget.org, and creates a GitHub release. A tag with a suffix (`v0.2.0-preview.1`) publishes a prerelease.

```bash
git tag v0.1.0
git push origin v0.1.0
```

One-time setup: add a nuget.org API key as the repository secret `NUGET_API_KEY`, scoped to push `RomanAmbinder.A2A.*`.
