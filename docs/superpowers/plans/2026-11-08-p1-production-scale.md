# P1 Production Scale Implementation Plan (re-scoped)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the production gaps `main` still has after PRs #9/#10: atomic task commits, a durable signed webhook outbox, a cross-instance execution coordinator, retention, multi-agent hosting, lifecycle hardening, and the P0 carry-overs.

**Architecture:** Core contracts in `A2A.Server` gain an atomic commit, a push outbox, an execution coordinator and retention members, each with an in-memory implementation; `A2A.Server.Postgres` implements the store-side members and `A2A.Server.Redis` implements the coordinator. Contract suites in `tests/A2A.Stores.Tests` run against in-memory and the Docker-backed providers.

**Tech Stack:** .NET 8/10, ASP.NET Core, System.Text.Json source generation, Npgsql (existing), StackExchange.Redis (existing), Testcontainers (existing tests), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-p1-production-scale-design.md` — §0 (re-scope) overrides the rest; §10 calls are binding except where §0 drops a feature.

## Global Constraints

- Libraries target `net8.0;net10.0`; `TreatWarningsAsErrors` on; `IsAotCompatible` on core packages; source-generated JSON (`A2AJsonContext`; register new runtime-serialized types). Public members carry XML docs (main now treats CS1591 as an error if the sdk-design-principles work has landed — check `Directory.Build.props`).
- No new third-party runtime dependency in core packages. Postgres/Redis packages keep their existing dependencies; no new packages without a ruling.
- Every P0 guarantee holds for every implementation: owner-scoped keys, foreign data looks missing, version CAS, in-order gap-free delivery, fail-closed auth, wire compatibility (additions are ignorable metadata/headers only).
- One agent = one tenant; `TaskKey` stays `(Tenant, Owner, TaskId)`.
- Provider tests use `tests/A2A.Stores.Tests` fixtures (Testcontainers). Locally Docker 29.x is available, so they must actually run, not skip; report the counts.
- Never delete a test to pass; rewrite only when this spec changes the asserted behaviour, and say so in the commit message.
- Verification: `dotnet build A2A.slnx -warnaserror` → `0 Error(s)`; `dotnet test A2A.slnx -f net10.0` → `Failed: 0` (all test projects, including `A2A.Stores.Tests` with Docker).
- Commits end with the committing model's own `Co-Authored-By` line. Never push. Never use bare `git stash`. Never commit `.superpowers/`.

## Review Focus

1. **Instance killed mid-execution.** A person expects the task to become FAILED with "The agent stopped unexpectedly." within lease TTL + one sweep, seen by subscribers on another instance. Test in Task 6 (in-memory, fake time) and Task 5 (Redis multi-instance).
2. **Two instances write one task at once.** A person expects log order = apply order with no gaps on both instances. Test in Task 1 (Postgres contract: concurrent committers) and Task 5 (multi-instance follow-up during execution).
3. **Store fails mid-commit.** A person expects nothing partial: no event without its task change, no outbox entry without its event; the request fails with retryable Internal. Test in Task 1 (in-memory failing wrapper; Postgres by killing the transaction).
4. **Clock skew between instances.** A person expects leases not to expire early: the Redis coordinator uses Redis `TIME`, Postgres uses `now()`/`clock_timestamp()`. Test in Task 5.
5. **Replayed signed webhook.** Second identical delivery → Duplicate; older than 5 min → Stale; no exception. Test in Task 3.

---

### Task 1: Atomic commit (in-memory + Postgres)

**Files:**
- Create: `src/A2A.Server/Storage/TaskCommit.cs`
- Modify: `src/A2A.Server/Storage/TaskStore.cs`, `InMemoryTaskStore.cs` (constructor takes `InMemoryEventStore`), `src/A2A.Server/A2ARequestHandler.cs` (`ApplyAndStoreAsync`, create path in `ConsumeAsync`), `src/A2A.Server/ServiceCollectionExtensions.cs`, `src/A2A.Server.Postgres/PostgresTaskStore.cs` (+ `Sql.cs`), `tests/A2A.Stores.Tests/TaskStoreContract.cs`
- Test: `tests/A2A.Tests/CommitTests.cs`, contract additions

**Interfaces:**
```csharp
public sealed record TaskCommit(TaskRecord Record, long ExpectedVersion, StreamResponse Event, IReadOnlyList<PushDelivery> Deliveries)
{ public bool IsCreate => ExpectedVersion == 0; }      // create: Record.Version must be 0
public sealed record CommitResult(TaskRecord Record, long Sequence);
public sealed record PushDelivery(TaskKey Task, string ConfigId, long Sequence, string DeliveryId); // Sequence set by the store; Deliveries empty until Task 2
// TaskStore:
public abstract ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default);
```
Postgres: one transaction — `UPDATE a2a_tasks ... WHERE tenant=@t AND task_id=@id AND owner=@o AND version=@v` (0 rows → `ConcurrencyException`), `UPDATE a2a_event_logs SET last_seq = last_seq + 1 ... RETURNING last_seq` (insert row on create), `INSERT INTO a2a_events`, trim to `ReplayBufferSize`, outbox rows (Task 2) — commit or roll back as a unit. Handler: Apply → `CommitAsync` → Publish; history-only follow-up append keeps `UpdateAsync`.
- [ ] **Step 1: Failing tests.** `CommitTests`: `Commit_writes_task_and_event_together`, `Stale_commit_writes_nothing`, `Create_commit_assigns_sequence_1`, `Failing_store_writes_nothing` (Review Focus 3, wrapper fails after the task write), `[Theory] Handler_uses_commit(binding)` (wrapper counts `CommitAsync` > 0, `UpdateAsync` not used for status/artifact events). Contract (in-memory + Postgres): `Concurrent_commits_log_order_equals_version_order` (Review Focus 2: 20 parallel writers with reload+retry; event i carries metadata "v" = the version it produced; sequence order = version order), `Commit_is_atomic` (Postgres: force a constraint violation on the event insert → task version unchanged).
- [ ] **Step 2: Run** `dotnet test A2A.slnx -f net10.0 --filter "FullyQualifiedName~Commit"`. Expected: compile FAIL.
- [ ] **Step 3: Implement.** Keep the handler's reload-and-retry loop (max 3).
- [ ] **Step 4: Run** focused, `A2A.Stores.Tests`, then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): atomic task commit (in-memory, Postgres)`.

---

### Task 2: Durable push outbox and delivery service

**Files:**
- Create: `src/A2A.Server/Push/PushOutbox.cs`, `InMemoryPushOutbox.cs`, `PushDeliveryService.cs`, `PushDeliveryOptions.cs`, `A2APushAdmin.cs`; `src/A2A.Server.Postgres/PostgresPushOutbox.cs` (+ schema table `a2a_push_outbox`, `a2a_push_dead_letters`)
- Modify: `src/A2A.Server/Push/PushNotifications.cs` (remove `IPushNotificationSender` and the in-memory queue of `HttpPushNotificationSender`; keep the guarded HTTP send as an internal sender), `src/A2A.Server/A2ARequestHandler.cs` (deliveries go into `TaskCommit.Deliveries`, one per push config of the task), in-memory and Postgres `CommitAsync`, DI, `src/A2A.Server.Postgres/PostgresSchema.cs`, tests that used `HttpPushNotificationSender.WhenIdleAsync`
- Test: `tests/A2A.Tests/PushOutboxTests.cs`, `tests/A2A.Stores.Tests/PushOutboxContract.cs`

**Interfaces:**
```csharp
public sealed record OutboxEntry(PushDelivery Delivery, int Attempt, DateTimeOffset DueAt, string? LastError);
public abstract class PushOutbox
{
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default); // at most one in flight per (task, config): the oldest pending
    public abstract ValueTask CompleteAsync(PushDelivery d, CancellationToken ct = default);
    public abstract ValueTask RescheduleAsync(PushDelivery d, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default);
    public abstract ValueTask DeadLetterAsync(PushDelivery d, string error, CancellationToken ct = default);
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default);
    public abstract ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default);
    public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default);
}
public sealed class PushDeliveryOptions { InitialBackoff = 1 s; MaxBackoff = 5 min; MaxAttempts = 10; Lease = 1 min; PollInterval = 1 s; BatchSize = 50; }
public sealed class PushDeliveryService : BackgroundService
public sealed class A2APushAdmin(PushOutbox outbox) { ListDeadLettersAsync(string tenant, int max); RedriveAsync(PushDelivery d); }
```
Payload is read from the event store at send time by `(Task, Sequence)`. URL re-validated per attempt (P0 guard); blocked address / URL policy → dead letter immediately. Backoff exponential ±20% jitter. Counters `a2a.push.deliveries` (tag `outcome` = sent|retried|dead_lettered) and `a2a.push.dead_letters` on meter "A2A". Test helper `TestAgentHost.WaitForPushIdleAsync()`.
- [ ] **Step 1: Failing tests:** `Delivery_survives_service_restart`, `Failed_delivery_retries_with_backoff_then_succeeds` (500,500,200; delays ≈1 s,2 s with fake time; same delivery id), `Dead_letter_after_max_attempts` (MaxAttempts 3), `Redrive_sends_again`, `Order_is_preserved_per_task_and_config`; contract (in-memory + Postgres) for every outbox member incl. two leasing services never getting the same entry; existing push tests adapted.
- [ ] **Step 2: Run** filter `PushOutbox`. Expected: compile FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** focused, stores, full. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(push): durable outbox with retry, ordering and dead letters`.

---

### Task 3: Signed webhooks (Standard Webhooks)

**Files:** Create `src/A2A.Protocol/Push/WebhookSignature.cs`; modify the internal push sender, `A2ARequestHandler` (validate/protect/redact the signing secret), `src/A2A.Client/Push/PushNotificationReceiver.cs`. Test `tests/A2A.Tests/WebhookSignatureTests.cs`.

**Interfaces:**
```csharp
public static class WebhookSignature
{
    public const string Scheme = "webhook-signature", SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id", TimestampHeader = "webhook-timestamp", SignatureHeader = "webhook-signature";
    public static string Sign(ReadOnlySpan<byte> secret, string id, long unixSeconds, ReadOnlySpan<byte> body); // "v1," + base64(HMAC-SHA256(secret, $"{id}.{ts}.{body}"))
    public static bool TryParseSecret(string credentials, out byte[] secret); // whsec_<base64>, ≥ 16 bytes
}
// PushNotificationReceiver.VerifySignature(IReadOnlyDictionary<string,string> headers, ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, TimeSpan? tolerance = null) : PushValidationResult
// PushValidationResult gains InvalidSignature, Stale
```
Config with `authentication.scheme == "webhook-signature"`: secret validated at create (else InvalidParams "Invalid webhook signing secret."), stored protected, redacted on read, never sent as `Authorization`; deliveries carry the three headers (`webhook-id` = delivery id); P0 token headers unchanged.
- [ ] **Step 1: Failing tests:** `Sign_matches_standard_webhooks_vector` (secret `whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw`, id `msg_p5jXN8AQM9LWM0D4loKWxJek`, ts 1614265330, body `{"test": 2432232314}` → `v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=`), `[Theory] Signed_delivery_verifies(binding)`, `Signing_secret_never_sent_or_returned`, `Replay_is_duplicate_and_stale_is_rejected` (Review Focus 5), `Tampered_body_is_invalid_signature`, `Short_secret_rejected_at_create`.
- [ ] **Step 2–4:** run → FAIL; implement (constant-time compare; accept multiple space-separated `v1,` signatures); run focused + full → `Failed: 0`.
- [ ] **Step 5: Commit** `feat(push): Standard Webhooks signatures and receiver verification`.

---

### Task 4: Retention contract members (in-memory + Postgres)

**Files:** Modify `TaskStore.cs`, `EventStore.cs`, `PushConfigStore.cs`, `IdempotencyStore.cs` and their in-memory and Postgres implementations; `A2ARequestHandler` (use `SaveIfUnderLimitAsync`); contract files in `tests/A2A.Stores.Tests`.

**Interfaces:**
```csharp
// TaskStore
ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default);
ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default);
ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default); // INPUT_REQUIRED/AUTH_REQUIRED by status time
ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default);
ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default);
// EventStore
ValueTask DeleteAsync(TaskKey task, CancellationToken ct = default);
// PushConfigStore
ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default);
ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken ct = default); // replace allowed; false at limit
// IdempotencyStore
ValueTask RenewAsync(IdempotencyScope scope, CancellationToken ct = default); // extends a running claim's timeout
```
All abstract. Postgres `IdempotencyClaimTimeout` default changes from 1 hour to 2 minutes (spec §0); document it.
- [ ] **Step 1: Failing contract tests** (in-memory + Postgres) for each member, owner scoping on each, and `SaveIfUnderLimitAsync` with 20 parallel savers, limit 10 → exactly 10 stored.
- [ ] **Step 2–4:** run → FAIL; implement; run stores + full → `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): retention store members (in-memory, Postgres)`.

---

### Task 5: Execution coordinator (in-memory + Redis)

**Files:** Create `src/A2A.Server/Coordination/ExecutionCoordinator.cs`, `InMemoryExecutionCoordinator.cs`, `CoordinationOptions.cs`; `src/A2A.Server.Redis/RedisExecutionCoordinator.cs` (+ DI `AddRedisCoordinator` / included in `AddRedisEventBus`? — add a separate `AddRedisExecutionCoordinator(...)` and make `AddRedisEventBus` register both unless opted out); modify `A2ARequestHandler` (register/heartbeat/unregister every execution; heartbeat also calls `IdempotencyStore.RenewAsync` for the execution's claim; `CancelTaskAsync` → `RequestCancelAsync`; each instance consumes `CancelRequestsAsync` and cancels local executions; blocking waits and replays use the bus plus `IsRunningAsync` every `RunningPollInterval` instead of process-local completions). Test: `tests/A2A.Stores.Tests/ExecutionCoordinatorContract.cs`, additions to `MultiInstanceTests.cs`.

**Interfaces:**
```csharp
public abstract class ExecutionCoordinator
{
    ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default);
    ValueTask HeartbeatAsync(TaskKey task, string executionId, CancellationToken ct = default);
    ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default);
    ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default);
    ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default);
    IAsyncEnumerable<TaskKey> CancelRequestsAsync(CancellationToken ct);
    ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default); // store clock
    ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default);
}
public sealed class CoordinationOptions { LeaseTtl = 30 s; Heartbeat = 10 s; RunningPollInterval = 1 s; }
```
Redis: lease keys with `PX` TTL and expiry stored as Redis `TIME`-based timestamps; cancel channel `{prefix}cancel`; expired listing via a sorted set scored by expiry (Redis time).
- [ ] **Step 1: Failing tests:** coordinator contract (in-memory + Redis): register/heartbeat/unregister/IsRunning, lease expiry with fake time (in-memory) and short TTL (Redis), cancel signal reaches another coordinator instance, expired listing, sweep lease exclusivity, Review Focus 4 (skewed instance `TimeProvider` +60 s does not expire a fresh Redis lease). Multi-instance (Postgres + Redis, two hosts): CancelTask on B cancels an execution on A; blocking send on B returns when A finishes; Review Focus 2 follow-up on B during A's execution → identical sequences on both subscribers; Review Focus 1 kill A's execution heartbeats (dispose host A abruptly) → B's sweep (Task 6 adds it; here assert `ListExpiredAsync` reports it).
- [ ] **Step 2–4:** run → FAIL; implement; run stores (three times) + full → `Failed: 0`.
- [ ] **Step 5: Commit** `feat: execution coordinator (in-memory, Redis)`.

---

### Task 6: Retention service, interrupted-state timeout, dead executions

**Files:** Create `src/A2A.Server/Retention/A2ARetentionOptions.cs`, `A2ARetentionService.cs`, `A2ATaskAdmin.cs`; modify `A2AServerOptions.cs` (`InterruptedTimeout`), DI (`AddA2ARetention`), `A2ARequestHandler` (internal `CommitSystemStatusAsync(TaskKey, TaskState, string text)` through the normal commit + publish path), `src/A2A.Server.Postgres/PostgresMaintenance.cs` (leave its own purges; retention service handles tasks/contexts/push configs). Test `tests/A2A.Tests/RetentionTests.cs`, one Postgres-backed case in `A2A.Stores.Tests`.

**Interfaces:** `A2ARetentionOptions { TaskRetention = 7 d; OrphanContextRetention = 7 d; SweepInterval = 5 min; BatchSize = 500; }`; `A2AServerOptions.InterruptedTimeout : TimeSpan?` = 24 h (null disables); `A2ATaskAdmin.DeleteTaskAsync(TaskKey) : ValueTask<bool>`. Texts exactly `"The task waited too long for input."` (CANCELED) and `"The agent stopped unexpectedly."` (FAILED). One sweeper at a time via `TryAcquireSweepLeaseAsync("a2a:retention", SweepInterval)`.
- [ ] **Step 1: Failing tests:** `Expired_terminal_task_is_purged_with_its_data` (task, events, push configs, outbox), `Recent_and_running_tasks_are_kept`, `Interrupted_task_times_out_to_canceled` (subscriber + webhook see it), `Dead_execution_marks_task_failed` (Review Focus 1), `Orphan_contexts_are_removed`, `Only_one_instance_sweeps`, `Admin_delete_removes_everything`; Postgres-backed purge case.
- [ ] **Step 2–4:** run → FAIL; implement; run focused, stores, full → `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): retention, interrupted-state timeout, dead-execution recovery`.

---

### Task 7: Approval binding, cancel-requested, follow-up de-dup

As in spec §6.6 and §6.9 (follow-up de-dup by messageId). **Files:** create `src/A2A.Server/Approval/PendingApproval.cs`; modify `AgentExecutor.cs` (`AgentRequestContext.Approval`, `TaskUpdater.RequireApprovalAsync`), `A2ARequestHandler` (`PrepareAsync`, `CancelTaskAsync` using `ExecutionCoordinator.IsRunningAsync`), `A2AProtocol` constants `ApprovalHashKey = "a2a.approval.hash"`, `CancelRequestedKey = "a2a.cancelRequested"`. Tests `ApprovalTests.cs`, `CancelRequestedTests.cs`.

**Interfaces:** `PendingApproval(string OperationId, string Description) { string Hash }` (lowercase hex SHA-256 of UTF-8 `$"{OperationId}\n{Description}"`); `enum ApprovalState { None, Approved, NotApproved }`; mismatched hash → InvalidParams "Approval does not match the pending request."; missing → NotApproved; slow cancel → task with response metadata `a2a.cancelRequested = true` (not stored); refused cancel with nothing running → TaskNotCancelable; follow-up whose messageId is already in history → not appended again.
- [ ] **Step 1: Failing tests:** `[Theory] Matching_hash_approves`, `Mismatched_hash_rejected`, `Missing_hash_is_not_approved`, `Executor_without_pending_sees_none`, `Slow_cancel_returns_cancel_requested`, `Refused_cancel_is_not_cancelable`, `Duplicate_follow_up_is_not_appended_twice` (both bindings where HTTP).
- [ ] **Step 2–4:** run → FAIL; implement; run → `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): approval binding, cancel-requested, follow-up de-dup`.

---

### Task 8: Multi-agent hosting

As in spec §6.5 (one agent = one tenant). **Files:** create `src/A2A.Server/Agents/AgentCardProvider.cs`, `AgentExecutorResolver.cs`, `AgentRequestScope.cs`; modify `A2AServerOptions.cs` (`ExtendedAgentCardProvider` adapter), `A2ARequestHandler` (capability checks from the resolved card; executor resolution in `StartRaw`), DI builder (`AddAgentCardProvider<T>()`, `AddAgentExecutorResolver<T>()`), `AgentCardEndpoint.cs` / `A2AEndpointRouteBuilderExtensions.cs` (`/{tenant}/.well-known/agent-card.json`, per-tenant JWKS), `src/A2A.Server.Grpc/A2AGrpcService.cs`. Tests `MultiAgentTests.cs` + a gRPC case.

**Interfaces:**
```csharp
public sealed record AgentRequestScope(string Tenant, IReadOnlyDictionary<string, string?> RouteValues);
public abstract class AgentCardProvider { ValueTask<AgentCard?> GetCardAsync(AgentRequestScope, CancellationToken); virtual ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext, CancellationToken); virtual ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken); }
public abstract class AgentExecutorResolver { IAgentExecutor? Resolve(string tenant, IServiceProvider services); }
```
Unknown tenant: card 404; task ops TaskNotFound; SendMessage/ListTasks/extended card → UnsupportedOperation "No agent is configured for this tenant."
- [ ] **Step 1: Failing tests:** tenants "acme"/"globex" with different cards (streaming on/off) and executors, over JSON-RPC, HTTP+JSON, gRPC: card per tenant; right executor; streaming refused where disabled; no cross-tenant visibility; unknown tenant behaviour; per-tenant signed card verifies with its JWKS.
- [ ] **Step 2–4:** run → FAIL; implement; run both test projects → `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): multi-agent hosting`.

---

### Task 9: P0 carry-overs

**Files/changes:** `WithA2AStartupCheck` → `internal` (if still public); `ResumableStream` treats a snapshot below the cursor as a server reset (re-baseline, yield it); anonymous IPv6 partition by /64 (`anon:{first 64 bits}::/64`) in `ServerCallContext`/`A2ARateLimiting`/`StreamAdmission`; gRPC test where a custom `ITenantResolver` tenant differs from the request `tenant` → InvalidParams.
- [ ] **Step 1: Failing tests** for each item.
- [ ] **Step 2–4:** run → FAIL; implement; full → `Failed: 0`.
- [ ] **Step 5: Commit** `fix: P0 carry-overs`.

---

### Task 10: Docs, sample, CI

**Files:** `README.md` and package READMEs (outbox, signatures, coordinator, retention, multi-agent, upgrade notes: `IPushNotificationSender` removed, `TaskStore.CommitAsync`, `IdempotencyClaimTimeout` default 2 min), `samples/` (a multi-instance sample using Postgres + Redis via docker compose), `.github/workflows/*` (new test classes run under `A2A_REQUIRE_DOCKER=1`; release packs unchanged).
- [ ] **Step 1:** docs + sample; build sample `-warnaserror`; run it once with docker compose and two instances; report output.
- [ ] **Step 2:** full verification: build, full suite three times, `A2A.Stores.Tests` with Docker.
- [ ] **Step 3: Commit** `docs: P1 features, multi-instance sample, CI`.

---

## Order and parallelism

Task 1 → Task 2 → Task 3. Task 4 after Task 2. Task 5 after Task 4. Task 6 and Task 7 after Task 5. Task 8 independent (touches `A2ARequestHandler`: run between other handler tasks or in a worktree). Task 9 independent. Task 10 last. Another session may still be landing tracing on `main`: rebase onto `origin/main` before Task 1 and before the final review.
