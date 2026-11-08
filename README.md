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
| `A2A.Security.Http` | SSRF-safe outbound HTTP handler (private-address guard), guarded `jku` key-set fetching and `SafeHttp` for peer-chosen URLs, shared by the client and the push sender |
| `A2A.Client` | `A2AClient`, JSON-RPC + REST transports, SSE reader, card resolver with caching and signature policy |
| `A2A.Client.DependencyInjection` | `AddA2AClient`, `IA2AClientFactory`, `AddBearerToken` (adds DI, Options, `IHttpClientFactory`) |
| `A2A.Server` | Transport-agnostic server core: executor model, task lifecycle, stores, event bus, durable push delivery (outbox), execution coordination, retention, tracing and metrics |
| `A2A.Server.AspNetCore` | Endpoints: JSON-RPC, REST, SSE, `/.well-known/agent-card.json`, JWKS |
| `A2A.Grpc` | Generated types/stubs for `lf.a2a.v1.A2AService` (from `docs/a2a.proto`) and model mapping |
| `A2A.Server.Grpc` | `MapA2AGrpc()`: the gRPC binding on ASP.NET Core |
| `A2A.Client.Grpc` | `GrpcTransport` and `A2AGrpcClientFactory` |
| `A2A.V0_3Compat` | Shared v0.3 JSON codecs and Agent Card conversion (used by the two packages below) |
| `A2A.Server.V0_3Compat` | `EnableV03Compat()`: lets v0.3 clients call a v1.0 agent |
| `A2A.Client.V0_3Compat` | `V03CompatClientFactory`: lets a client call v0.3 agents |
| `A2A.Extensions.AI` | `Microsoft.Extensions.AI` integration: host an `IChatClient` as an agent (`AddChatClientAgent`), call a remote agent as an AI tool (`AsAIFunction`) or as an `IChatClient` (`AsChatClient`) |
| `A2A.Server.Postgres` | PostgreSQL stores (tasks, events, push configs, push outbox, idempotency) via Npgsql |
| `A2A.Server.Redis` | Redis pub/sub `TaskEventBus` and execution coordinator for running several server instances |

## Documentation

Guides and API reference are built from [docs/site](docs/site/README.md) with DocFX. CI uploads the built site as the `docs-site` artifact on every run. The guides pull their C# samples from compiled snippets, so read them on the built site to see the code.

Start with [Getting started](docs/site/guides/getting-started.md). Then: [bindings](docs/site/guides/bindings.md), [authentication and ownership](docs/site/guides/auth-and-ownership.md), [streaming](docs/site/guides/streaming.md), [push notifications](docs/site/guides/push-notifications.md), [Agent Card signing](docs/site/guides/agent-card-signing.md), [multi-tenancy](docs/site/guides/multi-tenancy.md), [durable stores](docs/site/guides/durable-stores.md), [retention](docs/site/guides/retention.md), [observability](docs/site/guides/observability.md), [limits](docs/site/guides/limits-and-rate-limiting.md), [task safety](docs/site/guides/task-safety.md), [interop](docs/site/guides/interop-and-conformance.md), [spec decisions](docs/site/guides/spec-decisions.md).

## Server in 20 lines

```csharp
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddA2A(a => a
    .AddAgentExecutor<MyAgent>()
    .AddAgentCard(new AgentCard { Name = "My Agent", Description = "Says hello.", Version = "1.0.0" }));

var app = builder.Build();
app.UseAuthentication();
app.UseA2A();                               // rate limiter middleware; the app refuses to start without it
app.UseAuthorization();
app.MapA2A("/a2a").RequireAuthorization();  // JSON-RPC at POST /a2a, REST at /a2a/message:send, ...; and the Agent Card at /.well-known/agent-card.json

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

What this gives you, with nothing else to configure:

- `AddA2A` is `AddA2AServer()` + `AddA2AServerAspNetCore()` + your builder calls. Use the two-step form when you need `A2AServerOptions` at registration.
- `MapA2A` also maps the public Agent Card (`GET /.well-known/agent-card.json`, anonymous, cacheable; and the JWKS when a signing key is set). On a route group (`app.MapGroup("/api")`) it does not: call `app.MapAgentCard()` on the application root. Turn it off with `o.MapAgentCard = false`.
- Authorization: `MapA2A("/a2a", o => o.AuthorizationPolicy = "MyPolicy")` or `.RequireAuthorization()`. Without either, the app refuses to start, unless you set `A2AServerOptions.AllowAnonymous = true`.
- The card needs only `Name`, `Description` and `Version`. Empty `SupportedInterfaces` are completed once, at first serve, from the mapped bindings (`MapA2A`, `MapA2AJsonRpc`, `MapA2AHttpJson`, `MapA2AGrpc`) at `A2AHttpOptions.PublicBaseUrl` (else the first listen address) + the mapped pattern. Set `PublicBaseUrl` behind a proxy or load balancer (`services.ConfigureA2AHttp(h => h.PublicBaseUrl = new Uri("https://agent.example.com"))`). Patterns with `{tenant}` are not listed, and a card that ends up with no interface fails at first serve with an `InvalidOperationException`.
- Limits (`o.Limits`): body 10 MB, 100 parts per message, JSON depth 32, history 100, page size 100. Rate limit per tenant and principal (anonymous: per remote IP): 20 burst, 60 per minute (`services.AddA2ARateLimiting(o => ...)` to change; `o.Enabled = false` turns it off and lifts the `UseA2A()` requirement). Both answer 429/413 with the right headers.
- Tenants: `MapA2A("/{tenant}/a2a")` or the default `/a2a/{tenant}` variants set `ServerCallContext.Tenant`.
- A full sample with API-key auth, card signing and streaming is in [samples/EchoAgent](samples/EchoAgent/Program.cs).

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

DI (package `A2A.Client.DependencyInjection`, which adds DI, Options and `IHttpClientFactory`): `services.AddA2AClient("weather", o => o.AgentUrl = ...).AddBearerToken(...)`, then `IA2AClientFactory.GetClientAsync("weather")`. The clients are cached per name; the returned `IHttpClientBuilder` takes auth handlers and resilience. See [samples/EchoClient](samples/EchoClient/Program.cs).

The factory picks the first interface **in the card's order** that the client supports (spec §8.3.2), and sets `tenant` from it.

## gRPC

```csharp
// Server: gRPC needs HTTP/2 (Kestrel: HttpProtocols.Http2, or Http1AndHttp2 over TLS).
builder.Services.AddA2AGrpc();               // includes AddA2AServerAspNetCore() (startup check, rate limit policy)
app.UseAuthentication();
app.UseA2A();
app.UseAuthorization();
app.MapA2AGrpc().RequireAuthorization();   // same fail-closed startup check and "a2a" rate limit as MapA2A
// Card: new AgentInterface { Url = "https://agent.example.com:443", ProtocolBinding = ProtocolBindings.Grpc }

// Client: let the card choose (its interface order wins) ...
var client = await A2AClientFactory.CreateAsync(agentUrl, httpClient, new A2AClientOptions().UseGrpc());
// ... or require gRPC
var grpcClient = await A2AGrpcClientFactory.CreateAsync(agentUrl, httpClient);   // or A2AGrpcClientFactory.Create(card)
```

The gRPC service runs on the same `IA2ARequestHandler` and call-context plumbing as the HTTP bindings, so ownership, tenants
(the request's `tenant` field), limits, idempotency and errors behave the same; the parity suite runs every method over all three bindings.
A binding of your own resolves `IA2ARequestHandler` from DI and gets the same operations.
Errors are `google.rpc.Status` with an `ErrorInfo` detail (`retryable`/`retryAfter` metadata, plus `RetryInfo` on rate limits).
`A2A-Version`, `A2A-Extensions` and `Idempotency-Key` travel as `a2a-version` / `a2a-extensions` / `idempotency-key` metadata.
gRPC error responses do not carry the `a2a-extensions` (A2A-Extensions) response header; successful responses do.
A message larger than `Limits.MaxRequestBodyBytes` is refused by the gRPC layer with `RESOURCE_EXHAUSTED` (the client reports it as `InvalidRequest` / `PAYLOAD_TOO_LARGE`, as over HTTP).
`GrpcTransport` honours `A2ARequestOptions` (headers as metadata, per-attempt deadline, idempotency key, `MaxRetries` and the same retry rules)
except `ConfigureRequest`, which throws `NotSupportedException`.
**Stream resume** (`A2AClient.ObserveTaskAsync`, JSON-RPC and HTTP+JSON) reconnects with `Last-Event-ID` and skips events it already delivered. If the server lost its event log and starts over with a fresh task snapshot (a sequence below the cursor), the client takes the snapshot and continues from there instead of dropping events. **Stream resume is not supported over gRPC yet**: there is no event cursor on the wire, so after a lost stream call `SubscribeToTask` again.
Proto ↔ model conversion goes through ProtoJSON (the model's JSON form *is* ProtoJSON), so all three bindings share one mapping.
Without `UseGrpc()`, `A2AClientFactory` chooses between JSON-RPC and HTTP+JSON only. With it, gRPC joins the choice. `A2AGrpcClientFactory` always uses gRPC.

## Design principles

The SDK ranks its goals in this order: **safe > easy > performant > extendable > modular**. When two collide, the higher one wins: secure defaults before a short hello world, a short hello world before speed, and so on. The reasoning, the rules and the non-goals are in [docs/superpowers/specs/2026-11-07-sdk-design-principles-design.md](docs/superpowers/specs/2026-11-07-sdk-design-principles-design.md). CI checks them with gates: a public API snapshot (PublicApiAnalyzers), XML docs as errors, benchmarks with an allocation regression check (non-blocking until the baseline is regenerated on CI hardware), an AOT publish of the samples, and a line budget for the hello-world samples (`HelloWorldGateTests`).

## Extending

Every seam below is a DI registration or a builder call. Defaults are safe and in-memory; replace only what you need.

- `IA2AOperationFilter` (+ `A2AFilterStage`): wrap every operation; `A2AServerBuilder.AddOperationFilter<T>()`.
- `IA2ARequestHandler`: the operation set every binding calls; resolve it from DI to write a binding of your own.
- Stores: `TaskStore` (its `CommitAsync` writes the task, the event and its push deliveries as one unit), `EventStore`, `PushConfigStore`, `PushOutbox`, `IdempotencyStore`. A store keeps and returns `SequencedEvent.Json` (the event encoded once) as it is: for the log, and as the push payload. An `EventStore` must return stored `SequencedEvent.Json` bytes unchanged: they are written to the wire unvalidated. `InMemoryEventStore` keeps each retained event as the model plus its UTF-8 bytes, so `StreamingOptions.ReplayBufferSize` costs roughly double the model's memory.
- `IAgentExecutorFactory`: supplies the executor per execution and tenant (default: a new DI scope, with the `AgentExecutorResolver` when one is registered), so the core can run without a DI container.
- `IAgentCardProvider` / `AgentCardProvider`: supply the public card, and each tenant's completed card (`GetAsync`, fed by a `TenantCardProvider`). `AddA2AServerAspNetCore` replaces this registration with the completing provider, so register a custom provider after it. `A2AServerOptions.ExtendedAgentCardProvider` has the signature `(call, completedCard, cancellationToken)`.
- `ExecutionCoordinator`: running executions and cancel requests across instances (default in-memory; `AddRedisExecutionCoordinator`).
- Push delivery: the hosted `PushDeliveryService` sends the `PushOutbox` through the keyed `HttpMessageInvoker` `PushDeliveryService.InvokerServiceKey` (SSRF-guarded); retries are `A2AServerOptions.PushDelivery`.
- `IResumableTransport` (client): lets a custom transport support `ObserveTaskAsync` resume with `Last-Event-ID`.
- Extension operations: `A2AServerBuilder.AddExtensionOperation(...)`. A policy denial happens in the HTTP binding before the pipeline: no span, no counter, and `BeforeAuthorization` filters do not see it. `allow: true` admits every caller the endpoint admits, including anonymous callers when `AllowAnonymous` is set.
- `A2AAuthorizationHandler`: decides per operation; it receives an `A2AOperationDescriptor`.
- Telemetry: the `ActivitySource` names `A2A.Server` and `A2A.Client` and the `Meter` `A2A.Server` (constants on `A2ATelemetry`).
- `IA2ARejectionWriter` (ASP.NET Core): writes the rate limit rejection in a binding's own format (the gRPC binding registers one).
- `ITokenExchanger` (`A2A.Delegation`): swaps the caller's token for a down-scoped one (RFC 8693) before a delegating agent calls the next agent. `A2AServerBuilder.AddTokenExchanger<T>()` or `AddTokenExchanger(instance)`; `A2A.Client.Delegation.OAuthTokenExchanger` is the ready-made implementation. See [Delegation](docs/site/guides/auth-and-ownership.md).
- Client: `ITransportBinding` names a custom transport's binding on `A2AClient` spans; `RetryPolicy`/`RetryMode` are the retry rules every transport shares; `A2AClientOptions.OnMessageReceived` sees every agent message.

## Observability

Tracing uses `System.Diagnostics.ActivitySource`, so the core packages need no OpenTelemetry package. With OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource("A2A.Server", "A2A.Client")   // A2ATelemetry.ServerSourceName, ClientSourceName
    .AddAspNetCoreInstrumentation()
    .AddOtlpExporter());
```

| Source | Span | Kind | When |
|---|---|---|---|
| `A2A.Server` | `a2a.{Operation}` (`a2a.SendMessage`, `a2a.GetTask`, ...) | Server, or Internal under ASP.NET Core's request span | every `A2ARequestHandler` operation, all bindings |
| `A2A.Server` | `a2a.agent.execute` | Internal | one agent run (execute or cancel); child of the operation span |
| `A2A.Server` | `a2a.push.deliver` | Client | one attempt to send one event to one webhook; child of the agent run (also after a retry or on another instance) |
| `A2A.Client` | `a2a.{Operation}` | Client | every `A2AClient` operation, retries and stream reconnects included |

- **Propagation**: the client transports send the current span as W3C `traceparent`/`tracestate` on JSON-RPC, HTTP+JSON and gRPC (metadata). The server reads them on all three bindings (`ServerCallContext.TraceParent`), so client and server spans share one trace. Webhook POSTs carry the `traceparent` of their delivery span. A `traceparent` in `A2ARequestOptions.Headers` replaces the SDK's.
- **Streams**: one span covers the whole stream; each event is a span event `a2a.stream.event` (tags `a2a.event.kind`, `a2a.event.sequence` on the server, `a2a.task.state`), not a child span. Client reconnects are `a2a.retry` events.
- **Tags**: `rpc.system` (`a2a`), `rpc.method`, `a2a.binding` (`JSONRPC`, `HTTP+JSON`, `GRPC`), `a2a.task.id`, `a2a.context.id`, `a2a.tenant` (non-default tenants only), `a2a.task.state`, `a2a.stream.event_count`; on agent runs `a2a.execution.kind` and `a2a.execution.timed_out`; on deliveries `a2a.push.config.id`, `a2a.push.sequence`, `a2a.push.attempts`, `server.address` (host only) and `http.response.status_code`.
- **Errors**: a failed operation gets status `Error`, `error.type` and, for A2A errors, `a2a.error.code` (the `A2AErrorCode` name, e.g. `TaskNotFound`). An agent run that throws, hits `MaxTaskDuration` (`error.type` `TimeLimit`) or leaves the task `FAILED` (`TaskFailed`) is an error too. A cancellation the caller asked for sets `a2a.canceled` and is not an error.
- **No content**: tags never hold message text, parts, metadata, tokens, credentials, webhook URLs or exception messages.

**Metrics** (meter `A2A.Server`, `AddMeter("A2A.Server")`): `a2a.server.requests` (tags `rpc.method`, `a2a.outcome` = `ok`, `canceled` or `error`, and `error.type` on errors), `a2a.server.events` (events stored), `a2a.server.streams.opened` (streams admitted), `a2a.server.push.deliveries` (tag `a2a.outcome` = `sent`, `retried` or `dead_lettered`, one per attempt) and `a2a.server.push.dead_letters`. A stream the reader stops early counts as `canceled`.

The request span is the outermost step of the operation pipeline (around the host filters and authorization), so a refused or unauthenticated call still has a span with its error. Extension operations get `a2a.{method}` spans.

All names are constants on `A2A.Diagnostics.A2ATelemetry`.

## Behavior and spec decisions

Where the spec is loose or self-contradictory, the SDK makes a choice. The full list is in [Spec decisions](docs/site/guides/spec-decisions.md).

## Durable stores and several instances

The default stores are in memory: fine for dev and tests, lost on restart, and one instance only. For production:

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = card)
    .AddAgentExecutor<MyAgent>()
    .AddPostgresStores(postgresConnectionString)   // RomanAmbinder.A2A.Server.Postgres
    .AddRedisEventBus(redisConfiguration);         // RomanAmbinder.A2A.Server.Redis
builder.Services.AddA2ARetention();                // opt-in, see below
builder.Services.AddDataProtection();              // persist and share the key ring across instances
builder.Services.AddA2ADataProtectionPushSecrets();
```

A runnable two-instance setup (Postgres, Redis, docker compose) is in [samples/MultiInstance](samples/MultiInstance/README.md).

- `A2A.Server.Postgres` stores tasks, event logs, push configs, the push outbox and idempotency keys, and creates its tables at startup ([README](src/A2A.Server.Postgres/README.md)).
- `A2A.Server.Redis` fans live events out to every instance, and `AddRedisEventBus` also registers its execution coordinator ([README](src/A2A.Server.Redis/README.md)).
- Push secrets need a protector with persisted, shared keys; the default one (`EphemeralPushSecretProtector`) is per process. With a durable push outbox and the default protector, startup logs an **error**: push configs with a token or credentials would be dead-lettered after a restart or when another instance sends them. Register `AddDataProtection()` with a persisted key ring shared by every instance, then `AddA2ADataProtectionPushSecrets()` (or your own `IPushSecretProtector`). Startup does not fail, so a host that does not use push keeps working.
- Deadlines in the Postgres stores use the database clock (`statement_timestamp()`), not each instance's clock: push outbox due times and leases, idempotency claim timeouts and retention, and event log retention. Instances whose clocks disagree still agree on what has expired, so skew cannot make two instances own one lease or delay a delivery. `PostgresStoreOptions.UseDatabaseClock = false` switches back to the `TimeProvider` (for tests with a fake clock). Task timestamps (`UpdatedAt`, status times) still come from the instance that wrote them, because they are part of the task; skew shifts when retention and `InterruptedTimeout` act on a task by that much. Skew larger than `Coordination.LeaseTtl` also weakens the sweep's "changed within the last `LeaseTtl`" guard for dead executions, so still keep instance clocks in sync (NTP).
- The handler's per-task lock is per process. Across instances, each task change, its event and its push deliveries are written in one transaction (`TaskStore.CommitAsync`) with a version check, so the event log follows the order of task versions.
- Both packages pass the same contract tests as the in-memory versions (`tests/A2A.Stores.Tests`, Docker via Testcontainers).

### Execution coordinator

The coordinator answers "is this task running anywhere?", delivers CancelTask to the instance that runs the agent, and keeps a lease per running execution. **In memory is the default** (one instance). For several instances use Redis:

```csharp
.AddRedisEventBus(redis)                                  // registers the coordinator too; RegisterCoordinator = false opts out
.AddRedisExecutionCoordinator(redis)                      // or: the coordinator alone
```

- Pass the **same connection string** to both calls (or one `IConnectionMultiplexer`). Two different strings throw at registration, because they would share one connection.
- On Redis Cluster the key prefix must map to one hash slot, for example `{a2a}:` (the Lua scripts touch several keys).
- `A2AServerOptions.Coordination` (`CoordinationOptions`): `LeaseTtl` 30 s, `Heartbeat` 10 s, `RunningPollInterval` 1 s. `LeaseTtl` must be longer than 2 x `Heartbeat`. The heartbeat also renews the request's idempotency claim.
- An execution whose heartbeats lapse past `LeaseTtl` (instance crash, long pause, Redis outage) is treated as **dead**. The retention sweep marks its task `FAILED`, drops its late events, and asks every instance to cancel it. An agent that is in fact still alive also stops at its next heartbeat, which finds its lease lost and the task ended. Exceptions: a lapsed execution on the sweeping instance itself is still seen running and registers again; a task that changed within the last `LeaseTtl` waits for a later sweep (a follow-up on another instance may not have registered yet).
- A heartbeat that finds its lease lost while the task still runs registers the execution again, under the same id. This includes a new task whose agent has sent no event yet (a new task is stored with its first event, so no stored task there does not mean it was deleted).
- With a durable task store and the in-memory coordinator, startup logs a **warning**: CancelTask, running checks and dead-execution recovery then stay on the instance that runs the agent. One instance can ignore it.

### Retention (opt-in)

`services.AddA2ARetention(o => ...)` adds a background sweep. **It is not on by default. Without it nothing is purged (terminal tasks and their data are never deleted), `InterruptedTimeout` does nothing (tasks waiting for input never time out), and executions whose instance died are never recovered** (their tasks stay `WORKING`). With a durable task store and no retention, startup logs a **warning** (event 253) that says so.

| Option (`A2ARetentionOptions`) | Default | Meaning |
|---|---|---|
| `TaskRetention` | 7 days | Terminal tasks are deleted after this, with events, push configs, deliveries and dead letters |
| `OrphanContextRetention` | 7 days | Contexts no task uses |
| `SweepInterval` | 5 minutes | One instance sweeps per interval (a cluster-wide lease) |
| `BatchSize` | 500 | New items read per batch. Items a step skips (for example a task whose follow-up still runs) stay listed and do not hold back the items after them |

Also done by the sweep: tasks waiting in `INPUT_REQUIRED`/`AUTH_REQUIRED` longer than `A2AServerOptions.InterruptedTimeout` (24 hours, `null` = never) are cancelled, and tasks of dead executions are failed. `A2ATaskAdmin.DeleteTaskAsync(key)` deletes one task and all its data on demand (a running task is cancelled first). Both `A2ATaskAdmin` and `A2APushAdmin` (dead letters: `ListDeadLettersAsync`, `RedriveAsync`) are plain services for your own admin endpoint: not exposed over A2A, so protect that endpoint.

### Approvals, cancel and follow-ups

- **Approval binding.** An executor that needs the user to confirm an action calls `updater.RequireApprovalAsync(new PendingApproval(operationId, exactDescription), "Delete 3 files?")`. The task goes to `INPUT_REQUIRED` and the status message carries the SHA-256 hash of the operation in metadata `a2a.approval.hash` (over `operationId + "\n" + exactDescription`, so `operationId` must not contain a line feed). The client approves by echoing that hash in the follow-up message's metadata. The executor then sees `context.Approval == ApprovalState.Approved`; `NotApproved` when the hash is missing, `None` when nothing is pending. A wrong hash, or a hash when nothing is pending, is refused with `InvalidParams` before the agent runs. An approval works **once**: after it is used, the same hash is `InvalidParams`. Call `RequireApprovalAsync` again to ask anew.
- **Cancel requested.** If the agent has not stopped within `CancelGracePeriod`, CancelTask answers with the task still `WORKING` and metadata `a2a.cancelRequested = true` (never stored). The `CANCELED` state arrives later on the stream.
- **Follow-up de-duplication.** A follow-up whose `messageId` is already in the task's history is not run again: the caller gets the task as it is, with no new turn, push config or execution.

### Several agents in one host

One agent serves one tenant. By default one card and one executor serve every tenant. To host many agents, register a card provider and an executor resolver:

```csharp
builder.Services.AddA2AServer()
    .AddTenantCardProvider<MyCards>()         // TenantCardProvider: card per tenant (and optional extended card, signing key)
    .AddAgentExecutorResolver<MyAgents>();    // AgentExecutorResolver: executor per tenant

app.MapAgentCard();                           // /.well-known/agent-card.json and /{tenant}/.well-known/agent-card.json
app.MapA2A("/{tenant}/a2a").RequireAuthorization();
// Keys: /.well-known/jwks.json and /{tenant}/.well-known/jwks.json (TenantCardProvider.GetSigningKeyAsync, else the host key)
```

- **Cards go through the `IAgentCardProvider`.** It completes each card the `TenantCardProvider` returns (extension entries; an empty `SupportedInterfaces` filled from the mapped bindings) and keeps the completed copy while that card instance lives, so a provider that returns the same instance is completed once and one that builds a new card is completed again. The provider is asked once per request, once per system status commit (retention sweep), never once per event. Cache inside the provider if building a card costs anything.
- **Unknown tenant** (the provider returns `null`): the public card is 404; SendMessage, SendStreamingMessage, ListTasks and GetExtendedAgentCard fail with `UnsupportedOperation`; every operation on a task (GetTask, CancelTask, SubscribeToTask, push configs) answers `TaskNotFound`, as for a task that does not exist.
- Capability checks (streaming, push, extensions, extended card) use the card of the request's tenant. Background work (retention, remote cancel, push delivery) knows only the tenant, so the tenant alone must identify the agent.

## Upgrading from 0.x / P0

Breaking changes for code that replaced SDK parts:

- **`IPushNotificationSender` and `HttpPushNotificationSender` are removed.** Push delivery is now a durable outbox sent by the hosted `PushDeliveryService`. Remove your registrations. To change how deliveries are sent, tune `A2AServerOptions.PushDelivery`; to read failures, use `A2APushAdmin`.
- **`MaxAttempts` and `InitialBackoff` moved** from `A2AServerOptions.PushNotifications` to `A2AServerOptions.PushDelivery` (with new `MaxBackoff` 5 min, `Lease`, `PollInterval`, `BatchSize`).
- **Custom `TaskStore`:** implement the new abstract `CommitAsync(TaskCommit)`: write the task (version check), append the event to the log your `EventStore` reads, and add the deliveries to the outbox your `PushOutbox` reads, as one atomic unit.
- **Custom stores must also implement the new members:** `TaskStore.DeleteAsync`, `ListExpiredAsync`, `ListInterruptedBeforeAsync`, `DeleteContextAsync`, `ListOrphanContextsAsync`; `EventStore.DeleteAsync`; `IdempotencyStore.RenewAsync`; `PushConfigStore.DeleteAllAsync` and `SaveIfUnderLimitAsync`; and a `PushOutbox` (`LeaseDueAsync`, `CompleteAsync`, `RescheduleAsync`, `DeadLetterAsync`, `ListDeadLettersAsync`, `RedriveAsync`, `DeleteAllAsync`). A custom `ExecutionCoordinator` is optional (the in-memory one is the default).
- **Postgres `IdempotencyClaimTimeout` default: 1 hour -> 2 minutes.** Running claims are now renewed by the execution heartbeat, so a live request keeps its key and a crashed instance frees it sooner. Set the old value if you relied on it.
- **`InMemoryTaskStore` constructor** is now `InMemoryTaskStore(InMemoryEventStore events, InMemoryPushOutbox? outbox = null)`. `new InMemoryTaskStore()` no longer compiles. Use the DI registration (`AddA2AServer`).
- **`PostgresTaskStore` constructor** now takes `StreamingOptions` second: `PostgresTaskStore(NpgsqlDataSource, StreamingOptions, PostgresStoreOptions?, TimeProvider?)`. Prefer `AddPostgresStores`.
- **Push transport:** webhooks go through the keyed `HttpMessageInvoker` `PushDeliveryService.InvokerServiceKey` (`"A2A.PushNotifications"`, the old `HttpPushNotificationSender.InvokerServiceKey` value). Register your own keyed singleton under it to replace it.
- **Retention is opt-in.** If you expected purges, add `services.AddA2ARetention()`.
- **Postgres schema:** P1 adds the tables `a2a_push_outbox` and `a2a_push_dead_letters` and some indexes (no existing column changes). They are created at startup. If you set `CreateSchemaOnStartup = false`, apply `PostgresSchema.ScriptFor("<schema>")` again before you deploy.
- **Webhook receivers:** nothing breaks. To use signatures, register push configs with scheme `webhook-signature` and verify with `PushNotificationReceiver.VerifySignature`.
- **`PushValidationResult` has new members** `InvalidSignature` and `Stale` (from `VerifySignature`). A `switch` over it that must be exhaustive needs cases for them.
- **`TaskExecutionEngine` public constructor:** the `IPushNotificationSender push` parameter is now `PushOutbox outbox`, and an optional `ExecutionCoordinator? coordinator` follows `timeProvider`. `IAgentExecutorFactory.RentAsync` takes the tenant, and a lease with a null `Executor` means no agent serves it. Prefer resolving the parts from DI (`AddA2AServer`).
- **Custom `ExecutionCoordinator`: `HeartbeatAsync` returns `ValueTask<bool>`.** Return true when the lease was held (and renew it), false when it is missing or expired (and do not renew it). On false the handler re-reads the task: it cancels the execution when the task has ended, and registers it again otherwise. A coordinator that always returns true turns this off.
- **Redriven dead letters** (`A2APushAdmin.RedriveAsync`) are sent after the newer events of the same push config that were delivered meanwhile, not in their original place.
- **`PendingApproval`** throws `ArgumentException` when `OperationId` contains a line feed. The hash format is unchanged.
- **Startup diagnostics:** `AddA2AServer` logs an error for a durable push outbox with the per-process push secret protector, and a warning for a durable task store with the in-memory execution coordinator (see "Durable stores and several instances"). Nothing throws.

### Public API changes relative to `main`

Code written against the `main` branch needs these changes:

- **Rate limiting switch moved.** `A2AServerOptions.EnableRateLimiting` is removed. Use `A2ARateLimitOptions.Enabled` (default true): `services.AddA2ARateLimiting(o => o.Enabled = false)`. The same options set the bucket (`TokenLimit`, `TokensPerPeriod`, `ReplenishmentPeriod`).
- **`A2AServerOptions.ExtendedAgentCardProvider` gets the public card.** It is now `Func<ServerCallContext, AgentCard, CancellationToken, ValueTask<AgentCard?>>`; the second argument is the tenant's completed public card. Add the parameter to your delegate.
- **`A2AAuthorizationHandler.AuthorizeAsync`** takes an `A2AOperationDescriptor` instead of an `A2AOperation`: `AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken)`. Use `operation.Kind` where you compared the `A2AOperation`; an extension operation has `Kind` = `Extension` and its registered `Name`. Every resume of a stream (Last-Event-ID, idempotent retry, duplicate follow-up) is authorized as `SubscribeToTask`.
- **`IdempotencyClaim` is public** (`A2A.Server.Idempotency`), because the public `IdempotencyCoordinator` methods (`BeginAsync`, `RecordAsync`, `AbandonAsync`) take or return it.
- **Constructors changed** (prefer the DI registrations, which pass the new arguments):
  - `RedisTaskEventBus(IConnectionMultiplexer redis, StreamingOptions streaming, RedisEventBusOptions? options = null, ILogger<RedisTaskEventBus>? logger = null)`: `StreamingOptions` is new, second.
  - `PushDeliveryService(PushOutbox, PushConfigStore, IPushSecretProtector, HttpMessageInvoker http, A2AServerOptions options, ILogger? logger = null, TimeProvider? time = null)`: an `HttpMessageInvoker` replaces `IHttpClientFactory`, and the options are passed directly, not as `IOptions<A2AServerOptions>`.
  - `A2ARetentionService(IOptions<A2ARetentionOptions>, A2AServerOptions serverOptions, TaskStore, ExecutionCoordinator, TaskExecutionEngine engine, A2ATaskAdmin, ILogger? logger = null, TimeProvider? time = null)`: `A2AServerOptions` directly, and the `TaskExecutionEngine` replaces the `A2ARequestHandler`.
- **Push metrics renamed.** The meter is `A2A.Server` (was `A2A`) and the counter is `a2a.server.push.deliveries` (was `a2a.push.deliveries`). It counts each attempt, with `a2a.outcome` = `sent`, `retried` or `dead_lettered` (was: final results, `delivered`/`failed`). `a2a.server.push.dead_letters` counts dead letters. Update dashboards and alerts.
- **Per-tenant cards: `AgentCardProvider` (`A2A.Server.Agents`) is now `TenantCardProvider`**, and `AddAgentCardProvider<T>()` is now `AddTenantCardProvider<T>()`. The members are the same. Do not confuse it with the new `A2A.Server.AgentCardProvider`, the default `IAgentCardProvider` that completes each tenant's card; register a `TenantCardProvider`, not a replacement `IAgentCardProvider`.
- **`IAgentExecutorFactory.RentAsync(string tenant, CancellationToken cancellationToken)`** rents the executor of one tenant. A lease with a null `Executor` means no agent serves the tenant. `AgentExecutorResolver` (`AddAgentExecutorResolver<T>()`) still picks the executor per tenant on top of the default factory.

## v0.3 compatibility

Opt in. Nothing changes if you do not.

Server: your v1.0 agent also answers v0.3 clients (JSON-RPC and HTTP+JSON).

```csharp
// dotnet add package RomanAmbinder.A2A.Server.V0_3Compat
app.MapA2A("/", o => o.EnableV03Compat()).RequireAuthorization();
```

Client: call a v0.3 agent. A card that offers v1.x is never downgraded.

```csharp
// dotnet add package RomanAmbinder.A2A.Client.V0_3Compat
var client = await V03CompatClientFactory.CreateAsync(new Uri("https://old-agent.example.com/"), httpClient);
```

Not supported: gRPC on v0.3, push notifications on v0.3 (server and client; answered with an error), and v0.2. Fields v0.3 cannot carry are counted, not sent. All `RomanAmbinder.A2A.*` packages must have the same version.

Tested against the official Python SDK 0.3 in both directions ([interop/v03](interop/v03/README.md)). Details: [design spec](docs/superpowers/specs/2026-11-08-v03-compat-design.md).

## Interop

Tested both ways against the official Python SDK (`a2a-sdk` 1.3.0) over JSON-RPC, HTTP+JSON, and gRPC: .NET client → Python agent and Python client → .NET agent. See [interop/](interop/README.md). CI runs it on every push.

## Build and test

```bash
dotnet build A2A.slnx
dotnet test tests/A2A.Tests
dotnet test tests/A2A.Stores.Tests   # store contract tests; the Postgres/Redis ones need Docker and skip without it
A2A_REQUIRE_DOCKER=1 dotnet test tests/A2A.Stores.Tests   # fail instead of skipping (CI does this)
```

Run the samples:

```bash
dotnet run --project samples/EchoAgent -- --EchoAgent:ApiKey=secret
dotnet run --project samples/EchoClient -- http://localhost:5100/ "hello" secret
# Two instances + Postgres + Redis: see samples/MultiInstance/README.md
```

`docs/a2a.proto` is the normative proto this SDK was built against.

## Releasing

Push a version tag. The `Release` workflow builds, tests, packs all `src/` projects (with SourceLink and `.snupkg` symbols), pushes them to nuget.org, and creates a GitHub release. A tag with a suffix (`v0.2.0-preview.1`) publishes a prerelease.

```bash
git tag v0.1.0
git push origin v0.1.0
```

One-time setup: add a nuget.org API key as the repository secret `NUGET_API_KEY`, scoped to push `RomanAmbinder.A2A.*`.
