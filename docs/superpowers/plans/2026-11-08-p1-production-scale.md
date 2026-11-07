# P1 Production Scale Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the A2A .NET SDK deployable at production scale: durable EF Core and Redis stores, multiple server instances, a durable signed webhook outbox, multi-agent hosting, task lifecycle hardening, observability, and retention.

**Architecture:** Core contracts in `A2A.Server` gain an atomic commit (task CAS + event + outbox entries), a push outbox, an execution coordinator and retention members, each with an in-memory implementation and a shared contract test suite. Provider packages (`A2A.Server.Storage.EntityFrameworkCore`, `A2A.Server.Redis`) implement the same contracts and run the same tests. Hosted services deliver webhooks and sweep retention.

**Tech Stack:** .NET 8/10, ASP.NET Core, System.Text.Json source generation, EF Core 8+ (SQLite for tests, PostgreSQL in CI), StackExchange.Redis 2.8+, System.Diagnostics (ActivitySource, Meter), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-p1-production-scale-design.md` (sections cited as §6.x; its section 10 "Calls I made" items are binding). P0 background: `docs/superpowers/specs/2026-11-07-p0-security-correctness-design.md`, `docs/superpowers/plans/2026-11-07-p0-rulings.md`.

## Global Constraints

- Libraries target `net8.0;net10.0`; `TreatWarningsAsErrors` on; `IsAotCompatible` on core packages. Provider packages may set `IsAotCompatible=false` only if their dependency is not trim-safe, with a comment saying why.
- No third-party runtime dependency in `A2A.Protocol`, `A2A.Client`, `A2A.Server`, `A2A.Server.AspNetCore`. New dependencies only in `A2A.Server.Storage.EntityFrameworkCore` (Microsoft.EntityFrameworkCore.Relational ≥ 8) and `A2A.Server.Redis` (StackExchange.Redis ≥ 2.8). Tracing/metrics use BCL `System.Diagnostics` only.
- NuGet package IDs follow the existing `RomanAmbinder.$(MSBuildProjectName)` rule (already in `Directory.Build.props`).
- Every P0 guarantee holds for every provider: owner-scoped keys (no lookup by task id alone), foreign data reported exactly like missing data, version compare-and-set, in-order gap-free delivery, fail-closed auth, wire compatibility with plain A2A v1.0 peers (additions are ignorable metadata/headers only).
- One agent = one tenant. `TaskKey` stays `(Tenant, Owner, TaskId)`.
- All provider tests run in CI. Redis tests use env var `A2A_TEST_REDIS` (connection string); when it is unset they are skipped with the explicit reason "A2A_TEST_REDIS not set", never passed. EF Core tests use SQLite in-memory (`DataSource=:memory:` with an open connection) locally and in CI; a separate CI job runs them against PostgreSQL via env var `A2A_TEST_POSTGRES`.
- Never delete a test to make a task pass; rewrite only when it asserts behaviour this spec changes, and say so in the commit message.
- Verification: `dotnet build A2A.slnx -warnaserror` → `0 Error(s)`; `dotnet test A2A.slnx -f net10.0` → `Failed: 0` (net8 tests run in CI; this machine has no ASP.NET Core 8 runtime).
- Commits end with the committing model's own `Co-Authored-By` line. Never push. Never use bare `git stash`. Never commit `.superpowers/`.

## Review Focus

1. **Instance killed mid-execution.** The process dies while a task is WORKING. A person expects the task to become FAILED with "The agent stopped unexpectedly." within lease TTL + one sweep, and a subscriber on another instance to receive that FAILED event. Test added in Task 6.
2. **Two instances write the same task at once.** A follow-up lands on instance B while A's execution is still emitting. A person expects the event log order to equal the order the task state changed, with no gaps, and both instances' subscribers to see the same sequence. Test added in Task 12.
3. **Store unavailable during a commit.** The database or Redis fails mid-commit. A person expects the request to fail with a retryable Internal error and nothing partially written: no event without its task change, no outbox entry without its event. Test added to the contract suite in Task 4 (with a failing-store wrapper) and run by Tasks 10 and 11.
4. **Clock skew between instances.** Instance clocks differ by a minute. A person expects leases and retention not to fire early: providers compute lease expiry with the store's clock (Redis `TIME`, database `CURRENT_TIMESTAMP`), not the instance clock. Test added in Task 12 (Redis) and Task 10 (EF, via an injected skewed `TimeProvider` on the instance).
5. **Replayed signed webhook.** A receiver gets the same signed delivery twice, or one older than 5 minutes. A person expects the second to be reported Duplicate and the stale one rejected, with no exception. Test added in Task 3.

---

### Task 1: Atomic commit (task CAS + event append)

**Files:**
- Create: `src/A2A.Server/Storage/TaskCommit.cs`
- Modify: `src/A2A.Server/Storage/TaskStore.cs`, `src/A2A.Server/Storage/InMemoryTaskStore.cs`, `src/A2A.Server/Storage/InMemoryEventStore.cs`, `src/A2A.Server/A2ARequestHandler.cs` (`ApplyAndStoreAsync`, the new-task create path in `ConsumeAsync`), `src/A2A.Server/ServiceCollectionExtensions.cs`
- Test: `tests/A2A.Tests/CommitTests.cs`

**Interfaces:**
- Consumes: P0 `TaskStore`, `EventStore`, `TaskKey`, `TaskRecord`, `ConcurrencyException`, `SequencedEvent`.
- Produces:

```csharp
public sealed record TaskCommit(TaskRecord Record, long ExpectedVersion, StreamResponse Event, IReadOnlyList<PushDelivery> Deliveries)
{   // ExpectedVersion 0 = create (Record.Version must be 0); otherwise CAS on ExpectedVersion
    public bool IsCreate => ExpectedVersion == 0;
}
public sealed record CommitResult(TaskRecord Record, long Sequence);
public abstract class TaskStore { public abstract ValueTask<CommitResult> CommitAsync(TaskCommit commit, CancellationToken ct = default); /* existing members unchanged */ }
public sealed record PushDelivery(TaskKey Task, string ConfigId, long Sequence, string DeliveryId);  // Sequence filled by the store; Deliveries is empty until Task 2
```

  `InMemoryTaskStore` takes `InMemoryEventStore` in its constructor and performs CAS + append under one lock. `CommitAsync` throws `ConcurrencyException` (or `DuplicateTaskException` for create) with nothing written. The handler path becomes Apply → `CommitAsync` → Publish; `UpdateAsync` remains for history-only changes (follow-up append).
- [ ] **Step 1: Write failing tests** in `CommitTests.cs`:

```csharp
[Fact] Commit_writes_task_and_event_together()           // after CommitAsync: GetAsync(key).Version == 2 and LastSequenceAsync(key) == result.Sequence
[Fact] Stale_commit_writes_nothing()                     // CommitAsync(expected: 1) after another commit → ConcurrencyException; event count unchanged; version unchanged
[Fact] Create_commit_assigns_sequence_1()                // IsCreate → stored Version 1, Sequence 1
[Fact] Concurrent_commits_log_order_equals_version_order() // 20 parallel writers with reload+retry; read events: sequence i corresponds to version i+1 (store the version in event metadata "v" in the test)
[Theory] Handler_uses_commit(binding)                    // existing BindingTests still pass; a TaskStore wrapper counts CommitAsync calls > 0 and UpdateAsync is not called for status/artifact events
```
- [ ] **Step 2: Run** `dotnet test A2A.slnx -f net10.0 --filter "FullyQualifiedName~CommitTests"`. Expected: compile FAIL (`TaskCommit` missing).
- [ ] **Step 3: Implement** the types and in-memory commit; switch `ApplyAndStoreAsync` and the create path to `CommitAsync` (keep the reload-and-retry loop on `ConcurrencyException`, max 3 retries as today).
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): atomic task commit (CAS + event append)`.

---

### Task 2: Durable push outbox and delivery service

**Files:**
- Create: `src/A2A.Server/Push/PushOutbox.cs`, `src/A2A.Server/Push/InMemoryPushOutbox.cs`, `src/A2A.Server/Push/PushDeliveryService.cs`, `src/A2A.Server/Push/PushDeliveryOptions.cs`, `src/A2A.Server/Push/A2APushAdmin.cs`
- Modify: `src/A2A.Server/Push/PushNotifications.cs` (remove `IPushNotificationSender`, `HttpPushNotificationSender`'s queue; keep `PushUrlValidator` and the HTTP send code as `PushHttpSender` internal), `src/A2A.Server/A2ARequestHandler.cs` (`Publish` no longer enqueues; commit carries deliveries for every push config of the task), `src/A2A.Server/Storage/InMemoryTaskStore.cs` (commit writes outbox entries), `src/A2A.Server/ServiceCollectionExtensions.cs`, tests that used `HttpPushNotificationSender`/`WhenIdleAsync` (`TestHost.cs`, `PushDeliveryTests.cs`, `PushConfigTests.cs`, `EventStoreTests.cs`)
- Test: `tests/A2A.Tests/PushOutboxTests.cs`

**Interfaces:**
- Consumes: Task 1 `TaskCommit.Deliveries`, `PushDelivery`.
- Produces:

```csharp
public sealed record OutboxEntry(PushDelivery Delivery, int Attempt, DateTimeOffset DueAt, string? LastError);
public abstract class PushOutbox
{
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> LeaseDueAsync(int max, TimeSpan lease, CancellationToken ct = default); // returns at most one entry per (task, config): the oldest not yet done
    public abstract ValueTask CompleteAsync(PushDelivery d, CancellationToken ct = default);
    public abstract ValueTask RescheduleAsync(PushDelivery d, int attempt, DateTimeOffset dueAt, string error, CancellationToken ct = default);
    public abstract ValueTask DeadLetterAsync(PushDelivery d, string error, CancellationToken ct = default);
    public abstract ValueTask<IReadOnlyList<OutboxEntry>> ListDeadLettersAsync(string tenant, int max, CancellationToken ct = default);
    public abstract ValueTask<bool> RedriveAsync(PushDelivery d, CancellationToken ct = default);
    public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default);
    internal abstract ValueTask EnqueueAsync(IReadOnlyList<PushDelivery> deliveries, CancellationToken ct = default); // used by in-memory commit only
}
public sealed class PushDeliveryOptions { TimeSpan InitialBackoff = 1 s; TimeSpan MaxBackoff = 5 min; int MaxAttempts = 10; TimeSpan Lease = 1 min; TimeSpan PollInterval = 1 s; int BatchSize = 50; }
public sealed class PushDeliveryService : BackgroundService  // leases, re-validates URL (P0 guard), sends with P0 headers, Complete/Reschedule (exp backoff + ±20% jitter)/DeadLetter
public sealed class A2APushAdmin(PushOutbox outbox) { ListDeadLettersAsync(tenant, max); RedriveAsync(delivery); }
```

  Delivery payload = the stored event for `(Task, Sequence)` read from `EventStore` at send time (the outbox stores references, not bodies). Counter `a2a.push.deliveries` (tag `outcome` = sent|retried|dead_lettered) and `a2a.push.dead_letters` on meter "A2A". Order per (task, config) is preserved by `LeaseDueAsync`'s one-in-flight rule. Test helper: `TestAgentHost.WaitForPushIdleAsync()` (polls the outbox until empty, 10 s cap).
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Delivery_survives_service_restart()               // enqueue via commit; stop PushDeliveryService before send; start a new service instance on the same outbox → webhook receives it once
[Fact] Failed_delivery_retries_with_backoff_then_succeeds() // handler 500, 500, 200 → 3 calls; delays ≈ 1 s, 2 s (fake TimeProvider), same X-A2A-Delivery-Id each time
[Fact] Dead_letter_after_max_attempts()                  // MaxAttempts 3, always 500 → entry in ListDeadLettersAsync; counter a2a.push.dead_letters == 1
[Fact] Redrive_sends_again()                             // RedriveAsync → delivered once more
[Fact] Order_is_preserved_per_task_and_config()          // first delivery fails twice then succeeds; second must not be sent before the first succeeded
[Theory] Existing_push_tests_still_pass(binding)         // PushDeliveryTests and PushConfigTests adapted to WaitForPushIdleAsync
```
- [ ] **Step 2: Run** filter `PushOutboxTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement** as specified. Blocked-address and URL-policy failures go straight to dead letter (no retry), as P0 Task 16 decided.
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(push): durable outbox with retry, ordering and dead letters`.

---

### Task 3: Signed webhooks (Standard Webhooks)

**Files:**
- Modify: `src/A2A.Server/Push/PushDeliveryService.cs` (or the internal sender), `src/A2A.Server/A2ARequestHandler.cs` (`Redact`, `SavePushConfigAsync`: protect the signing secret), `src/A2A.Client/Push/PushNotificationReceiver.cs`
- Create: `src/A2A.Protocol/Push/WebhookSignature.cs`
- Test: `tests/A2A.Tests/WebhookSignatureTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class WebhookSignature
{
    public const string Scheme = "webhook-signature";            // AuthenticationInfo.Scheme that carries the secret
    public const string SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id", TimestampHeader = "webhook-timestamp", SignatureHeader = "webhook-signature";
    public static string Sign(ReadOnlySpan<byte> secret, string id, long unixSeconds, ReadOnlySpan<byte> body); // "v1," + base64(HMAC-SHA256(secret, $"{id}.{unixSeconds}.{body}"))
    public static bool TryParseSecret(string credentials, out byte[] secret);                                  // "whsec_<base64>", decoded ≥ 16 bytes
}
// PushNotificationReceiver:
public PushValidationResult VerifySignature(IReadOnlyDictionary<string,string> headers, ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, TimeSpan? tolerance = null); // default 5 min
public enum PushValidationResult { Valid, InvalidToken, Duplicate, InvalidSignature, Stale } // extend existing enum
```

  A config with `authentication.scheme == WebhookSignature.Scheme` is validated at create (secret parse; invalid → InvalidParams "Invalid webhook signing secret."), stored protected, redacted on read, and never sent as `Authorization`. Deliveries for it carry the three headers (`webhook-id` = existing delivery id). The P0 token headers are unchanged.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Sign_matches_standard_webhooks_vector()           // secret "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", id "msg_p5jXN8AQM9LWM0D4loKWxJek", ts 1614265330, body {"test": 2432232314} → "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE="
[Theory] Signed_delivery_verifies(binding)               // config with webhook-signature secret → receiver VerifySignature == Valid
[Fact] Signing_secret_never_sent_or_returned()           // no Authorization header with the secret; Get/List redact credentials
[Fact] Replay_is_duplicate_and_stale_is_rejected()       // Review Focus 5: same headers twice → Duplicate; timestamp 6 min old → Stale; no exception
[Fact] Tampered_body_is_invalid_signature()
[Fact] Short_secret_rejected_at_create()                 // whsec_ of 8 bytes → InvalidParams "Invalid webhook signing secret."
```
- [ ] **Step 2: Run** filter `WebhookSignatureTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement.** Constant-time compare (`CryptographicOperations.FixedTimeEquals`); accept multiple space-separated `v1,` signatures per Standard Webhooks.
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(push): Standard Webhooks signatures and receiver verification`.

---

### Task 4: Store contract additions and the shared contract test suite

**Files:**
- Modify: `src/A2A.Server/Storage/TaskStore.cs`, `EventStore.cs`, `PushConfigStore.cs`, their in-memory implementations, `src/A2A.Server/Idempotency/IdempotencyStore.cs`, `InMemoryIdempotencyStore.cs`, `src/A2A.Server/A2ARequestHandler.cs` (use `WaitForOutcomeAsync`, `SaveIfUnderLimitAsync`)
- Create: `tests/A2A.Server.ContractTests/` — a class library (xUnit referenced, not a test project itself) holding the public abstract `StoreContractTests<TFactory>`, `IStoreFactory`, `StoreSet` and `FailingStores`; referenced by `A2A.Tests` and both provider test projects. Add it to `A2A.slnx`.
- Create: `tests/A2A.Tests/Contracts/InMemoryStoreContractTests.cs` (the in-memory run).

**Interfaces:**
- Produces (all abstract, implemented in-memory):

```csharp
// TaskStore
public abstract ValueTask<bool> DeleteAsync(TaskKey key, CancellationToken ct = default);
public abstract ValueTask<IReadOnlyList<TaskKey>> ListExpiredAsync(DateTimeOffset terminalBefore, int max, CancellationToken ct = default);
public abstract ValueTask<IReadOnlyList<TaskKey>> ListInterruptedBeforeAsync(DateTimeOffset before, int max, CancellationToken ct = default); // INPUT_REQUIRED/AUTH_REQUIRED, status timestamp < before
public abstract ValueTask<bool> DeleteContextAsync(string tenant, string contextId, CancellationToken ct = default);
public abstract ValueTask<IReadOnlyList<ContextRecord>> ListOrphanContextsAsync(DateTimeOffset createdBefore, int max, CancellationToken ct = default);
// EventStore
public abstract ValueTask TrimAsync(TaskKey task, int keepLast, CancellationToken ct = default);
public abstract ValueTask DeleteAsync(TaskKey task, CancellationToken ct = default);
// PushConfigStore
public abstract ValueTask DeleteAllAsync(TaskKey task, CancellationToken ct = default);
public abstract ValueTask<bool> SaveIfUnderLimitAsync(PushConfigRecord record, int limit, CancellationToken ct = default); // replace allowed; false when at limit
// IdempotencyStore
public abstract ValueTask<IdempotencyOutcome?> WaitForOutcomeAsync(IdempotencyScope scope, CancellationToken ct = default); // null when the claim was abandoned or its lease expired
public abstract ValueTask RenewAsync(IdempotencyScope scope, TimeSpan lease, CancellationToken ct = default);
public abstract ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
// IdempotencyResult.InProgress loses its Task-typed member; IdempotencyOptions gains Lease = 30 s and MaxEntriesPerPrincipal = 10_000
public abstract class StoreContractTests<TFactory> where TFactory : IStoreFactory, new()  // ~40 [Fact]s
public interface IStoreFactory { StoreSet Create(TimeProvider time); }  public sealed record StoreSet(TaskStore Tasks, EventStore Events, PushConfigStore PushConfigs, IdempotencyStore Idempotency, PushOutbox Outbox) : IAsyncDisposable;
```
- [ ] **Step 1: Write the contract suite** (fails on missing members). It covers, at minimum: owner scoping on every member (foreign owner sees nothing, cannot delete); CAS + commit atomicity (Task 1 tests move here); create duplicate; list paging and filters (P0 behaviour); context get-or-create first-writer-wins; event read-after/oldest/last/trim/delete; push config limit atomicity (`SaveIfUnderLimitAsync` with 20 parallel savers, limit 10 → exactly 10 stored); outbox lease/one-in-flight/reschedule/dead-letter/redrive/delete; idempotency begin/complete/abandon/mismatch/lease-expiry (`WaitForOutcomeAsync` returns null after lease) /per-principal cap/delete-expired; expired and interrupted listing; orphan contexts; **Review Focus 3**: `FailingStores` wrapper throws on the second internal write of a commit → afterwards no event, no outbox entry, version unchanged.
- [ ] **Step 2: Run** `dotnet test A2A.slnx -f net10.0 --filter "FullyQualifiedName~InMemoryStoreContractTests"`. Expected: compile FAIL.
- [ ] **Step 3: Implement** the members in-memory; switch the handler to `WaitForOutcomeAsync` (replay path) and `SaveIfUnderLimitAsync` (create + inline push configs; message text unchanged).
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): store contract additions and shared contract suite`.

---

### Task 5: Abstract event bus and execution coordinator

**Files:**
- Modify: `src/A2A.Server/TaskEventBus.cs` (→ abstract `TaskEventBus` + `InMemoryTaskEventBus`), `src/A2A.Server/A2ARequestHandler.cs` (subscriptions, `RunningCompletions`, `WaitUntilSettledAsync`, blocking send, `CancelTaskAsync`), `src/A2A.Server/RunningRegistry.cs` (stays as the local registry)
- Create: `src/A2A.Server/Coordination/ExecutionCoordinator.cs`, `src/A2A.Server/Coordination/InMemoryExecutionCoordinator.cs`, `src/A2A.Server/Coordination/CoordinationOptions.cs`
- Create: `tests/A2A.Server.ContractTests/BusContractTests.cs`, `CoordinatorContractTests.cs`; `tests/A2A.Tests/Contracts/InMemoryBusContractTests.cs`
- Test: existing streaming/cross-execution/idempotency suites must stay green

**Interfaces:**
- Produces:

```csharp
public abstract class TaskEventBus
{
    public abstract ValueTask<TaskSubscription> SubscribeAsync(TaskKey task, CancellationToken ct = default); // returns once active
    public abstract void Publish(TaskKey task, SequencedEvent evt);   // non-blocking, ordered per instance
    public abstract void Complete(TaskKey task);                       // non-blocking
}
public abstract class TaskSubscription : IAsyncDisposable { public abstract ChannelReader<SequencedEvent> Reader { get; } public abstract Task Completion { get; } }
public abstract class ExecutionCoordinator
{
    public abstract ValueTask RegisterAsync(TaskKey task, string executionId, CancellationToken ct = default);
    public abstract ValueTask UnregisterAsync(TaskKey task, string executionId, CancellationToken ct = default);
    public abstract ValueTask<bool> IsRunningAsync(TaskKey task, CancellationToken ct = default);
    public abstract ValueTask RequestCancelAsync(TaskKey task, CancellationToken ct = default);
    public abstract IAsyncEnumerable<TaskKey> CancelRequestsAsync(CancellationToken ct);              // this instance's subscription to cancel signals
    public abstract ValueTask<IReadOnlyList<(TaskKey Task, string ExecutionId)>> ListExpiredAsync(int max, CancellationToken ct = default); // leases past TTL (store clock)
    public abstract ValueTask<bool> TryAcquireSweepLeaseAsync(string name, TimeSpan duration, CancellationToken ct = default); // used by retention (Task 6)
}
public sealed class CoordinationOptions { TimeSpan LeaseTtl = 30 s; TimeSpan Heartbeat = 10 s; TimeSpan RunningPollInterval = 1 s; }
```

  The handler registers every execution with the coordinator, heartbeats it every `Heartbeat`, unregisters on completion; `CancelTaskAsync` calls `RequestCancelAsync` and every instance cancels its local executions for that task on a signal; blocking waits use the bus plus `IsRunningAsync` every `RunningPollInterval` (no process-local `Task` waits). Local fast paths stay (local registry checked first).
- [ ] **Step 1: Write the bus and coordinator contract suites** (subscribe-before-publish delivery, ordering, complete ends subscription, dispose releases; register/unregister/IsRunning, lease expiry by TTL with fake time, cancel signal reaches subscribers, expired listing). Run in-memory.
- [ ] **Step 2: Run** filter `Bus|Coordinator`. Expected: compile FAIL.
- [ ] **Step 3: Implement** and migrate the handler. Keep all P0 tests green (CrossExecutionTests, HandlerSequenceTests, IdempotencyTests, StreamingResumeTests).
- [ ] **Step 4: Run** full suite three times in a row. Expected: `Failed: 0` each run.
- [ ] **Step 5: Commit** `refactor(server): abstract event bus and execution coordinator`.

---

### Task 6: Retention service, interrupted-state TTL and dead executions

**Files:**
- Create: `src/A2A.Server/Retention/A2ARetentionOptions.cs`, `A2ARetentionService.cs`, `A2ATaskAdmin.cs`
- Modify: `src/A2A.Server/A2AServerOptions.cs` (`InterruptedTimeout`), `src/A2A.Server/ServiceCollectionExtensions.cs` (`AddA2ARetention`), `src/A2A.Server/A2ARequestHandler.cs` (an internal `CommitSystemStatusAsync(TaskKey, TaskState, string text)` used by the sweeper)
- Test: `tests/A2A.Tests/RetentionTests.cs`

**Interfaces:**
- Consumes: Task 4 listing/deletion members, Task 5 `ExecutionCoordinator.ListExpiredAsync`, Task 2 `PushOutbox.DeleteAllAsync`.
- Produces: `A2ARetentionOptions { TimeSpan TaskRetention = 7 d; TimeSpan OrphanContextRetention = 7 d; TimeSpan IdempotencyRetention = 24 h; TimeSpan SweepInterval = 5 min; int BatchSize = 500; }`; `A2AServerOptions.InterruptedTimeout : TimeSpan?` = 24 h (null disables); `A2ATaskAdmin.DeleteTaskAsync(TaskKey) : ValueTask<bool>` (deletes task, events, push configs, outbox entries). Status texts exactly: `"The task waited too long for input."` (CANCELED), `"The agent stopped unexpectedly."` (FAILED). A sweep run is guarded by `ExecutionCoordinator.TryAcquireSweepLeaseAsync("a2a:retention", SweepInterval)` (Task 5) so one instance sweeps at a time.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Expired_terminal_task_is_purged_with_its_data()   // fake time +8 d → task, events, push configs, outbox gone; GetTask → TaskNotFound
[Fact] Recent_and_running_tasks_are_kept()
[Fact] Interrupted_task_times_out_to_canceled()          // INPUT_REQUIRED, +25 h → CANCELED with exact text; a live subscriber receives the status event; webhook receives it
[Fact] Dead_execution_marks_task_failed()                // Review Focus 1: register execution, stop heartbeats (simulate death), advance past LeaseTtl, sweep → FAILED with exact text; subscriber receives it
[Fact] Orphan_contexts_are_removed()
[Fact] Expired_idempotency_entries_are_removed()
[Fact] Only_one_instance_sweeps()                        // two services sharing one coordinator → one acquires the lease
[Fact] Admin_delete_removes_everything()
```
- [ ] **Step 2: Run** filter `RetentionTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement.** System status transitions go through the normal commit + publish path (subscribers and outbox see them).
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): retention, interrupted-state timeout, dead-execution recovery`.

---

### Task 7: Approval binding, cancel-requested, follow-up de-duplication

**Files:**
- Create: `src/A2A.Server/Approval/PendingApproval.cs`
- Modify: `src/A2A.Server/AgentExecutor.cs` (`AgentRequestContext.Approval`, `TaskUpdater.RequireApprovalAsync`), `src/A2A.Server/A2ARequestHandler.cs` (`PrepareAsync` approval check and messageId de-dup; `CancelTaskAsync`)
- Test: `tests/A2A.Tests/ApprovalTests.cs`, `tests/A2A.Tests/CancelRequestedTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record PendingApproval(string OperationId, string Description) { public string Hash { get; } } // Hash = lowercase hex SHA-256 of UTF-8 $"{OperationId}\n{Description}"
public enum ApprovalState { None, Approved, NotApproved }
// AgentRequestContext.Approval : ApprovalState
// TaskUpdater.RequireApprovalAsync(PendingApproval approval, TaskState state = TaskState.InputRequired, string? text = null): sets status with metadata["a2a.approval.hash"] = approval.Hash
public const string ApprovalHashKey = "a2a.approval.hash";       // in A2AProtocol (constants class)
public const string CancelRequestedKey = "a2a.cancelRequested";  // in A2AProtocol
```

  Handler: pending hash = metadata of the task's current interrupted status. Follow-up message metadata hash present and different → InvalidParams "Approval does not match the pending request."; absent → accepted, `Approval = NotApproved`; equal → `Approved`; no pending → `None`. Follow-up whose `messageId` is already in the task history → not appended again; the request returns the current task (blocking) or resumes the stream from the follow-up's recorded sequence. CancelTask: if after the grace period the task is not CANCELED and `ExecutionCoordinator.IsRunningAsync` is true → return the task with response metadata `a2a.cancelRequested = true` (not stored); else unchanged P0 behaviour.
- [ ] **Step 1: Write failing tests:**

```csharp
[Theory] Matching_hash_approves(binding)   [Theory] Mismatched_hash_rejected(binding)   [Theory] Missing_hash_is_not_approved(binding)
[Fact] Executor_without_pending_sees_none()
[Theory] Slow_cancel_returns_cancel_requested(binding)   // agent ignores cancel for 3 s, grace 1 s → WORKING task with metadata a2a.cancelRequested=true; later CANCELED on the stream
[Theory] Refused_cancel_is_not_cancelable(binding)       // CancelAsync returns without cancelling, nothing running → TaskNotCancelable
[Theory] Duplicate_follow_up_is_not_appended_twice(binding) // same messageId twice after the first claim was abandoned → history contains it once
```
- [ ] **Step 2: Run** filter `Approval|CancelRequested`. Expected: compile FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): approval binding, cancel-requested, follow-up de-dup`.

---

### Task 8: Multi-agent hosting

**Files:**
- Create: `src/A2A.Server/Agents/AgentCardProvider.cs`, `AgentExecutorResolver.cs`, `AgentRequestScope.cs`
- Modify: `src/A2A.Server/A2AServerOptions.cs` (`ExtendedAgentCardProvider` becomes an adapter), `src/A2A.Server/A2ARequestHandler.cs` (all `_options.AgentCard` capability checks → resolved card; executor resolution in `StartRaw`), `src/A2A.Server/ServiceCollectionExtensions.cs` (builder `AddAgentCardProvider<T>()`, `AddAgentExecutorResolver<T>()`), `src/A2A.Server.AspNetCore/AgentCardEndpoint.cs` and `A2AEndpointRouteBuilderExtensions.cs` (`MapAgentCard()` also maps `/{tenant}/.well-known/agent-card.json` and per-tenant JWKS when the provider supplies a signing key), `src/A2A.Server.Grpc/A2AGrpcService.cs`
- Test: `tests/A2A.Tests/MultiAgentTests.cs` (+ gRPC case in `tests/A2A.Grpc.Tests`)

**Interfaces:**
- Produces:

```csharp
public sealed record AgentRequestScope(string Tenant, IReadOnlyDictionary<string, string?> RouteValues);
public abstract class AgentCardProvider
{
    public abstract ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken ct = default);     // null = no agent for this tenant
    public virtual ValueTask<AgentCard?> GetExtendedCardAsync(ServerCallContext call, CancellationToken ct = default) => ValueTask.FromResult<AgentCard?>(null);
    public virtual ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken ct = default) => ValueTask.FromResult<AgentCardSigningKey?>(null);
}
public abstract class AgentExecutorResolver { public abstract IAgentExecutor? Resolve(string tenant, IServiceProvider services); }
```

  Default implementations wrap the single configured card/executor (P0 behaviour unchanged). A tenant with no agent: card endpoint 404; every operation → TaskNotFound for task ops, `UnsupportedOperation` "No agent is configured for this tenant." for SendMessage/ListTasks/extended card.
- [ ] **Step 1: Write failing tests:** two tenants ("acme", "globex") with different cards (names, streaming on/off) and executors on one host. Assert per binding (JSON-RPC, HTTP+JSON, gRPC): each tenant's card endpoint serves its card; SendMessage reaches the right executor; streaming refused for the tenant whose card disables it; acme cannot see globex tasks; unknown tenant behaviour as above; per-tenant signed card verifies with that tenant's JWKS.
- [ ] **Step 2: Run** filter `MultiAgent`. Expected: compile FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** focused then full suite (both test projects). Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): multi-agent hosting via card provider and executor resolver`.

---

### Task 9: Observability

**Files:**
- Create: `src/A2A.Protocol/Diagnostics/A2ADiagnostics.cs` (ActivitySource "A2A", Meter "A2A", names as constants), `samples/EchoAgent` OpenTelemetry wiring (sample only)
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (spans + metrics per operation), `src/A2A.Server/Push/PushDeliveryService.cs` (delivery spans), `src/A2A.Client/A2AClient.cs` and transports (client spans; `traceparent` into `SendMessageRequest.Metadata`), push payload metadata `traceparent`; `src/A2A.Client/A2ACardResolver.cs` and `InMemoryIdempotencyStore.cs` (reuse `A2ADiagnostics.Meter`)
- Test: `tests/A2A.Tests/DiagnosticsTests.cs`

**Interfaces:**
- Produces: span names `a2a.server {method}`, `a2a.client {method}`, `a2a.push.deliver`; attributes `rpc.system`="a2a", `rpc.method`, `a2a.task.id`, `a2a.context.id`, `a2a.binding`, `error.type`; `a2a.tenant` only when `A2AServerOptions.Diagnostics.IncludeTenant` is true (default false). Metrics: `a2a.server.requests` (counter; tags `rpc.method`, `outcome`), `a2a.server.request.duration` (histogram, seconds), `a2a.server.active_streams` (up-down counter), plus Task 2's push counters and the existing P0 counters. Metadata key `traceparent`.
- [ ] **Step 1: Write failing tests** with an `ActivityListener` and `MeterListener`: server span per operation with the attributes and no message content; client span is parent of the server span across TestServer (W3C header); `traceparent` present in request metadata and in push payload metadata; idempotent retry with a new attempt still matches (RequestHasher ignores request metadata); tenant attribute absent by default; metrics recorded with outcome tags.
- [ ] **Step 2: Run** filter `DiagnosticsTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** focused then full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat: tracing and metrics via System.Diagnostics`.

---

### Task 10: EF Core provider

**Files:**
- Create: `src/A2A.Server.Storage.EntityFrameworkCore/` (csproj, `A2AModelBuilderExtensions.AddA2AModel(this ModelBuilder)`, entity types, `EfTaskStore`, `EfEventStore`, `EfPushConfigStore`, `EfIdempotencyStore`, `EfPushOutbox`, `EfExecutionCoordinator` (leases in a table; cancel signals by polling a table every `RunningPollInterval` — documented as the EF fallback; Redis is the recommended bus), `A2AEntityFrameworkBuilderExtensions.AddA2AEntityFrameworkStores<TDbContext>()`), README
- Create: `tests/A2A.Server.Storage.EntityFrameworkCore.Tests/` (SQLite + PostgreSQL contract runs, integration suite run against EF stores)
- Modify: `A2A.slnx`, `Directory.Packages.props`, `.github/workflows/ci.yml` (PostgreSQL service job setting `A2A_TEST_POSTGRES`)

**Interfaces:**
- Consumes: all contracts from Tasks 1, 2, 4, 5.
- Produces: `AddA2AEntityFrameworkStores<TDbContext>()` where `TDbContext : DbContext` and its `OnModelCreating` calls `modelBuilder.AddA2AModel(schema: "a2a")`. Startup fails with "Durable A2A stores need a shared push-secret protector. Call AddA2ADataProtectionPushSecrets() with a shared key ring." if `EphemeralPushSecretProtector` is registered. `CommitAsync` = one DB transaction: `UPDATE tasks SET ..., version = @v+1 WHERE tenant=@t AND owner=@o AND id=@id AND version=@v` (0 rows → ConcurrencyException), insert event with `sequence = tasks.last_sequence + 1` updated in the same statement batch, insert outbox rows. Lease expiry uses database time.
- [ ] **Step 1: Wire the shared contract suites** (`StoreContractTests`, `CoordinatorContractTests`) for SQLite and PostgreSQL factories; add an integration fixture that runs the existing `BindingTests`/`IdempotencyTests`/`StreamingResumeTests` classes against EF-backed `TestAgentHost` (factor `TestAgentHost` to accept a store registration callback). Include **Review Focus 4**: skewed instance `TimeProvider` (+60 s) does not expire a fresh lease.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Server.Storage.EntityFrameworkCore.Tests -f net10.0`. Expected: compile FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the provider tests and the full suite. Expected: `Failed: 0` (PostgreSQL tests skipped locally with the explicit reason if `A2A_TEST_POSTGRES` is unset).
- [ ] **Step 5: Commit** `feat(storage): EF Core provider`.

---

### Task 11: Redis provider — stores

**Files:**
- Create: `src/A2A.Server.Redis/` (csproj, `RedisOptions { Configuration, KeyPrefix = "a2a:" }`, `RedisTaskStore`, `RedisEventStore`, `RedisPushConfigStore`, `RedisIdempotencyStore`, `RedisPushOutbox`, Lua scripts as embedded resources, `A2ARedisBuilderExtensions.AddA2ARedis(Action<RedisOptions>)`), README
- Create: `tests/A2A.Server.Redis.Tests/`
- Modify: `A2A.slnx`, `Directory.Packages.props`, `.github/workflows/ci.yml` (Redis service container, `A2A_TEST_REDIS=localhost:6379`)

**Interfaces:**
- Produces: keys `{prefix}{tenant}:{owner}:task:{id}` etc. (tenant and owner always in the key); commit is one Lua script (CAS on version field, INCR per-task sequence, write event to a sorted set scored by sequence, push outbox entries); same shared-protector startup check and message as Task 10.
- [ ] **Step 1: Wire the shared contract suite** and the integration fixture (as Task 10) for Redis.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Server.Redis.Tests -f net10.0`. Expected: compile FAIL (then: skipped locally with "A2A_TEST_REDIS not set" if no Redis).
- [ ] **Step 3: Implement.** If Docker is available locally, run `docker run -d --rm -p 6379:6379 redis:7` and set `A2A_TEST_REDIS=localhost:6379` to execute the tests; report whether they ran.
- [ ] **Step 4: Run** provider tests and the full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(redis): Redis stores`.

---

### Task 12: Redis bus, coordinator and multi-instance tests

**Files:**
- Create: `src/A2A.Server.Redis/RedisTaskEventBus.cs`, `RedisExecutionCoordinator.cs`
- Modify: `src/A2A.Server.Redis/A2ARedisBuilderExtensions.cs` (`AddA2ARedis` registers bus and coordinator too)
- Test: `tests/A2A.Server.Redis.Tests/MultiInstanceTests.cs`, bus/coordinator contract runs

**Interfaces:**
- Produces: pub/sub channel `{prefix}{tenant}:{owner}:events:{taskId}`; cancel channel `{prefix}cancel`; leases as keys with `PX` TTL using Redis time; `SubscribeAsync` awaits the SUBSCRIBE confirmation; ordered background publisher per instance.
- [ ] **Step 1: Write multi-instance tests** with two `TestAgentHost` instances sharing one Redis: subscriber on B sees events produced on A, in order, no gaps; CancelTask on B cancels an execution on A; idempotent retry on B returns A's task; blocking send on B returns when A's execution finishes; **Review Focus 2**: follow-up on B while A's execution emits → both instances' subscribers see identical sequences and log order equals version order; **Review Focus 4**: instance clock skew (+60 s TimeProvider) does not expire a fresh lease.
- [ ] **Step 2: Run** filter `MultiInstance|Bus|Coordinator` with Redis available. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** provider tests three times in a row and the full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(redis): cross-instance event bus and execution coordinator`.

---

### Task 13: P0 carry-overs

**Files:**
- Modify: `src/A2A.Server.AspNetCore/A2AEndpointRouteBuilderExtensions.cs` (`WithA2AStartupCheck` → `internal`), `src/A2A.Client/Streaming/ResumableStream.cs` (snapshot below cursor = server reset: re-baseline `lastSequence` to the snapshot's sequence and yield it), `src/A2A.Server/ServerCallContext.cs` / `A2ARateLimiting.cs` / `StreamAdmission.cs` (anonymous IPv6 partition by /64: key `anon:{first 64 bits}::/64`), `tests/A2A.Grpc.Tests` (tenant-resolver mismatch test)
- Test: in the respective existing test files

- [ ] **Step 1: Write failing tests:** `ResumableStream` against a server whose sequence restarted (snapshot seq 3 after client cursor 10) → events after the snapshot are yielded; two IPv6 addresses in the same /64 share an anonymous bucket, different /64s do not; gRPC call where a custom `ITenantResolver` tenant differs from the request `tenant` field → InvalidParams; `WithA2AStartupCheck` no longer public (reflection assert in a test).
- [ ] **Step 2: Run** the affected filters. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** full suite. Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `fix: P0 carry-overs (resume reset, IPv6 /64, gRPC tenant test, internal startup check)`.

---

### Task 14: Docs, samples, CI and release readiness

**Files:**
- Modify: `README.md` (durable stores, multi-instance, signed webhooks, multi-agent, retention, observability, upgrade notes from P0 API: `TaskEventBus` abstract, `IPushNotificationSender` removed, `IdempotencyResult.InProgress` change), package READMEs, `samples/` (add `samples/DurableAgent` using EF Core + SQLite and optional Redis), `.github/workflows/ci.yml` (ensure provider test projects run on Ubuntu with Postgres and Redis services; Windows runs core tests only), `.github/workflows/release.yml` (pack new packages)
- Test: CI workflow lint by running the build/test commands it uses locally

- [ ] **Step 1:** Update docs and add the sample; build the sample with `-warnaserror` and run it once (SQLite) end to end with EchoClient-style calls; report output.
- [ ] **Step 2:** Update CI and release workflows; run `dotnet pack A2A.slnx -c Release -o artifacts` locally → new packages produced with `RomanAmbinder.*` IDs.
- [ ] **Step 3:** Full verification: build `-warnaserror`, full suite three times in a row, provider suites (Redis via Docker if available).
- [ ] **Step 4: Commit** `docs: P1 features, sample, CI and release`.

---

## Order and parallelism

- Task 1 first; then Task 2 → Task 3; Task 4 after Task 2 (outbox in the contract suite); Task 5 after Task 4; Task 6 after Task 5; Task 7 after Task 5.
- Task 8 and Task 9 are independent of 2–7 (both touch `A2ARequestHandler`, so run them sequentially relative to each other, or in a worktree with a merge).
- Task 10 and Task 11 after Task 5 (in parallel in separate worktrees); Task 12 after Task 11.
- Task 13 any time (small, separate files); Task 14 last.
