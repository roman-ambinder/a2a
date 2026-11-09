# Execution limits and task budgets: design

Today `A2ALimits` caps streams, but nothing caps running agents. A burst starts unlimited executions and uses up LLM quota or memory. This adds (1) a concurrency limit with a bounded wait queue and (2) per-task usage budgets.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Scope across instances | **Limits are per instance.** The per-tenant limit is per tenant on this instance. N instances allow N × limit. | The coordinator has leases, not an atomic counter. A cluster semaphore adds a store round trip to every start, and a crashed holder would keep its slot until a lease expires. Hosts divide the quota by instance count, or gate at the LLM provider. |
| Budget scope | **Per task, across all runs and instances.** Stored in the task's metadata. | A budget that resets with each follow-up does not bound a long conversation. |
| Default limits | `MaxConcurrentExecutions` 1024, queue 256, wait 10 s, per-tenant off, budgets off. | High enough that normal hosts never reach it, and still stops a runaway burst. Matches "every limit is on by default". |
| Rejection | `RATE_LIMITED` (HTTP 429, `Retry-After`), `metadata.reason = "execution_capacity"`. | Same error as the stream limits. The client already retries it. Nothing is stored. |
| Past a budget | The task goes `FAILED` ("Task exceeded its budget") and the agent is stopped, as with the time limit. | One behaviour, already understood. |

## 1. Concurrency

New options on `A2ALimits`:

| Option | Default | Meaning |
|---|---|---|
| `MaxConcurrentExecutions` | 1024 | Running agents on this instance. Zero or negative: off. |
| `MaxConcurrentExecutionsPerTenant` | 0 (off) | Running agents per tenant on this instance. Zero or negative: off. |
| `MaxQueuedExecutions` | 256 | Starts that may wait for a slot. Zero or negative: no queue. With a per-tenant limit, one tenant may queue at most that limit, so one tenant cannot fill the queue. |
| `MaxExecutionQueueWait` | 10 s | Longest wait. Zero, negative or too large for a timer (`TimeLimits.IsTimeLimit`): no queue. Never an unbounded wait. |

`ExecutionAdmission` (internal, one per engine, one lock):

- A FIFO list of waiters, a total count, and per-tenant running and queued counts. A tenant's entry is removed at zero.
- Invariant: after every change, a pump grants every waiter it can, in order. So no waiter is ever eligible while waiting, and a new arrival may run at once when both the instance and its tenant have room, without jumping the queue.
- A waiter blocked only by its tenant's limit does not block other tenants.
- Grant, timeout and cancel decide under the lock. A cancel that loses to a grant returns the slot to the caller, who disposes it.
- The timeout uses the engine's `TimeProvider`.
- `Close()` (on engine shutdown and dispose) rejects all waiters and turns the queue off. A start with a free slot still proceeds, as today.
- Rejection reasons: `queue_full`, `queue_timeout`, `shutdown`.

Who is admitted:

- SendMessage and SendStreamingMessage: in `A2ARequestHandler.AcceptAsync`, before `PrepareAsync`. So it comes after the idempotency replay check (a replay takes no slot) and before anything is stored (a refused follow-up leaves no trace). The existing catch abandons the claim.
- `ContinueTaskAsync`: after the early refusals and the agent check, before the start claim (which has no heartbeat). When full it returns a new status, `TaskContinuationStatus.AtCapacity`.
- CancelTask runs are **never** gated and never counted. A cancel must get through most when the server is full.

The slot lives on `RentedAgent` (`AttachSlot`, `TakeSlot`, released by `DisposeAsync` if not taken), so every existing failure path already releases it. `StartRawAsync` moves it to the `Execution` next to `TakeLease`. `FinishAsync` releases it in a `finally`, after `_running.Remove` and before `MarkDone`. So a slot is held until the agent returned and every event is stored.

A queued request holds an idempotency claim, and claims are renewed only by a running execution's heartbeat. While waiting (only then), `AdmitAsync` renews the claim every `Coordination.Heartbeat`. A renew failure is logged.

## 2. Budgets

New `A2ALimits` options: `long? MaxTaskTokens`, `decimal? MaxTaskCostUnits`. Default null. Null, zero or negative: off.

New `TaskUpdater` members:

```csharp
public ValueTask ReportUsageAsync(long tokens, decimal costUnits = 0, CancellationToken cancellationToken = default);
public long UsedTokens { get; }          // the task's total, all runs
public decimal UsedCostUnits { get; }
```

- Negative values throw `ArgumentOutOfRangeException`. Sums saturate, they never wrap: a wrapped total would pass the check.
- "Past" means strictly greater than the budget. Usage exactly at the budget is allowed.
- A report after a terminal state is ignored.
- The report that crosses the budget queues FAILED (`TaskExecutionEngine.BudgetExceededText`), fires the token, then throws `OperationCanceledException`, so an agent that ignores its token still stops. It goes through the same `Execution` outcome compare-and-swap as the time limit (rename `TryMarkTimedOut` to `TryMarkStopped`), so the agent's late error or cancel is expected and swallowed.
- CancelTask runs count usage but have no budget, like the time limit.

Persistence, with no store change:

- Task metadata key `a2a.usage` = `{"tokens":n,"costUnits":x}`, server-owned (add it to the `MergeMetadata` skip list and the `TaskSize` skip list). An agent cannot write it. It is visible to callers, like the other `a2a.*` keys.
- `TaskUsageMeter` (internal, under one lock) is shared by the updater and the `Execution`. It holds the total (seeded from the stored task when a run starts, like `SeedArtifacts`) and the pending delta not yet stored.
- `ApplyAndStoreAsync` adds the pending delta to `next` after `ApplyToCopy`, inside the conflict-retry loop, so it is re-added on a fresh record. It marks the delta stored only after the commit succeeds. Any event of the run flushes it; no extra event is written for a report.
- The total on the record is what is summed, so two runs of one task never overwrite each other's usage.

Known limits, stated in the guide:

- Enforcement is at report time on this run's view (stored total at start, plus its own reports). Two runs of one task at once can overshoot by what the other reported. The default `FollowUpPolicy` allows one run at a time.
- A budget is checked after the spending. One report can overshoot by its own size.
- Usage reported after a run's last event is not stored. Report before you set the status.

## Metrics (meter `A2A.Server`)

| Name | Kind | Tags |
|---|---|---|
| `a2a.server.executions.active` | UpDownCounter | none |
| `a2a.server.executions.queued` | UpDownCounter | none |
| `a2a.server.executions.rejected` | Counter | `a2a.rejection.reason` |
| `a2a.server.tasks.budget_exceeded` | Counter | none |

No tenant tag: tenants are unbounded, and metric tags must stay low-cardinality.

## Not in this change

Per-tenant values for limits or budgets (one value for all tenants), and a cluster-wide limit. Both can be added later without breaking this API.
