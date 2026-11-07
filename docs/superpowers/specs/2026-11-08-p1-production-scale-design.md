# P1 design: production scale for the A2A .NET SDK

Date: 2026-11-08. Status: draft for review.
Builds on: P0 (`docs/superpowers/specs/2026-11-07-p0-security-correctness-design.md`, rulings in `docs/superpowers/plans/2026-11-07-p0-rulings.md`), now on `main`.
Source of items: `docs/2026-11-07-a2a-gap-analysis.md` Part 3, P1 items 11–18, plus P0 items deferred to P1.

## 1. Intent

P0 made a single process safe. P1 makes the SDK deployable for real: state survives restarts, several server instances can serve the same agent, webhooks are delivered reliably and verifiably, one host can serve many agents, long-waiting tasks don't pile up forever, and operators can see what is happening. The official a2a-dotnet SDK leaves all of this to the host application (gap analysis D1, D3, D7–D9, D19, D31, S11, S32). This is the biggest practical differentiator.

## 2. Goals

1. A host can swap every in-memory store for a durable one with one DI call, and the full P0 test suite passes against it.
2. Two or more server instances behind a load balancer serve the same agent correctly: a subscriber on instance B sees events produced on instance A, in order, without gaps; CancelTask on B cancels an execution running on A; idempotent retries landing on another instance return the same result.
3. Push notifications survive restarts, are retried with backoff, land in a dead-letter list after the last attempt, and carry a signature a receiver can verify.
4. One host can serve many agents (per tenant or per route), each with its own card, extended card and executor.
5. Tasks waiting for input or authorization don't wait forever; an approval answers exactly the action it was asked for; cancel has defined semantics when it can't finish at once.
6. Operators get distributed traces and metrics with standard names.
7. Finished tasks and their event logs, push configs, idempotency entries and contexts are purged after a configurable retention.

## 3. Non-goals

- New wire features beyond A2A v1.0 (all additions stay ignorable metadata/headers, as in P0).
- Cosmos DB, DynamoDB, Azure Service Bus or Kafka providers (P2; the abstractions must allow them).
- gRPC stream resume (stays a documented limit).
- A management UI or dashboard.
- Agent identity standards (DID/SPIFFE), registries, extensions packages (P2).

## 4. Anti-goals

- Weakening any P0 guarantee to make a durable or multi-instance provider easier (owner scoping, version checks, in-order delivery, fail-closed auth).
- A third-party runtime dependency in the core packages (`A2A.Protocol`, `A2A.Client`, `A2A.Server`, `A2A.Server.AspNetCore`). Providers live in their own packages.
- A provider that silently degrades to process-local behaviour (e.g. a "Redis" bus that only works on one node).
- Tests for a provider that only run on the author's machine: every provider test runs in CI.

## 5. Constraints

- Same as P0: net8.0 + net10.0, warnings as errors, `IsAotCompatible` on core packages (providers may opt out only where their own dependency is not trim-safe, documented), source-generated JSON, NuGet package IDs `RomanAmbinder.*`.
- New packages: `A2A.Server.Storage.EntityFrameworkCore` (depends on Microsoft.EntityFrameworkCore ≥ 8, relational; provider-agnostic model), `A2A.Server.Redis` (StackExchange.Redis ≥ 2.8; stores + bus + coordination), `A2A.Server.OpenTelemetry` is NOT needed — tracing uses `System.Diagnostics` only (BCL), so it lives in core.
- Tests: EF Core provider tests run against SQLite (in-memory, file-less) locally and in CI; an additional CI job runs them against PostgreSQL (service container). Redis tests run against a Redis service container in CI and locally when `A2A_TEST_REDIS` is set (they are skipped with an explicit skip reason otherwise, never silently passed).
- A shared contract test suite (`StoreContractTests<TFactory>`, `BusContractTests<TFactory>`) runs unchanged against every provider, in-memory included.

## 6. Components

### 6.1 Store contract additions (core)

P0 store contracts gain what retention, durability and multi-instance need:

- `TaskStore`: `DeleteAsync(TaskKey)`, `ListExpiredAsync(DateTimeOffset terminalBefore, int max)` returning keys of terminal tasks whose last update is older than the cut-off, `DeleteContextAsync(tenant, contextId)` and `ListOrphanContextsAsync(...)` (contexts with no tasks and older than the cut-off).
- **Atomic commit (new, central):** `TaskStore.CommitAsync(TaskCommit)` performs, as one atomic unit: the task compare-and-set (expected version → version+1), the event append with its sequence allocated inside the same unit, and zero or more push-outbox entries. It returns the stored record and the event's sequence, or throws `ConcurrencyException` with nothing written. The handler's Apply → Update → Append → Enqueue path becomes Apply → `CommitAsync` → Publish. Consequences: the event log is always in apply order across instances (log order = CAS order), a crash can't leave an applied event out of the log or the outbox, and `IPushNotificationSender.Enqueue` is replaced by outbox entries in the commit (the in-memory outbox keeps the non-blocking behaviour). `EventStore` stays the read side (`ReadAfterAsync`, `LastSequenceAsync`, `OldestRetainedAsync`) plus `TrimAsync(TaskKey, keepLast)` and `DeleteAsync(TaskKey)` for durable stores (the in-memory store already trims by count). In-memory implements `CommitAsync` under its existing lock.
- `PushConfigStore`: `DeleteAllAsync(TaskKey)`; `SaveIfUnderLimitAsync(record, limit)` so the per-task cap is atomic (P0 deferred race).
- `IdempotencyStore`: `InProgress` across instances no longer carries a process-local `Task`. Callers wait through `WaitForOutcomeAsync(scope, ct)` (provider: subscription or polling every 250 ms). An in-progress claim has a lease equal to the execution lease (30 s, renewed by the heartbeat); a claim whose lease expired without an outcome counts as abandoned, so the next `TryBeginAsync` returns `New`. `DeleteExpiredAsync(now)`; per-principal entry cap (P0 deferred LRU flush).
- New: `PushOutbox` (6.4) and `ExecutionCoordinator` (6.3).

All additions are abstract members with in-memory implementations; the contract tests cover them.

### 6.2 Durable stores

- **EF Core** (`AddA2AEntityFrameworkStores<TDbContext>()` where the host's DbContext calls `modelBuilder.AddA2AModel()`): tables for tasks (tenant, owner, task id, version, JSON payload, timestamps, terminal flag), contexts, events (tenant, owner, task id, sequence, JSON), push configs (secrets stored protected, as in P0), idempotency entries, outbox. Optimistic concurrency via the version column (`UPDATE ... WHERE version = @expected`). Sequence allocation in the same transaction as the insert (`MAX(sequence)+1` under the task row lock, or a per-task counter row). Migrations: the package ships the model; hosts generate migrations (documented, with a sample).
- **Redis** (`AddA2ARedis(options)`): tasks as hashes with a version field updated by a Lua compare-and-set script; events in a per-task stream or sorted set with an atomic `INCR`+write script; push configs, idempotency (with `SET NX` + TTL), contexts, card pins. Keys prefixed by a configurable namespace and include tenant and owner.
- Owner scoping is part of every key/primary key, exactly as `TaskKey` in P0. No provider API fetches by task id alone.
- Durable providers require a shared `IPushSecretProtector` (e.g. `AddA2ADataProtectionPushSecrets()` with a shared key ring): `AddA2AEntityFrameworkStores`/`AddA2ARedis` fail at startup if the per-process ephemeral protector is still registered, since another instance could not decrypt secrets it saved. Card pins are client-side and stay out of the server providers.

### 6.3 Multi-instance coordination

- `TaskEventBus` becomes an abstract class with an async `SubscribeAsync(TaskKey)` that returns only once the subscription is active (Redis SUBSCRIBE is async, and P0 correctness needs subscribe-before-read). `Publish`/`Complete` stay non-blocking: providers queue them to a per-instance ordered background publisher. Keys are `TaskKey`, not the internal composite string. Redis implementation: pub/sub, one channel per tenant+owner+task. Instances publish after `CommitAsync`. Live subscribers already de-duplicate and gap-fill from the event store (P0 T12); with the atomic commit (6.1) the log is in apply order, so cross-instance delivery is gap-free and in order.
- `ExecutionCoordinator` (new, abstract; in-memory and Redis implementations):
  - `RegisterRunningAsync(TaskKey, executionId)` / `UnregisterAsync` with a lease (TTL 30 s, heartbeat every 10 s) so "is any execution running for this task" is answerable cluster-wide. Blocking waits (blocking send, idempotent replays) no longer await process-local `Task`s: they wait on the bus for status events and re-check `IsRunningAsync` every second.
  - **Instance death:** when an execution's lease expires without unregistering, the retention service (6.8) commits FAILED with status text "The agent stopped unexpectedly." (normal commit path, so subscribers and webhooks see it). Executions are never re-run automatically.
  - `RequestCancelAsync(TaskKey)`: broadcasts a cancel signal; the instance running the execution cancels it. CancelTask waits for the CANCELED status via the bus, with the existing grace period.
- Per-task mutual exclusion stays optimistic across instances (version CAS + retry, as P0 designed); the per-process lock remains an optimisation.
- Stream admission limits stay per instance (documented).

### 6.4 Durable push outbox and signed webhooks

- `PushOutbox` (abstract; in-memory, EF Core, Redis): `EnqueueAsync(delivery)` is called inside the per-task lock after Append (non-blocking: a single store write); a `PushDeliveryService` (hosted service) leases due deliveries, sends them, and records the result.
- Retry: exponential backoff with jitter (initial 1 s, max 5 min, max 10 attempts — options); delivery order per task+config is preserved (a delivery is not sent until the previous one for the same task+config succeeded or was dead-lettered).
- Dead letter: after the last attempt the delivery moves to a dead-letter list, queryable and re-drivable through an admin API (`A2APushAdmin`), with a counter metric.
- Signature: Standard Webhooks headers on signed deliveries: `webhook-id` (= the existing `X-A2A-Delivery-Id`), `webhook-timestamp` (unix seconds), `webhook-signature: v1,<base64(HMAC-SHA256(secret, id + "." + timestamp + "." + body))>`. The signing secret is NOT the token (the token travels in plain on every delivery). The client supplies it through the existing v1.0 field `authentication` with `scheme = "webhook-signature"` and `credentials = "whsec_<base64>"` (≥ 16 bytes). That scheme is never sent as an `Authorization` header; it is stored protected like other secrets and redacted on read. Without it, deliveries are unsigned. The token header and other P0 headers stay unchanged for compatibility.
- `PushNotificationReceiver` gains `VerifySignature(headers, body, key, tolerance = 5 min)` with constant-time compare and timestamp tolerance; replay protection through its existing delivery-id memory.
- The P0 SSRF guard runs for every attempt (unchanged).

### 6.5 Multi-agent hosting

- `AgentCardProvider` (abstract): `GetCardAsync(AgentRequestScope)` and `GetExtendedCardAsync(ServerCallContext)`, where `AgentRequestScope` carries tenant and route values. Default: the single configured card (P0 behaviour).
- `AgentExecutorResolver` (abstract): resolves the `IAgentExecutor` for a call from tenant/route. Default: the single registered executor.
- **Rule: one agent = one tenant.** A route-based host maps its agent route segment to the tenant through `ITenantResolver`. `TaskKey` and every store key stay (tenant, owner, task), so agents can never see each other's tasks, and background paths (retention, cross-instance cancel, outbox) resolve the executor and card from the tenant alone.
- `MapA2A("/{tenant}/...")` and the `/{tenant}` sibling routes already exist (P0). New: a well-known card endpoint per tenant (`/{tenant}/.well-known/agent-card.json`) served by the provider, and card signing per agent (the provider may return a signing key). The existing `A2AServerOptions.ExtendedAgentCardProvider` becomes an adapter over `AgentCardProvider`. Every capability check (streaming, push, extensions, extended card) uses the card resolved for the call's tenant, not `_options.AgentCard`.
- Tests: two tenants with different cards and executors on one host, over all three bindings; a tenant with no agent → 404 on its card and TaskNotFound-style refusal on operations.

### 6.6 Task lifecycle hardening

- **Interrupted-state TTL**: `A2AServerOptions.InterruptedTimeout` (default 24 h, null disables). A task in INPUT_REQUIRED or AUTH_REQUIRED longer than that is moved to CANCELED with status text "The task waited too long for input." by the retention service; the transition goes through the normal Apply/Update/Append/Publish path so subscribers and webhooks see it.
- **Approval binding** (S18, loopjacking): when an executor asks for authorization or confirmation it can attach a `PendingApproval` (operation id + SHA-256 of a canonical description of the exact action). The status message carries the hash in its metadata under `a2a.approval.hash`. A client that approves echoes it in the follow-up message's metadata under the same key. Handler rule: a follow-up carrying a hash that does not match the pending one → InvalidParams; a follow-up without a hash is accepted (plain v1.0 clients keep working) but reaches the executor with `AgentRequestContext.Approval = NotApproved`. `TaskUpdater`/context expose `Approval` ∈ {None (nothing pending), Approved (hash matched), NotApproved}; the SDK's helper `RequireApprovalAsync` re-asks when not approved. Executors must act only on `Approved`.
- **Cancel-requested semantics**: only when an execution is running (anywhere in the cluster) and the task has not reached CANCELED within the grace period, CancelTask returns the current task with `metadata["a2a.cancelRequested"] = true` on the response (not persisted). The execution's later CANCELED arrives through the normal stream. TaskNotCancelable stays for terminal tasks and for an executor whose `CancelAsync` ends without cancelling while nothing is running (agent refusal).

### 6.7 Observability

- `ActivitySource("A2A")`: server spans per operation (`a2a.server {method}`), client spans per call (`a2a.client {method}`), push delivery spans; attributes `rpc.system=a2a`, `rpc.method`, `a2a.task.id`, `a2a.context.id`, `a2a.tenant` (opt-in, may be sensitive), `a2a.binding`, error type. No message content in spans.
- Trace context propagation: W3C `traceparent`/`tracestate` over HTTP and gRPC (ASP.NET Core and HttpClient do this when an ActivitySource listener is active); additionally copied into the SendMessage request-level metadata (`SendMessageRequest.metadata`, which `RequestHasher` does not hash, so idempotent retries still match) and into push-payload event metadata, both under `traceparent`, so asynchronous paths keep the trace. Never into message metadata (it is hashed and stored in history).
- `Meter("A2A")`: `a2a.server.requests` (counter, by method/outcome), `a2a.server.request.duration` (histogram), `a2a.server.active_streams` (up-down counter), `a2a.push.deliveries` (by outcome), `a2a.push.dead_letters`, plus the P0 counters already defined. Names follow OpenTelemetry RPC semantic conventions where they fit; GenAI conventions are not applicable to transport-level spans and are left to agent code.
- A sample shows wiring to OpenTelemetry in a host (sample only; no dependency in the libraries).

### 6.8 Retention

- `A2ARetentionOptions`: `TaskRetention` (default 7 days: terminal tasks with their event logs, push configs and outbox entries are deleted after this), `OrphanContextRetention` (default 7 days), `IdempotencyRetention` (P0's 24 h), sweep interval (default 5 min), batch size. The existing `StreamingOptions.TerminalRetention` (10 min) and `ReplayBufferSize` (1000) keep their meaning: the replay window for resume. The in-memory event store keeps evicting through `MarkTerminalAsync`; durable stores keep the full log until `TaskRetention` deletes it, but replay still serves only the last `ReplayBufferSize` events (older cursors get a snapshot, as in P0).
- `A2ARetentionService` (hosted service): purges expired terminal tasks with their events, push configs and outbox entries; expired idempotency entries; orphan contexts; applies the interrupted-state TTL (6.6). Multi-instance safe (each delete is idempotent; Redis/EF use a short lease so only one instance sweeps at a time).
- Admin API (`A2ATaskAdmin`, not exposed on the wire): `DeleteTaskAsync(TaskKey)`.

### 6.9 P0 items carried into P1

- Remove the public `WithA2AStartupCheck` helper (make it internal or delete) before the first NuGet release.
- `ResumableStream`: treat a snapshot whose sequence is below the client's cursor as a server reset (re-baseline instead of dropping events).
- Follow-up double-append after an abandoned idempotency claim (any failure after the history commit): the follow-up append becomes idempotent by `messageId` — if the task history already contains that message id, the append is skipped and the original outcome is used.
- Anonymous per-IP limits: group IPv6 by /64.
- gRPC: add the tenant-resolver mismatch test.

## 7. Package and API summary

```csharp
services.AddA2AServer(o => { o.InterruptedTimeout = TimeSpan.FromHours(24); })
        .AddAgentCardProvider<MyCards>()          // optional, multi-agent
        .AddAgentExecutorResolver<MyAgents>()     // optional, multi-agent
        .AddA2AEntityFrameworkStores<AppDb>()     // or .AddA2ARedis(r => r.Configuration = "...")
        .AddA2ARetention(r => r.TerminalRetention = TimeSpan.FromDays(7))
        .AddA2ADurablePush();                     // outbox + delivery service
```

## 8. Order of work

1. Store contract additions + contract test suite (6.1) — everything else builds on it.
2. Then in parallel: EF Core provider (6.2), Redis provider (6.2 + 6.3 bus/coordinator), multi-agent hosting (6.5), observability (6.7).
3. Outbox + signatures (6.4) after the providers' store parts exist.
4. Lifecycle hardening (6.6) and retention (6.8) after 6.1.
5. P0 carry-overs (6.9) anywhere.

## 9. Left to the builder

Table and key layouts, Lua script details, EF configuration style, internal class layout, exact log messages, metric bucket boundaries, sample structure.

## 10. Calls I made (not discussed with the user)

1. Providers: EF Core (relational, provider-agnostic) and Redis now; Cosmos/DynamoDB/Service Bus later.
2. One Redis package covers stores, bus and coordinator.
3. Tracing/metrics use BCL `System.Diagnostics` only; no OpenTelemetry package dependency.
4. Provider tests: SQLite locally + PostgreSQL and Redis service containers in CI; Redis tests skip with an explicit reason when no Redis is configured locally.
5. Webhook signatures follow Standard Webhooks, keyed by the push config token (≥ 16 bytes), unsigned otherwise.
6. Cross-instance per-task exclusion stays optimistic (version CAS), not a distributed lock.
7. Interrupted-state TTL default 24 h → CANCELED; terminal retention default 7 days.
8. Cancel that doesn't finish in the grace period returns the WORKING task flagged `a2a.cancelRequested` instead of an error.
9. Client resilience (gap item 16) is already done in P0 and is not repeated.
10. One atomic `TaskStore.CommitAsync` (task CAS + event append + outbox entries) instead of separate writes; sequence order = apply order across instances.
11. One agent = one tenant; no agent id in `TaskKey`.
12. Instance death: execution lease 30 s / heartbeat 10 s; expired lease → task FAILED; never auto re-run; idempotency claims share the lease.
13. Approval hash in message metadata `a2a.approval.hash`; missing hash accepted as NotApproved, mismatched hash rejected.
14. Webhook signing secret supplied via `authentication` scheme `webhook-signature` (whsec_…), never sent on the wire; token unchanged.
15. `StreamingOptions.TerminalRetention`/`ReplayBufferSize` remain the resume window; new `TaskRetention` governs deletion.
16. Cancel-requested flag only on the response and only while an execution runs somewhere.
17. `traceparent` in request-level metadata (not hashed) and push event metadata, never message metadata.
18. `TaskEventBus` becomes abstract with async `SubscribeAsync(TaskKey)`; non-blocking ordered publish.
19. Durable providers refuse to start with the ephemeral push-secret protector.
20. Follow-up append de-duplicated by messageId.
