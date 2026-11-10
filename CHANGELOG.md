# Changelog

All notable changes to this project are listed here. Versions follow [Semantic Versioning](https://semver.org/); see the [versioning guide](docs/site/guides/versioning.md) for what is covered and what is experimental.

## Unreleased

### Protocol and bindings

- Validation errors of request fields carry a `google.rpc.BadRequest` detail with the field and the reason (spec v1.0.1
  §9.5, §11.6), after the `ErrorInfo`, on all three bindings. The error message is unchanged. New
  `A2AException.InvalidField(field, description)` builds one; `A2AErrors.BadRequest` and `A2AErrors.BadRequestType`
  build the detail entry. The gRPC client reads `BadRequest` back into `A2AException.Details`.
- Each error detail object must carry an `@type` (spec v1.0.1 §3.3.2). An entry of `A2AException.Details` that is not an
  object with a non-empty `@type` is no longer sent. New `A2AErrors.HasType`.

### Documentation

- Spec decisions: the push delivery defaults said "10 attempts". The default is no limit on the count (`MaxAttempts`
  null); a delivery still failing 24 hours after it was queued is dead-lettered (`MaxDeliveryAge`).

### Performance

- Streaming many artifact chunks no longer copies the whole artifact for every chunk. When an agent sends chunks faster
  than they are stored, the server applies the waiting chunks to one copy of the task and stores them together. Each event
  keeps its own sequence number, so streams, resubscribe and push see no difference. A 1,000-chunk stream now allocates
  about 5.5 MB instead of 18.5 MB.
- New optional store interface `IBatchTaskCommit` (`TaskBatchCommit`, `BatchedEvent`, `BatchCommitResult`). The in-memory and
  Postgres task stores implement it. A store without it keeps working as before, one commit per event.
- New virtual `EventStore.ReadRangeAsync` reads only the events a stream is missing. The in-memory and Postgres event stores
  read just that range instead of every retained event. The in-memory event log is now a ring buffer.
- The server no longer reads the task back from the store before every event of a run, and an idle stream no longer
  creates a timer for every event.
- SSE frames are written straight into the response pipe. A JSON-RPC stream builds the start of its response envelope once
  (new `JsonRpcWriter.SuccessPrefix`) and copies each event's stored bytes after it. No envelope, frame or id string is
  made per event, and a stream has one keep-alive timer instead of one per event. Unary JSON responses are no longer
  copied into a new array. A 1,000-chunk stream now allocates about 4.4 MB.
- JSON reading and writing allocate less: union members, part members and enum names are matched on the raw bytes, and
  timestamps and enum names are written without a temporary string.
- The client reads stream events from their UTF-8 bytes: no string, no re-encoding and no `JsonDocument` per event, for
  JSON-RPC and HTTP+JSON. Typed JSON-RPC calls bind the result straight from the response body. A 1,000-chunk stream (server
  and client together) now allocates about 2.7 MB, down from 18.5 MB, and runs in about a third of the time.
- `A2AClientFactory.CreateAsync` with credential providers and no handler shares one connection pool instead of making a new
  one per call (which was never disposed). That pool, and the primary handler of `AddA2AClient`, recycle connections every
  2 minutes, so DNS changes are seen.
- A cached Agent Card is checked again on each `ResolveAsync` (as before) without parsing its text again.
- gRPC: stream events and SendMessage responses are mapped between the model and protobuf field by field instead of through
  ProtoJSON: about 10x less CPU and 15x less memory per message one way, 40x less CPU the other. Anything uncommon, and every
  error, still takes the ProtoJSON path, and tests check on a random corpus that both give the same result. The ProtoJSON
  path itself reuses its buffers.
- gRPC: clients made through `UseGrpc` / `A2AGrpcClientFactory` share one channel per address and channel options instead of
  opening a channel per client that was never disposed.
- `A2AProtocol.IsVersion11OrLater` parses the version in place instead of splitting it into new strings. The server calls it
  for every stream event since A2A 1.1, so this removes about 190 bytes per event.
- Agent Card verification is about 2.5x faster and allocates about 40% less: imported public keys are reused per trusted key
  (importing cost more than checking the signature), the signed bytes and the case-variant check come from one prune of
  the card instead of two, paths of pruned members are built only when reported, and base64url is encoded in place. The
  canonical bytes are unchanged. JCS sorts object members without LINQ, which also helps the idempotency hash of every
  SendMessage.

## 0.1.0

First public release of the .NET SDK for the Agent2Agent (A2A) protocol v1.0. Targets .NET 10. `A2A.Protocol` and `A2A.Client` also
target netstandard2.0. .NET 8 is not a target: its support ends on 10 November 2026.

### Postgres schema migrations

- `A2A.Server.Postgres` keeps its schema with numbered migrations, applied at startup in one transaction under an advisory lock. New: `PostgresSchema.ScriptFor(schema, fromVersion)` and `PostgresSchema.VerifySchemaAsync`. `PostgresSchema.Version` is now a property. With `CreateSchemaOnStartup = false` the app refuses to start when the schema version differs from the package.

### Packages

- 20 packages on NuGet, all with the ID prefix `RomanAmbinder.`, so they never clash with the official `A2A` packages.
  Assemblies and namespaces are `A2A.*`.
- Client: `A2A.Protocol`, `A2A.Security.Http`, `A2A.Client`, `A2A.Client.DependencyInjection`, `A2A.Client.Grpc`,
  `A2A.Client.Postgres`, `A2A.Client.V0_3Compat`.
- Server: `A2A.Server`, `A2A.Server.AspNetCore`, `A2A.Server.Grpc`, `A2A.Server.Postgres`, `A2A.Server.Redis`,
  `A2A.Server.TypedSkills`, `A2A.Server.Storage.S3`, `A2A.Server.Storage.AzureBlob`, `A2A.Server.V0_3Compat`.
- Shared: `A2A.Grpc` (gRPC types) and `A2A.V0_3Compat` (v0.3 codecs).
- Microsoft.Extensions.AI: `A2A.Extensions.AI`.
- Tool: `A2A.Cli` (the `a2a` command; install with `dotnet tool install -g`).
- Testing: `A2A.TestKit`, for agent authors. An in-process agent host, a scripted fake peer agent, assertions for task states
  and event order, a fake clock (`TestClock`) and a push receiver. It works with any test framework. It does not claim AOT
  compatibility and has no gRPC.
- Model Context Protocol: `A2A.Mcp`.
- Every package has a README, the Apache-2.0 license, SourceLink and a symbol package. The libraries are AOT compatible.
  (#4, #13)

### Command line tool

- `RomanAmbinder.A2A.Cli` is a dotnet tool with the command `a2a`: `card`, `send`, `stream`, `subscribe`, `get`, `list`,
  `cancel` and `download`. It uses JSON-RPC or HTTP+JSON, has no extra dependencies, and is AOT and trim friendly.
- Credentials come from options or from `A2A_TOKEN`, `A2A_API_KEY`, `A2A_CLIENT_ID` and `A2A_CLIENT_SECRET`. Secrets are
  never printed. Agent text is cleaned before it reaches the terminal. Exit codes are documented in the
  [guide](docs/site/guides/cli.md).
- `a2a download` checks the size and `sha256` of each file, writes to a temporary file first, and never lets an agent-chosen file
  name leave the output folder.

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
  It detects corruption, not forgery. Experimental. (#32, #35)
- Accepts `history_length` as well as `historyLength`, as ProtoJSON requires. (#8)
- REST stream errors are sent as SSE frames named `error`. All clients skip stream event types they do not know instead of
  ending the stream. (#8)
- A2A 1.1 (release candidate) data model, experimental (`A2AEXP005`): `docs/a2a.proto` is the `v1.1.0-rc` proto, and the
  model has `Task.generation` and `timeline`, `TimelineEntry`, artifact `startGeneration`, `endGeneration` and `role`,
  `generation` on status and artifact events, the `messageUpdate` and `error` stream events, `StreamRequest`, the new request
  fields, `AgentCapabilities.userArtifacts`, the push config `protocolVersion`, and the error `TaskGenerationMismatch`
  (-32010, HTTP 409, gRPC `ABORTED`). 64-bit values are written as ProtoJSON strings and read as strings or numbers.
- A2A 1.1 on the server, opt-in with `A2AServerOptions.EnableProtocolV11()` (experimental): a caller that sends
  `A2A-Version: 1.1` gets the task `generation` (one per change; it equals the event sequence), the `timeline` (client
  messages and agent status entries), artifact `startGeneration` / `endGeneration`, `generation` on every stream event, a
  `messageUpdate` event for each follow-up message, and send streams that stay open on `INPUT_REQUIRED` and
  `AUTH_REQUIRED` until the task ends. `timelineLength` works like `historyLength` (cap: `A2ALimits.MaxTimelineLengthReturned`,
  100). A card completed from the mapped bindings lists the 1.1 interfaces first. 1.0 and v0.3 callers get the 1.0 shape
  and no message events, and webhooks stay 1.0.
- A2A 1.1 for 1.1 callers: `configuration.ifGenerationMatch` refuses a follow-up when the task's generation differs
  (`TaskGenerationMismatch`, -32010, HTTP 409, gRPC `ABORTED`, with `expectedGeneration` and `currentGeneration`); GetTask
  with `currentGeneration` (REST `?currentGeneration=`) waits until the task changes, up to
  `A2AServerOptions.LongPollTimeout` (30 s, at most 5 min), holding a stream slot; `SubscribeToTask` with
  `lastSeenGeneration` (REST `?lastSeenGeneration=`) resumes after it without a snapshot; a push config created by a 1.1
  caller has `protocolVersion` 1.1 (or the `1.0` it asks for), and its webhook gets `A2A-Version: 1.1`, the `generation` of
  each event and the message updates. Configs of 1.0 callers stay 1.0.
- A2A 1.1 client-written artifacts (`AgentCapabilities.UserArtifacts`): a 1.1 caller may send `artifactUpdates` with its
  message, or without a message to an existing task. They are stored after the message, in list order, each one change,
  with `role` user; the client's `generation` is ignored. The agent reads them in `AgentRequestContext.UserArtifacts`. The
  agent cannot change a client's artifact and the client cannot change an agent's. At most
  `A2ALimits.MaxUserArtifactsPerRequest` (16) per request, each held to the message limits and input media types. Without the
  capability the request is `UnsupportedOperation`. `SendMessageRequest.Message` is now nullable for these requests.
- A2A 1.1 live exchange (`SendLiveMessage`, spec §3.1.12), experimental: gRPC bidi `SendLiveMessage`; JSON-RPC
  `SendLiveMessage` and REST `POST /message:live`, where the `Accept: text/event-stream` call is the stream and other calls
  send input to its task. The first request binds the exchange to a task (message or `subscribe`); a refused request is a
  `StreamError` event and the exchange stays open. Client: `A2AClient.OpenLiveAsync` returns an `A2ALiveConnection` on all
  three bindings. An agent that reads `AgentRequestContext.LiveInput` takes messages while it runs, in any non-terminal
  state (`A2ALimits.LiveInputCapacity`, 32). The client now reads `messageUpdate` and `error` stream events on HTTP.
- A follow-up message is now stored as an event of the task (a message update), so it takes a sequence number. 1.0
  callers do not see it, but stream ids and webhook sequences skip its number.
- The Agent Card endpoint answers with `application/a2a-agent-card+json` when the request asks for it, and with
  `application/json` otherwise. The client asks for both.
- A completed card lists a gRPC interface over TLS as `host:port` (for example `agent.example.com:443`), the form the spec
  shows and other SDKs pass to a gRPC channel. A plain-text gRPC interface keeps its `http://` URL.

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
  chain is copied to the task. Experimental. (#22, #35)
- `A2ALimits.MaxActorChainHops` (default 8) refuses a longer chain with `InvalidRequest`. It is a loop brake, not a security
  boundary. (#22)
- Token exchange (`ITokenExchanger`, `OAuthTokenExchanger`, RFC 8693 style). Agent code never gets the caller's token. (#22)

- Audit trail: an `A2AAuditSink` receives one event for each task start, follow-up, approval, answered form, cancel, artifact download,
  push config change, access denial and admin delete, redrive or dead-letter listing, on every binding. Events hold ids and
  reasons, never content. The default sink logs; `AddPostgresAuditSink` stores them in `a2a_audit_log`. Fail open by default;
  `Audit.FailClosed` writes an `Attempted` row first and refuses the request when that write fails. The admin classes have
  overloads that take an `actor`. See the audit guide.

### Skill selection

- A client names the skill it wants with `A2AClient.SelectSkill`. The agent reads `AgentRequestContext.RequestedSkillId`. It is
  an optional extension, and a hint, not authorization. A card can opt in to refusing unknown skills. Experimental. (#30, #35)

### Typed skills

- `A2A.Server.TypedSkills` (`AddTypedSkills`): declare a skill with .NET input and output types from a source-generated JSON
  context. The SDK puts JSON Schema on the card, refuses bad input with `InvalidParams` (a `google.rpc.BadRequest` detail)
  before the agent runs and before anything is stored, and sends the result as one data artifact. A plain executor can run
  next to it as the fallback. AOT safe. Experimental.
- `A2AClient.CallSkillAsync` calls a typed skill with .NET types. `TypedSkillCallException` reports a task that did not
  complete. `TypedSkillExtension.TryGetSchemas` reads the schemas from a card. Experimental.

### Skill authorization

- A host can allow a caller one skill and deny another. `A2AAuthorizationHandler.AuthorizeSkillAsync` decides (it denies by
  default), and `A2AServerOptions.SkillAuthorization` picks the mode: `Off`, `WhenNamed` or `Required`. The server checks the
  skill in `metadata["skillId"]` before the agent runs, on every binding, and an unknown skill gives the same error as a denied
  one. Open streams are checked again. `FilterExtendedCard` lists only allowed skills on the extended card.
  `AgentRequestContext.AuthorizedSkillId` and `IsSkillAllowedAsync` let an agent that picks its own skill ask the same question.

### Microsoft.Extensions.AI

- `A2A.Extensions.AI` runs any `IChatClient` as an agent (`AddChatClientAgent`), calls a remote agent as a tool
  (`AsAIFunction`) and as a chat client (`AsChatClient`). URL parts are refused unless allowed. (#21)

### MCP bridge

- `A2A.Mcp` serves the skills of a remote agent as MCP tools (`AsMcpTools`), with `get_task` and `cancel_task` for long tasks.
  It also runs MCP tools as an agent (`AddMcpToolAgent`) with an allowlist. Credentials are never forwarded, and tool
  descriptions are fixed text unless you opt in. Built on `ModelContextProtocol.Core` 2.4.0.

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
- `A2ALimits.MaxTaskBytes` (default 64 MiB) and `A2ALimits.MaxArtifactsPerTask` (default 1024) cap what a stored task can hold.
  An agent event past a cap fails the task and stops the agent. A follow-up past the byte cap is refused with
  `UnsupportedOperation` and the task stays as it was.
- A follow-up that was accepted but could not start fails its task, so the task does not wait forever. (#15)
- The client refuses a request with a missing required field before sending it, on every operation and binding, and names the
  field in the error (`InvalidParams`). No member became C# `required`.
  v0.3 clients must give a config id to get a push config; use the list operation otherwise. (#27)

### Structured elicitation

- Optional extension that lets an agent in `INPUT_REQUIRED` ask for a small typed form. `TaskUpdater.RequireInputAsync(context, form, text)` on
  the server, `UseElicitation`, `GetElicitationForm` and `AnswerElicitationAsync` on the client. All bindings. A client that does not
  activate it sees the text only.
- The server checks an answer against the form before the agent runs. A refused answer is `InvalidParams`, is not stored and uses no turn.
  Fields that look like requests for secrets are refused by default.

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
- A webhook registered late is not lost. `CreateTaskPushNotificationConfig` on a task that is terminal, `INPUT_REQUIRED` or
  `AUTH_REQUIRED` queues one delivery of the task snapshot (a `StreamResponse` with `task`) for that config, at the task's
  latest event sequence. The engine also lists a task's webhooks again after an event that leaves it final-for-now, so a
  config saved between its first listing and the commit gets that event too. No locks, so it works across instances. Both
  paths can queue the same config; the outbox keeps one (new `PushOutbox.EnqueueAsync`, idempotent on task, config and
  sequence, in the in-memory and PostgreSQL outboxes), and the delivery id is derived from that key.
  `PushNotifications.CatchUpOnRegistration` (default true) turns both off. Inline config on `SendMessage` is still the
  race-free way to register for a new task.
- `PushDelivery.MaxDeliveryAge` (default 24 hours): a delivery older than this, counted from when it was queued, is
  dead-lettered. PostgreSQL uses the database clock; the new nullable `a2a_push_outbox.queued_ticks` column is added by the
  schema script and is safe on existing rows. `PushDelivery.MaxAttempts` is now `int?` and defaults to null (no count
  limit; it caps attempts when set). Before, the default was 10 attempts.
- `PushDelivery.OnDeadLetter`: an optional callback with the task, config id, sequence and last error (never secrets),
  called when a delivery is dead-lettered. An exception it throws is logged and has no other effect.
- Custom `PushOutbox` classes must implement the new abstract `EnqueueAsync`.
- `PushNotificationReceiver.HandleAsync` checks the token and/or signature, runs your handler, and counts the delivery id as done
  only after the handler succeeds. It returns the status to answer: 200 (handled or duplicate), 503 (same id in progress,
  retryable), 500 (handler failed, the retry reaches the handler again), 401 (bad token or signature), 400 (bad body).
  `Validate` and `VerifySignature` mark the id as seen at once, so a failure after them lost the retry; their docs now say so.
- `IPushDeliveryStore` (default `InMemoryPushDeliveryStore`, 10-minute window) keeps handled ids, so they can survive a restart.
  With the in-memory store a restart can repeat a delivery, which is safe.
- `PushTaskTracker` notices lost webhooks: it calls `GetTask` when a task's deadline passes without a final state, or when
  `X-A2A-Notification-Sequence` skips a number, and reports the result to a callback. `IPushTaskStore` (default in memory), `TimeProvider` aware.
- `A2AClient.RegisterPushAsync` registers the webhook first, then calls `GetTask` once, and returns both, so a caller sees a task that
  already finished (servers of other SDKs send no catch-up delivery).

### Proactive continuation

- `TaskExecutionEngine.ContinueTaskAsync` continues an existing task with no inbound request, for an agent that implements
  `IContinuableAgentExecutor`. It needs no push registration. Events go to the event store, streams and push.
- The run is exclusive against every execution of the task on every instance. A request that cannot start returns a status
  (`TaskNotFound`, `TaskTerminal`, `InterruptedTimedOut`, `AlreadyRunning`, `NotSupported`) and changes nothing.
- The API does not check the caller. A host that exposes it must authenticate its callers.

### Client outbox

- A durable list of the messages an agent sends to peers: `A2A.Client.Outbox` (`ClientOutboxStore`, `InMemoryClientOutboxStore`, `ClientOutboxDispatcher`) and the new package
  `A2A.Client.Postgres`, which can enqueue and acknowledge in your own database transaction. Sends carry the entry id as `Idempotency-Key`; a crash
  between "call the peer" and "save" cannot create a second task on an SDK server. See "Client outbox" in the guides.

### Retention

- `AddA2ARetention` deletes old tasks and contexts, times out tasks stuck waiting for input (24 hours by default), and fails
  tasks whose instance died. It is opt-in. (#12)
- A sweep step no longer stalls behind items it has to skip. (#18)
- With an artifact object store, the sweep also deletes objects that no task refers to, and `A2ATaskAdmin.DeleteTaskAsync`
  deletes a task's objects after its row. See "Artifact storage".

### Artifact storage

- Large artifact bytes can live in an object store instead of the task row. Register an `ArtifactObjectStore` (abstract class, in
  `A2A.Server.Storage`) and set `A2AServerOptions.ArtifactStorage.PublicBaseUrl`. `InMemoryArtifactObjectStore` is for tests and
  development. With no store registered, nothing changes except the size caps.
- New packages `A2A.Server.Storage.S3` (`AddA2AS3ArtifactStore`; Amazon S3 and S3-compatible services) and
  `A2A.Server.Storage.AzureBlob` (`AddA2AAzureBlobArtifactStore`; block blobs). Uploads go in 8 MiB steps and a failed upload leaves
  no object. Download links force `Content-Disposition: attachment`. S3 needs the tagging permissions
  (`s3:PutObjectTagging`, `s3:GetObjectTagging`); Azure makes SAS links only from an account key.
- A raw part over `InlineThresholdBytes` (256 KiB) is uploaded and replaced by a `url` part with `sha256` and `sizeBytes` in its
  metadata. `TaskUpdater.AddArtifactFromStreamAsync` uploads a stream of up to `MaxObjectBytes` (5 GiB) without passing it
  through an event. Appended chunks stay inline.
- `MapA2AArtifacts()` serves `GET {pattern}/artifacts/{tenant}/{taskId}/{objectId}`. The caller needs the same credentials as
  for A2A calls, and the read is authorized as `GetTask`. Every miss is 404, also an object that is missing in `Redirect` mode.
  There are no signed links.
- `A2AArtifactDownloader` (.NET 8 and later) downloads a `url` part. It takes the caller's `HttpMessageHandler`, which must
  not follow redirects, and refuses a URL on another origin. It follows one redirect to a presigned URL without credentials,
  and checks `sizeBytes` and `sha256`.
- `TaskStore` has a new abstract member, `GetForMaintenanceAsync`, which reads a task without its owner. Custom task stores
  must implement it.
- Startup fails when a store has no link base, and warns (event 255) when a durable task store uses the in-memory object store.
  Counters: `a2a.server.artifact.objects.written`, `.bytes.written`, `.bytes.served` and `.orphans.deleted`.

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
  `a2a.extensions.v1.A2AExtensionService`, mapped with `MapA2AGrpcExtensions`. Experimental. (#13, #35)
- Custom operations are finished: a call must activate the extension of its operation (`A2A-Extensions`), or it fails with
  `ExtensionSupportRequired`; the tenant's card is checked first; `rpc.` names are refused (`A2AProtocol.Methods.IsReserved`);
  a typed `AddExtensionOperation<TRequest, TResult>`; client helpers (`InvokeExtensionOperationAsync`, on JSON-RPC, HTTP+JSON
  and gRPC); and a guide.
- gRPC errors list the activated extensions in the trailers, as the other bindings do. (#35)
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
  See the versioning guide. (#35)

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
  (#31, #35)

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
