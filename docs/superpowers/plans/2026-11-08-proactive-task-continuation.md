# Proactive Task Continuation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An agent can continue an existing task with no inbound request and no push registration, across instances (gap D8).

**Architecture:** `TaskExecutionEngine.ContinueTaskAsync` takes an atomic exclusive lease, re-checks the task, then starts a normal execution of `IContinuableAgentExecutor.ContinueAsync`. Events use the existing commit path, so store, bus and push need no change.

**Tech Stack:** .NET 8/10, xUnit, PostgreSQL + Redis (Testcontainers), StackExchange.Redis Lua.

**Spec:** `docs/superpowers/specs/2026-11-08-proactive-task-continuation-design.md`

## Global Constraints

- Build: `dotnet build A2A.slnx -c Release` must end with 0 warnings. XML docs are errors, `AnalysisLevel=latest-recommended`.
- Every new public member goes in `PublicAPI.Unshipped.txt` of its project.
- Public `CancellationToken` parameters are named `cancellationToken`. No unbounded channels in `src`.
- Do not change `ConsumeAsync`, `ApplyAndStoreAsync`, `ApplyToCopy` or `StartRawAsync` in `TaskExecutionEngine.cs`. Another session edits the event path.
- New log event ids are 240 and 241 only.
- Docs and comments use plain, short English.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never use bare `git stash`.

## Review Focus

1. A timer fires on every replica at once: exactly one `Started`, the others `AlreadyRunning`.
2. A callback arrives twice during one run: the second gets `AlreadyRunning`, and the first run is unharmed.
3. The task is cancelled or deleted during a continuation: later events are dropped and the stored task does not change.
4. The agent cannot continue, or its factory throws: the task stays exactly as it was and is never FAILED.
5. An instance dies during a continuation: the lease expires and the sweep fails a WORKING task; a claim left by a dead instance blocks nothing after `LeaseTtl`.

---

### Task 1: Exclusive lease in the coordinators

**Files:**
- Modify: `src/A2A.Server/Coordination/ExecutionCoordinator.cs`, `src/A2A.Server/Coordination/InMemoryExecutionCoordinator.cs`, `src/A2A.Server.Redis/RedisExecutionCoordinator.cs`
- Modify: `src/A2A.Server/PublicAPI.Unshipped.txt`, `src/A2A.Server.Redis/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Stores.Tests/ExecutionCoordinatorContract.cs`

**Interfaces:**
- Produces: `public virtual ValueTask<bool> ExecutionCoordinator.TryRegisterExclusiveAsync(TaskKey task, string executionId, CancellationToken cancellationToken = default)`. True: no unexpired lease of the task existed and this one is now held, valid for `LeaseTtl`. False: another unexpired lease exists and nothing changed.

- [ ] **Step 1: Add contract tests** to `ExecutionCoordinatorContract` (they run for in-memory and Redis), using `CreatePairAsync` and `PassAsync` like their neighbours:
  - `Exclusive_register_succeeds_on_a_free_task_and_counts_as_running`: true, then `IsRunningAsync` true on both A and B.
  - `Exclusive_register_is_refused_while_another_lease_runs`: `RegisterAsync` on A, then exclusive on B is false; after `UnregisterAsync` it is true. A refused call leaves no lease for its id: `HeartbeatAsync` with that id is false.
  - `Exclusive_register_ignores_an_expired_lease`: register, `PassAsync` past the TTL, exclusive is true.
  - `Exclusive_register_is_scoped_to_tenant_owner_and_task`: a lease on a key that differs only in tenant, only in owner, or only in task id does not refuse.
  - `Concurrent_exclusive_registers_have_one_winner`: 20 concurrent calls, half on A and half on B, distinct ids: exactly one true.
- [ ] **Step 2: Run them.** `dotnet test tests/A2A.Stores.Tests -f net10.0 --filter "FullyQualifiedName~Exclusive_register|FullyQualifiedName~Concurrent_exclusive"`. Expected: build FAILS (no such method).
- [ ] **Step 3: Implement.**
  - Base: virtual, not abstract. Body: `IsRunningAsync`, return false if running, else `RegisterAsync`, return true. XML docs say this default is not atomic and a coordinator shared by instances must override it.
  - In-memory: override under `_gate`, using the expiry test of `IsRunningAsync`.
  - Redis: new Lua constant `RegisterExclusiveScript = Now + "\n" + <loop over HVALS of KEYS[1]; any expiry > now returns 0> + "\n" + Lease`, evaluated with the `RegisterAsync` keys and args. Return `(int)result == 1`.
  - Add both `PublicAPI.Unshipped.txt` entries (the override in each project that has one).
- [ ] **Step 4: Run the tests again.** Expected: PASS, with Docker up.
- [ ] **Step 5: Commit** `feat: add exclusive execution lease to coordinators`.

### Task 2: ContinueTaskAsync

**Files:**
- Create: `src/A2A.Server/Execution/TaskExecutionEngine.Continuation.cs`, `src/A2A.Server/TaskContinuation.cs` (interface, context, status enum, result)
- Modify: `src/A2A.Server/Retention/A2ARetentionService.cs` (make `StillWaiting` and `Before` `internal`, nothing else), `src/A2A.Server/ServerLog.cs`, `src/A2A.Server/AgentExecutor.cs` (extend the `RequestMetadata` doc to name `ContinueAsync`), `src/A2A.Server/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/TaskContinuationTests.cs`

**Interfaces:**
- Consumes: `TryRegisterExclusiveAsync` from Task 1.
- Produces, with the exact shapes in the spec's API section: `IContinuableAgentExecutor`, `TaskContinuationContext`, `TaskContinuationStatus`, `TaskContinuationResult` (internal constructor), `TaskExecutionEngine.ContinueTaskAsync`.

- [ ] **Step 1: Write the failing tests** in `TaskContinuationTests.cs`. Use `TestAgentHost.StartAsync(services: ...)` to register a test agent implementing `IContinuableAgentExecutor` whose `ExecuteAsync` asks for input or returns WORKING, and whose `ContinueAsync` is driven per test. Assert through `App.Services.GetRequiredService<TaskExecutionEngine>()`, `TaskStore`, `EventStore`. Tests:
  - `Continuation_needs_no_request_and_no_push_config`: task in INPUT_REQUIRED, no push config; `ContinueTaskAsync` returns `Started`; after `Completion`, task is COMPLETED, `EventStore` holds the new status and artifact events, `PushOutbox` has no delivery.
  - `Continuation_reaches_a_live_subscriber`: `SubscribeToTaskAsync` open before the call; it gets WORKING, the artifact and COMPLETED, then ends.
  - `Continuation_reaches_a_resumed_stream`: stream resumed with `Last-Event-ID` at the sequence before the continuation gets the continuation's events.
  - `Continuation_runs_from_every_open_state`: theory over SUBMITTED, WORKING, INPUT_REQUIRED, AUTH_REQUIRED.
  - `Push_is_one_subscriber_of_a_continuation`: with a push config and a push-enabled card, the continuation's events queue deliveries (use the `PushDeliveryTests` setup).
  - `Agent_gets_a_copy_of_the_task_and_the_metadata`: changing `context.Task` does not change the store; `context.Metadata` and `updater.RequestMetadata` carry the caller's values.
  - `Other_owner_or_tenant_reads_as_not_found`: two cases, `TaskNotFound`, agent not run.
  - `Missing_task_is_not_found`.
  - `Terminal_task_is_refused_and_unchanged`: theory over the four terminal states if cheap, else COMPLETED; `TaskTerminal`, version and last sequence unchanged, agent not run.
  - `Overdue_interrupted_task_is_refused_and_left_for_the_sweep`: `FakeTimeProvider`, `InterruptedTimeout = 1h`, advance 2h: `InterruptedTimedOut`, state unchanged, agent not run. A sweep (as `RetentionTests.Sweeper`) then cancels it.
  - `Interrupted_task_within_its_timeout_continues`, and `No_interrupted_timeout_never_refuses` (`InterruptedTimeout = null`, advance a year).
  - `Running_execution_refuses_a_continuation`: gated `ExecuteAsync` still running: `AlreadyRunning`.
  - `Second_continuation_during_a_run_is_refused`: gated `ContinueAsync`; second call `AlreadyRunning`; first still finishes and its events are stored.
  - `Continuation_after_the_previous_one_ended_starts`: sequential runs both `Started`.
  - `Lease_held_during_the_run_and_released_after`: `ExecutionCoordinator.IsRunningAsync` true during a gated run, false after `Completion`, and `ListExpiredAsync` empty after advancing past the TTL (no leaked claim).
  - `Agent_that_cannot_continue_is_not_supported_and_task_unchanged`: default `ScenarioAgent`: `NotSupported`, record equal.
  - `Tenant_without_an_agent_is_not_supported`: a factory that returns `default` lease.
  - `Failing_factory_throws_and_task_unchanged`: the factory's exception reaches the caller.
  - `Coordinator_failure_starts_nothing`: `FaultyCoordinator` style: throw from `TryRegisterExclusiveAsync`, agent not run, task unchanged.
  - `Agent_failure_fails_the_task`: `ContinueAsync` throws: FAILED, "The agent failed to process the request.".
  - `CancelTask_stops_a_continuation`: gated run; `CancelTaskAsync` gives CANCELED, the agent's token fired.
  - `Events_after_the_task_ended_are_dropped`: gated run, task deleted through `A2ATaskAdmin.DeleteTaskAsync` or terminal by CancelTask with an agent that ignores its token and then writes: stored task and last sequence unchanged.
  - `Start_token_only_cancels_the_start`: cancelling the passed token after `Started` does not stop the run; an already-cancelled token throws `OperationCanceledException` and starts nothing.
  - `Invalid_key_throws`: null owner or empty task id: `ArgumentException` family.
  - `Run_span_has_kind_continue`: follow `TracingTests`: `a2a.execution.kind` is `continue`.
- [ ] **Step 2: Run them.** `dotnet test tests/A2A.Tests -f net10.0 --filter FullyQualifiedName~TaskContinuationTests`. Expected: build FAILS.
- [ ] **Step 3: Implement `src/A2A.Server/TaskContinuation.cs`** per the spec, with XML docs. `TaskContinuationResult.Completion` is `Task.CompletedTask` when refused. The remarks on `ContinueAsync` say: no token exchange, an exception fails the task, and the host must store the `TaskKey` (`new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId)`) when the task starts.
- [ ] **Step 4: Implement `ContinueTaskAsync`** in the new partial file, keeping this order:

```csharp
ArgumentNullException.ThrowIfNull(key.Tenant, nameof(key));
ArgumentNullException.ThrowIfNull(key.Owner, nameof(key));
ArgumentException.ThrowIfNullOrEmpty(key.TaskId, nameof(key));
if (Refusal(await _tasks.GetAsync(key, ct)) is { } early) return Refused(key, early);
if (RunningCompletions(key).Count > 0) return Refused(key, AlreadyRunning);
await using var agent = await RentAgentAsync(key.Tenant, null, ct);
agent.Error?.Throw();
if (agent.Executor is not IContinuableAgentExecutor) return Refused(key, NotSupported);
var claim = Guid.NewGuid().ToString("N");
if (!await _coordinator.TryRegisterExclusiveAsync(key, claim, ct)) return Refused(key, AlreadyRunning);
try
{
    // Read after the claim: the sweep and the other instances now see this task as running.
    var record = await _tasks.GetAsync(key, ct);
    if (Refusal(record) is { } late) return Refused(key, late);
    var pushEnabled = await PushEnabledForTenantAsync(key, ct);
    // context: new TaskContinuationContext { Key, Task = record!.Task.DeepCopy(), Metadata = copy of metadata }
    var exec = await StartRawAsync(key, record!.Task.ContextId, isNewTask: false, userMessage: null, push: null, emitInitialSnapshot: false,
        (executor, updater, token) => ((IContinuableAgentExecutor)executor).ContinueAsync(context, updater, token),
        agent, pushEnabled, requestMetadata: context.Metadata, timeLimit: _options.MaxTaskDuration, kind: ContinueKind);
    exec.Detach();
    return new TaskContinuationResult(TaskContinuationStatus.Started, exec.Completion);
}
finally { /* UnregisterAsync(key, claim, CancellationToken.None); on failure log event 241 and go on */ }
```

  `Refusal(TaskRecord?)`: null → `TaskNotFound`; terminal → `TaskTerminal`; `_options.InterruptedTimeout is { } t && A2ARetentionService.StillWaiting(record, A2ARetentionService.Before(_time.GetUtcNow(), t))` → `InterruptedTimedOut`; else null. `Refused` logs event 240 (Debug: task id and status). `ContinueKind = "continue"`. Add the public entries to `PublicAPI.Unshipped.txt`.
- [ ] **Step 5: Run the tests again.** Expected: PASS. Then `dotnet build A2A.slnx -c Release`: 0 warnings. Then full `dotnet test tests/A2A.Tests -f net10.0`: green, and `PackageRulesTests` included.
- [ ] **Step 6: Commit** `feat: continue a task with no inbound request`.

### Task 3: Across instances

**Files:**
- Test: `tests/A2A.Tests/TaskContinuationTests.cs` (two in-memory instances), `tests/A2A.Stores.Tests/MultiInstanceTests.cs`

**Interfaces:**
- Consumes: `TaskExecutionEngine.ContinueTaskAsync` and `IContinuableAgentExecutor` from Task 2.

- [ ] **Step 1: In-memory two-instance tests** in `TaskContinuationTests.cs`, following `RetentionTests.TwoInstancesAsync`, sharing stores, one coordinator and one `TaskEventBus` singleton:
  - `Continuation_on_A_reaches_a_stream_on_B`: task created on B, subscriber on B, continue on A.
  - `Continuation_runs_on_an_instance_that_never_saw_the_task`: task created on A, A disposed, continue on B.
  - `Every_instance_firing_gives_one_winner`: gated run, both instances call together, one `Started`, one `AlreadyRunning`.
  - `Execution_on_another_instance_refuses_a_continuation`: gated run on A, continue on B: `AlreadyRunning`.
  - `Dead_continuation_is_recovered_by_the_sweep`: use `DyingCoordinator`: A dies during a gated continuation of a WORKING task, advance past TTL, sweep on B gives FAILED "The agent stopped unexpectedly.".
  - `Claim_of_a_dead_instance_blocks_nothing_after_the_ttl`: `RegisterAsync` of a stray lease on the shared coordinator: `AlreadyRunning`; past the TTL: `Started`.
  - `Sweep_leaves_a_running_continuation_alone`: interrupted task whose continuation (started on A within its timeout, gated, writes nothing) passes the timeout: sweep on B does not cancel it.
- [ ] **Step 2: Add `MultiInstanceTests`** on PostgreSQL + Redis. Make `GatedAgent` implement `IContinuableAgentExecutor`, using the first word of the first history message like its neighbours (`continue <guid>`): `ExecuteAsync` starts work and returns with WORKING for that word; `ContinueAsync` signals `Started`, waits for `Gate(text + " continue")`, adds artifact `continued`, completes; for `observe-continue <guid>` it waits for its token and signals `Cancelled`.
  - `Continuation_on_one_instance_reaches_a_stream_on_another`: no push config (assert `PushConfigStore.ListAsync` is empty); B subscribes (`Collect`), A calls the engine; B's events include the artifact, its last event is COMPLETED; B's stored row is COMPLETED and `IsType<PostgresTaskStore>`.
  - `Only_one_instance_wins_a_continuation_race`: `Task.WhenAll` on A and B while gated: exactly one `Started`.
  - `CancelTask_on_one_instance_stops_a_continuation_on_another`.
- [ ] **Step 3: Run.** `dotnet test tests/A2A.Tests -f net10.0 --filter FullyQualifiedName~TaskContinuationTests` and `dotnet test tests/A2A.Stores.Tests -f net10.0 --filter "FullyQualifiedName~MultiInstanceTests"`. Expected: PASS. Run the new multi-instance tests 5 times to catch flakes.
- [ ] **Step 4: Commit** `test: continuation across instances`.

### Task 4: Docs

**Files:**
- Create: `docs/site/guides/proactive-continuation.md`, `docs/site/snippets/Continuation.cs`
- Modify: `docs/site/guides/toc.yml` (after Push notifications), `docs/site/guides/observability.md` (kinds table: `execute`, `cancel` or `continue`; id range 200-241), `docs/site/guides/retention.md` (the "without retention" list: continuations still honour `InterruptedTimeout`), `docs/site/guides/push-notifications.md` (one sentence with a link), `README.md`, `src/A2A.Server/README.md`, `CHANGELOG.md` (0.1.0 section), `docs/2026-11-07-a2a-gap-analysis.md` (D8 "This SDK" cell: `Done: TaskExecutionEngine.ContinueTaskAsync`, keeping the existing text)

- [ ] **Step 1: Write the guide.** Sections: when to use it; implement `IContinuableAgentExecutor` (snippet region `Agent`); keep the `TaskKey`; call it from a timer or callback (snippet regions `Timer`, `Callback`); table of the six statuses and what to do; rules (exclusive, interrupted timeout also with retention off, terminal, agent failure fails the task, no token exchange); security warning: no caller check, protect the callback endpoint. C# samples only through `[!code-csharp[](../snippets/Continuation.cs#Region)]`.
- [ ] **Step 2: Verify.** `dotnet build docs/site/snippets/A2A.DocSnippets.csproj -c Release` 0 warnings and `python3 docs/site/check-guides.py` passes.
- [ ] **Step 3: Commit** `docs: proactive task continuation`.
