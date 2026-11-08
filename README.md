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
| `A2A.Client` | `A2AClient`, JSON-RPC + REST transports, SSE reader, card resolver with caching and signature policy |
| `A2A.Client.DependencyInjection` | `AddA2AClient`, `IA2AClientFactory`, `AddBearerToken` (adds DI, Options, `IHttpClientFactory`) |
| `A2A.Server` | Transport-agnostic server core: executor model, task lifecycle, stores, event bus, push sender |
| `A2A.Server.AspNetCore` | Endpoints: JSON-RPC, REST, SSE, `/.well-known/agent-card.json`, JWKS |
| `A2A.Grpc` | Generated types/stubs for `lf.a2a.v1.A2AService` (from `docs/a2a.proto`) and model mapping |
| `A2A.Server.Grpc` | `MapA2AGrpc()`: the gRPC binding on ASP.NET Core |
| `A2A.Client.Grpc` | `GrpcTransport` and `A2AGrpcClientFactory` |

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

DI (package `A2A.Client.DependencyInjection`): `services.AddA2AClient("weather", o => o.AgentUrl = ...).AddBearerToken(...)`, then `IA2AClientFactory.GetClientAsync("weather")`.

The factory picks the first interface **in the card's order** that the client supports (spec §8.3.2), and sets `tenant` from it.

## gRPC

```csharp
// Server: gRPC needs HTTP/2 (Kestrel: HttpProtocols.Http2, or Http1AndHttp2 over TLS).
builder.Services.AddA2AGrpc();
app.MapA2AGrpc();
// Card: new AgentInterface { Url = "https://agent.example.com:443", ProtocolBinding = ProtocolBindings.Grpc }

// Client
var client = A2AGrpcClientFactory.Create(card);
```

The gRPC service runs on the same `IA2ARequestHandler` as the HTTP bindings, so auth scoping, validation, and errors behave the same. A binding of your own resolves `IA2ARequestHandler` from DI and gets the same operations.
Errors are `google.rpc.Status` with an `ErrorInfo` detail. `A2A-Version` and `A2A-Extensions` travel as `a2a-version` / `a2a-extensions` metadata.
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

**Signing and forward compatibility**
- Canonicalization matches a2a-python 1.2.1 and a2a-js 1.3.0: the card is reduced to the A2A v1.0 Agent Card schema, empty values are pruned, then RFC 8785 (JCS) is applied. **Fields outside the v1.0 schema (newer versions, extensions, v0.3 compatibility fields) and pruned values are not covered by the signature.** The resolver builds a verified card only from the signed content, so unsigned fields never reach it (see `tests/A2A.Tests/Vectors/README.md`).

**Protocol choices**
- **Missing `A2A-Version`** means 0.3 per §3.6.2 and is rejected. Set `TreatMissingVersionAsCurrent = true` to accept curl-style calls.
- **Extensions**: required extensions are enforced. The server **echoes the activated extensions** in the `A2A-Extensions` response header, so clients can tell what was honored.
- **REST subscribe**: the spec text says `POST /tasks/{id}:subscribe` but the normative proto says `GET`. The server accepts both. The client defaults to POST (`RestSubscribeMethod` to change).
- **Stream closing**: a send stream ends on a terminal state (also one set by another execution, such as CancelTask) or when the executor returns and no other execution of the task is running. `AUTH_REQUIRED` keeps the stream open while the executor waits for out-of-band credentials (§7.6.1). `SubscribeToTask` ends on a terminal state.
- **Push delivery**: events for one task are delivered in order, at least once, with exponential backoff (408/429/5xx retried). Each POST carries `X-A2A-Notification-Token` and `X-A2A-Notification-Sequence` (an SDK extension: a per-task counter for deduplication and ordering; the spec defines neither).
- **Auth errors**: the HTTP layer answers 401/403 with `WWW-Authenticate`. Over JSON-RPC the body uses code `-32603` with an `ErrorInfo` reason of `UNAUTHENTICATED` / `PERMISSION_DENIED`, since the spec assigns no JSON-RPC number.
- **Artifacts**: `append: true` concatenates parts onto the stored artifact; otherwise the artifact is replaced.
- **Telemetry**: the server's `ActivitySource` and `Meter` are both named `A2A.Server` (`A2AServerTelemetry.ActivitySourceName`, `.MeterName`); the client's `ActivitySource` is `A2A.Client` (`A2AClientTelemetry.ActivitySourceName`), for every binding. Each operation is a span `a2a.server.{operation}` / `a2a.client.{operation}` tagged `a2a.operation` and `a2a.outcome` (`ok`, `canceled` when the caller canceled or stopped reading, `error:{A2AErrorCode}`; other cancellations, such as timeouts, are `error:Internal`); server spans also carry `a2a.tenant` and `a2a.task_id_hash` (16 hex characters of the task id's SHA-256), never bodies or principals. A streaming server span lasts until the stream ends, and the agent work it starts runs inside it; a client stream span covers opening it. While a client span is current, `HttpClient` sends it as `traceparent`. Server counters: `a2a.server.requests`, `a2a.server.events`, `a2a.server.streams.opened`, `a2a.server.push.deliveries`. With no listener nothing is created.

## Not included yet

- Durable stores (implement `TaskStore` / `PushConfigStore`; the in-memory ones are for dev and tests)
- A multi-instance event bus (`TaskEventBus` is process-local; subclass it over Redis or similar)
- A webhook-receiver helper for clients

## Interop

Tested both ways against the official Python SDK (`a2a-sdk` 1.3.0) over JSON-RPC, HTTP+JSON, and gRPC: .NET client → Python agent and Python client → .NET agent. See [interop/](interop/README.md). CI runs it on every push.

## Build and test

```bash
dotnet build A2A.slnx
dotnet test tests/A2A.Tests
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
