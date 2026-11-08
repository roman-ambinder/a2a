# Proactive task continuation (gap D8)

Status: design, 2026-11-08. Closes D8 in `docs/2026-11-07-a2a-gap-analysis.md`.

## Gap check

The gap is real here. Nothing in `src` runs an agent on an existing task without an inbound message:

- `TaskUpdater` writes to a channel that closes when `ExecuteAsync` returns, so an agent cannot keep one for later.
- An agent that stays inside `ExecuteAsync` can go on writing, but it needs one live process for the whole wait, and it holds an execution lease all that time. It does not survive a restart and cannot move to another instance.
- `TaskExecutionEngine.CommitSystemStatusAsync` is internal and writes only status text, with no agent.
- `TaskStore.CommitAsync` is public, but it does not publish to the bus, take a lease or check state.

## Goal

An agent continues an existing task with no inbound client request and no push registration, for example from a timer or an external callback. The events go to the event store and the bus through the normal commit path, so subscribers and resumed streams see them. Push is one subscriber: its deliveries are queued only when the task has push configs and the tenant's card declares push.

## API

```csharp
// A2A.Server
public interface IContinuableAgentExecutor : IAgentExecutor
{
    Task ContinueAsync(TaskContinuationContext context, TaskUpdater updater, CancellationToken cancellationToken);
}

public sealed class TaskContinuationContext
{
    public required TaskKey Key { get; init; }
    public required AgentTask Task { get; init; }                 // the executor's own copy
    public Dictionary<string, JsonElement>? Metadata { get; init; }  // from the caller; also TaskUpdater.RequestMetadata
}

public enum TaskContinuationStatus { Started, TaskNotFound, TaskTerminal, InterruptedTimedOut, AlreadyRunning, NotSupported }

public sealed class TaskContinuationResult
{
    public TaskContinuationStatus Status { get; }
    public bool Started { get; }       // Status == Started
    public Task Completion { get; }    // agent returned and every event stored; already complete when refused; never faults
}

// A2A.Server.Execution
public sealed partial class TaskExecutionEngine
{
    public ValueTask<TaskContinuationResult> ContinueTaskAsync(TaskKey key, Dictionary<string, JsonElement>? metadata = null,
        CancellationToken cancellationToken = default);
}

// A2A.Server.Coordination
public abstract class ExecutionCoordinator
{
    public virtual ValueTask<bool> TryRegisterExclusiveAsync(TaskKey task, string executionId, CancellationToken cancellationToken = default);
}
```

`cancellationToken` cancels only the attempt to start. A started run stops through CancelTask, its time limit or shutdown, like any execution.

## Flow

1. Read the task. Refuse with no lease and no agent: `TaskNotFound`, `TaskTerminal`, `InterruptedTimedOut`.
2. An execution of the task running on this instance gives `AlreadyRunning`. This covers one whose register failed, which the coordinator cannot see.
3. Rent the tenant's agent. A factory that throws rethrows to the caller. No agent, or an agent that is not `IContinuableAgentExecutor`, gives `NotSupported`. The task is not touched in any of these cases.
4. Resolve the tenant's push capability, before the claim: the claim has no heartbeat, so nothing slow may run under it.
5. Take an exclusive lease under a fresh id with the `claim:` prefix, using `TryRegisterExclusiveAsync`. False gives `AlreadyRunning`. A coordinator failure throws to the caller and starts nothing.
6. Read the task again, now that every instance and the sweep see a running execution, and apply the same refusals as step 1.
7. Start a normal execution (`StartRawAsync`, kind `continue`, `isNewTask: false`, no snapshot, `MaxTaskDuration` applies). It registers its own lease and heartbeat. Detach its outgoing channel, since no request reads it.
8. Read the task once more. If it is gone or terminal, cancel the run. A CancelTask, delete or sweep that landed between step 6 and the run's registration found nothing to cancel, and this read catches it. Any later one finds the run registered. A failed read is logged, because the run has started.
9. Release the claim, on every path out of step 5, also when the claim call itself threw: a client timeout can throw after the coordinator ran the claim.

A claim is not an execution. A claim left behind (a failed release, an instance that died in steps 5 to 7) expires. The sweep removes an expired lease with the `claim:` prefix and never looks at its task, so it cannot fail a healthy task parked in WORKING.

No change to the event apply path (`ConsumeAsync`, `ApplyAndStoreAsync`, `ApplyToCopy`) or to `StartRawAsync`.

## Rules honoured

| Rule | How |
|---|---|
| Ownership and tenant | `TaskKey` is tenant + owner + id. A wrong owner or tenant reads as `TaskNotFound`, like every other cross-owner read. |
| Leases across instances | The claim is atomic in the coordinator. The run is a normal execution: lease, heartbeat, cross-instance cancel, dead-instance recovery. |
| Terminal immutability | Refused before start. After start, `ApplyToCopy` drops events on a terminal task and the version check guards the commit. |
| Interrupted TTL | An overdue interrupted task is refused, using the sweep's own test (`StillWaiting`), so the result does not depend on when the sweep last ran. The sweep sees a running continuation as running and leaves it alone. |

## Decisions

- **Separate interface, not a default method on `IAgentExecutor`.** A default that throws would fail the task through the executor error path, and a no-op default would report `Started` for nothing. A type check refuses before anything changes.
- **Service on the engine, not a new class.** The engine is already in DI, and this is an execution start.
- **Refusals are results, not exceptions.** A timer that fires after the client cancelled is routine. Exceptions are for store, coordinator and factory failures.
- **Exclusive at the start only.** A continuation does not start while another continuation or any registered execution of the task runs. A timer usually fires on every replica. The caller retries on `AlreadyRunning` if it must. Inbound follow-ups and CancelTask keep their existing non-exclusive start, so a client reply can start `ExecuteAsync` next to a running `ContinueAsync`; the commit path keeps the events ordered, and the agent must allow for it.
- **Claim id, then the execution's own lease.** This leaves `StartRawAsync` alone, which matters because another session is editing the event path.
- **Virtual base method with a non-atomic default.** Custom coordinators keep compiling. The in-memory and Redis coordinators override it atomically. The XML docs say a custom one should too.
- **Interrupted TTL applies with retention off too.** `InterruptedTimeout` is the allowed wait of a task. Set it to null to turn it off. The docs say so.
- **An agent failure in a continuation fails the task**, the same as `ExecuteAsync`. A refused continuation never does.
- **No token exchange.** A continuation has no inbound bearer token, so the context has no `ExchangeTokenAsync`.
- **No caller check.** Like `A2ATaskAdmin`, this is a server-side API. A host that exposes a callback must authenticate it and map it to a `TaskKey`.

## Accepted limits

- **Sweep race after the last read.** A sweep on another instance may pass its running check before the claim and commit after step 8. The task ends, which is what the sweep asks for, and the agent's events are dropped. The agent is not told until its lease is next lost, so it may run to its end.
- **Deduplication of callbacks and timers is the host's job.** `AlreadyRunning` covers a duplicate during a run only. A replica that fires just after a fast run ended, on a task left open, gets a second `Started`.

## Tests

- Single instance, `tests/A2A.Tests/TaskContinuationTests.cs`: every status, events in store and bus, resume, push as one subscriber, CANCEL during a run, agent failure, lease held during a run.
- Two instances on shared in-memory stores, same file: a continuation on A reaches a stream on B, the race has one winner, and a dead continuation is recovered.
- Multi-instance, `tests/A2A.Stores.Tests/MultiInstanceTests.cs` on PostgreSQL and Redis: a continuation on A reaches a stream on B with no push config, the race has one winner, CancelTask on B stops it on A.
- `TryRegisterExclusiveAsync` in the coordinator contract tests.
