# Changelog

All notable changes to this project are listed here. Versions follow [Semantic Versioning](https://semver.org/); see the [versioning guide](docs/site/guides/versioning.md) for what is covered and what is experimental.

## 0.1.0

First public release of the .NET SDK for the Agent2Agent (A2A) protocol v1.0. Targets .NET 8 and .NET 10.

### Packages

- 15 packages on NuGet, all with the ID prefix `RomanAmbinder.`, so they never clash with the official `A2A` packages.
  Assemblies and namespaces are `A2A.*`.
- Client: `A2A.Protocol`, `A2A.Security.Http`, `A2A.Client`, `A2A.Client.DependencyInjection`, `A2A.Client.Grpc`,
  `A2A.Client.V0_3Compat`.
- Server: `A2A.Server`, `A2A.Server.AspNetCore`, `A2A.Server.Grpc`, `A2A.Server.Postgres`, `A2A.Server.Redis`,
  `A2A.Server.V0_3Compat`.
- Shared: `A2A.Grpc` (gRPC types) and `A2A.V0_3Compat` (v0.3 codecs).
- Microsoft.Extensions.AI: `A2A.Extensions.AI`.
- Every package has a README, the Apache-2.0 license, SourceLink and a symbol package. The libraries are AOT compatible.
  (#4, #13)

### Protocol and bindings

- JSON-RPC, HTTP+JSON and gRPC bindings, for both client and server, with SSE streaming. (#3, #6, #7)
- Interop is tested in CI against the official Python `a2a-sdk`, in both directions. The official A2A conformance suites
  (a2a-tck and ACTS) run in CI too. Known failures are listed in `conformance/expected-failures.txt`. (#17)
- `DeleteTaskPushNotificationConfig` is idempotent: deleting a config that is gone succeeds (spec 3.1.10). (#17)
- A message part whose `mediaType` the card does not accept is refused with `ContentTypeNotSupported`. `Part.FromData` uses
  `application/json`, so a card that lists only `text/plain` now refuses data parts built with it. (#17)
- An artifact is sealed with `lastChunk: true`. A later `append: true` is refused, also after a restart. `append: false`
  replaces the artifact. (#32)
- Optional artifact integrity hash: `TaskUpdater.AddHashedArtifactAsync` on the producer and `ArtifactVerifier` on the client.
  It detects corruption, not forgery. Experimental. (#32, #TBD)
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
- Peer identity is the card origin plus the signing key id, never the card's `name`. `AgentRegistry` rejects duplicate names
  and warns about lookalike names. A cached card is checked again against the current trust policy. (#23)
- Credential providers are chosen by the card's security schemes: OAuth2 client credentials, API key and mutual TLS. A credential
  goes only to the origin the card came from and is never sent across a redirect. (#24)
- `OAuthTokenExchanger` and `UseBearerToken` follow the same redirect and origin rules. `OAuthTokenExchanger` no longer takes an
  `HttpClient`, and `UseBearerToken` now takes the agent URL. (#26)

### Delegation

- `metadata["actorChain"]` records who the work is for (shape of A2A#2028). It is audit data, not authorization. A well-formed
  chain is copied to the task. Experimental. (#22, #TBD)
- `A2ALimits.MaxActorChainHops` (default 8) refuses a longer chain with `InvalidRequest`. It is a loop brake, not a security
  boundary. (#22)
- Token exchange (`ITokenExchanger`, `OAuthTokenExchanger`, RFC 8693 style). Agent code never gets the caller's token. (#22)

### Skill selection

- A client names the skill it wants with `A2AClient.SelectSkill`. The agent reads `AgentRequestContext.RequestedSkillId`. It is
  an optional extension, and a hint, not authorization. A card can opt in to refusing unknown skills. Experimental. (#30, #TBD)

### Microsoft.Extensions.AI

- `A2A.Extensions.AI` runs any `IChatClient` as an agent (`AddChatClientAgent`), calls a remote agent as a tool
  (`AsAIFunction`) and as a chat client (`AsChatClient`). URL parts are refused unless allowed. (#21)

### v0.3 compatibility

- Opt-in support for A2A v0.3 peers over JSON-RPC and HTTP+JSON. Server: `EnableV03Compat()` in `MapA2A`. Client:
  `V03CompatClientFactory`. v0.3 push and gRPC are not supported. (#25)

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
  v0.3 clients must give a config id to get a push config; use the list operation otherwise. (#27)

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

### Proactive continuation

- `TaskExecutionEngine.ContinueTaskAsync` continues an existing task with no inbound request, for an agent that implements
  `IContinuableAgentExecutor`. It needs no push registration. Events go to the event store, streams and push.
- The run is exclusive against every execution of the task on every instance. A request that cannot start returns a status
  (`TaskNotFound`, `TaskTerminal`, `InterruptedTimedOut`, `AlreadyRunning`, `NotSupported`) and changes nothing.
- The API does not check the caller. A host that exposes it must authenticate its callers.

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
- Span and attribute names follow the OpenTelemetry A2A semantic conventions (status: Development). Span names lose the `a2a.`
  prefix, `rpc.method` is now `a2a.method.name`, and `a2a.context.id` is now `gen_ai.conversation.id`. (#29)

### Extending the SDK

- `IA2ARequestHandler`, used by every binding, and an operation filter pipeline (`IA2AOperationFilter`). (#13)
- Extension operations over JSON-RPC, REST and gRPC (`AddExtensionOperation`), denied unless authorized. gRPC serves them through
  `a2a.extensions.v1.A2AExtensionService`, mapped with `MapA2AGrpcExtensions`. Experimental. (#13, #TBD)
- gRPC errors list the activated extensions in the trailers, as the other bindings do. (#TBD)
- `IResumableTransport` and `StreamFrame`, so third-party transports get stream resume. (#13)
- `IAgentCardProvider` and `IPushConfigChangeListener`. (#13)
- The server core runs without a DI container. `A2A.Client` depends only on `Microsoft.Extensions.Logging.Abstractions`. (#13)

### Ease of use

- `services.AddA2A(...)` and `app.MapA2A(...)` set up a server in a few lines. `MapA2A` also maps the Agent Card. (#13)
- An Agent Card needs only `Name`, `Description` and `Version`. Its interfaces are filled in from the mapped bindings. (#13)
- `AddA2AClient` limits the response buffer to 16 MiB by default. (#15)

### Versioning

- `[Experimental]` marks APIs tied to unsettled contracts, each with a diagnostic ID: `A2AEXP001` skill selection, `A2AEXP002`
  actor chain, `A2AEXP003` artifact integrity hash, `A2AEXP004` gRPC extension service. They can change in any minor version.
  See the versioning guide. (#TBD)

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
- Guides for v0.3 compatibility, Microsoft.Extensions.AI, client credentials, peer identity, skill selection and versioning.
  (#31, #TBD)

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
