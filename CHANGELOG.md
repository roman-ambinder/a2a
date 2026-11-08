# Changelog

All notable changes to this project are listed here. Versions follow [Semantic Versioning](https://semver.org/).
Before 1.0.0, a minor version may include breaking changes.

## 0.1.0

First public release of the .NET SDK for the Agent2Agent (A2A) protocol v1.0. Targets .NET 8 and .NET 10.

### Packages

- 11 packages on NuGet, all with the ID prefix `RomanAmbinder.`, so they never clash with the official `A2A` packages.
  Assemblies and namespaces are `A2A.*`.
- Client: `A2A.Protocol`, `A2A.Security.Http`, `A2A.Client`, `A2A.Client.DependencyInjection`, `A2A.Client.Grpc`.
- Server: `A2A.Server`, `A2A.Server.AspNetCore`, `A2A.Server.Grpc`, `A2A.Server.Postgres`, `A2A.Server.Redis`.
- Shared gRPC types: `A2A.Grpc`.
- Every package has a README, the Apache-2.0 license, SourceLink and a symbol package. The libraries are AOT compatible.
  (#4, #13)

### Protocol and bindings

- JSON-RPC, HTTP+JSON and gRPC bindings, for both client and server, with SSE streaming. (#3, #6, #7)
- Interop is tested in CI against the official Python `a2a-sdk`, in both directions.
- Accepts `history_length` as well as `historyLength`, as ProtoJSON requires. (#8)
- REST stream errors are sent as SSE frames named `error`. All clients skip stream event types they do not know instead of
  ending the stream. (#8)

### Security

- Tasks, contexts, push configs, idempotency keys and event logs belong to a tenant and owner. Another caller's data looks
  like missing data (`TaskNotFound`). (#5, #7)
- An authorization hook runs on every binding. The app will not start when A2A endpoints have no authorization policy,
  unless anonymous access is turned on explicitly. (#5, #7)
- Default limits on request size, message parts, JSON depth, page size, event size and open streams. Rate limiting per
  caller, and per remote IP address for anonymous callers. (#5)
- SSRF guard for outbound HTTP, including webhooks, `jku` key sets and URLs chosen by a peer (`SafeHttp`). (#5, #8, #13)
- HTTP transports refuse a response that arrived through a redirect. (#8)
- Agent Card signing and verification (JWS, JCS).

### Streaming

- Every task has an event log with a per-task sequence. Events reach subscribers in order, with no repeats. (#5)
- Clients resume a stream with `Last-Event-ID`. (#5)
- A stream that falls behind fills the gap from the event log. If the log no longer has those events, the stream sends a
  snapshot of the task instead. Set `StreamingOptions.ReplayBufferSize` above the number of events your tasks produce if
  clients must see every event. (#5, #13)
- Limits on open streams per task and per caller, on stream duration, and a periodic re-authorization of open streams. (#5)

### Task safety

- `A2AServerOptions.MaxTaskDuration` fails a task that runs too long. `MaxTurnsPerTask` caps the turns in a multi-turn task.
  Both are off by default. (#9)
- `A2AClientOptions.OnMessageReceived` lets a client check every agent message, for example against the user's intent. (#9)
- Single-use approval binding (`PendingApproval`). A follow-up with a `messageId` already in the task's history is not run
  again. (#12)
- A follow-up that was accepted but could not start fails its task, so the task does not wait forever. (#15)
- The client refuses a request with a missing required field before sending it, on every operation and binding, and names the
  field in the error (`InvalidParams`). No member became C# `required`.

### Durable stores and running several instances

- `A2A.Server.Postgres` stores tasks, event logs, push configs, the push outbox and idempotency keys. Npgsql, no EF. (#10, #12)
- `A2A.Server.Redis` fans live events out to every instance and provides an execution coordinator, so CancelTask and
  blocking waits work across instances. (#10, #12)
- A task update, its event and its webhooks are saved together or not at all (`TaskStore.CommitAsync`). (#12)
- Postgres deadlines use the database clock, so instances whose clocks disagree still agree on what has expired. (#18)
- An execution that loses its lease before its first event is no longer cancelled by mistake. (#18)

### Push notifications

- Durable delivery through an outbox, with retries, backoff, dead letters and redrive (`A2APushAdmin`). (#12)
- Standard Webhooks signatures (`webhook-signature`) with replay protection, and `PushNotificationReceiver.VerifySignature`
  for receivers. (#12)
- Webhook requests send `A2A-Version`. (#8)

### Retention

- `AddA2ARetention` deletes old tasks and contexts, times out tasks stuck waiting for input (24 hours by default), and fails
  tasks whose instance died. It is opt-in. (#12)
- A sweep step no longer stalls behind items it has to skip. (#18)

### Hosting several agents

- One host can serve many agents, one per tenant: `AddTenantCardProvider` and `AddAgentExecutorResolver`. (#12)
- The Agent Card signature cache now evicts the least recently used card. (#18)

### Observability

- Spans for every server and client operation, agent run and webhook delivery through `ActivitySource`, plus metrics
  through `Meter`. No OpenTelemetry package is needed in the core packages. (#11, #13)
- All bindings send and read W3C `traceparent` and `tracestate`. (#11)
- Tags never contain message content, tokens, credentials, webhook URLs or exception messages. (#11)

### Extending the SDK

- `IA2ARequestHandler`, used by every binding, and an operation filter pipeline (`IA2AOperationFilter`). (#13)
- Extension operations over JSON-RPC and REST (`AddExtensionOperation`), denied unless authorized. (#13)
- `IResumableTransport` and `StreamFrame`, so third-party transports get stream resume. (#13)
- `IAgentCardProvider` and `IPushConfigChangeListener`. (#13)
- The server core runs without a DI container. `A2A.Client` depends only on `Microsoft.Extensions.Logging.Abstractions`. (#13)

### Ease of use

- `services.AddA2A(...)` and `app.MapA2A(...)` set up a server in a few lines. `MapA2A` also maps the Agent Card. (#13)
- An Agent Card needs only `Name`, `Description` and `Version`. Its interfaces are filled in from the mapped bindings. (#13)
- `AddA2AClient` limits the response buffer to 16 MiB by default. (#15)

### Performance

Short benchmark job, macOS arm64, before and after the design work in #13:

| Benchmark | Before | After |
|---|---|---|
| 1,000-event stream, allocations | 432.6 MB | 20.5 MB |
| 1,000-event stream, time | 407 ms | 9.3 ms |
| SendMessage round trip, allocations | 58.3 KB | 36.1 KB |
| JSON-RPC envelope encode | 1,923 ns, 1,640 B | 114 ns, 480 B |

Each event is encoded once, and SSE, push and replay all write the stored bytes. CI fails on an allocation regression above
10%. (#13, #15, #16)

### Documentation

- A docs site in `docs/site`, built with DocFX: task-oriented guides and a generated API reference. CI builds it, compiles
  every code sample, and checks type names and links. It is not hosted yet. (#19)

### Before you deploy

These matter on a first deployment. See "Durable stores and several instances" in the README.

- The default stores are in memory, for one instance, and are lost on restart.
- Retention is off unless you call `AddA2ARetention`. Without it, a durable task store keeps finished tasks forever and tasks
  of dead instances stay `WORKING`. Startup logs a warning for this.
- With a durable push outbox, push secrets need `AddDataProtection()` with a persisted, shared key ring and
  `AddA2ADataProtectionPushSecrets()`. Otherwise startup logs an error, and webhooks with secrets fail after a restart.
- With a durable task store and several instances, use the Redis execution coordinator (`AddRedisEventBus` registers it).
  Startup logs a warning if the coordinator is in memory.
- The Postgres stores create their tables at startup. If you set `CreateSchemaOnStartup = false`, apply
  `PostgresSchema.ScriptFor("<schema>")` before you deploy.
- To verify webhooks, register push configs with the scheme `webhook-signature` and call
  `PushNotificationReceiver.VerifySignature`.
