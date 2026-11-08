# Retention

Retention is a background sweep. It deletes old tasks, removes unused contexts, times out tasks that wait for input, and fails tasks whose instance died. You need it with any durable task store. It is off by default.

## Turn it on

[!code-csharp[](../snippets/Retention.cs#Register)]

<xref:A2A.Server.A2AServerServiceCollectionExtensions.AddA2ARetention*> adds the sweep as a hosted service. Call it on every instance; only one instance sweeps per interval. The options are checked when the service starts, and an invalid value throws `InvalidOperationException`. A second call only applies its `configure` as well.

## What is not done without it

Without `AddA2ARetention`:

- terminal tasks and their data are never deleted,
- `A2AServerOptions.InterruptedTimeout` does nothing, so tasks waiting for input never time out. It still decides whether a [continuation](proactive-continuation.md) may start, even with no sweep,
- tasks whose instance died stay `WORKING`, because nobody recovers them.

With a durable task store and no retention, startup logs a warning (event 253) that says this. If you clean up yourself, ignore it.

## Options

<xref:A2A.Server.Retention.A2ARetentionOptions>:

| Option | Default | Meaning |
|---|---|---|
| `TaskRetention` | 7 days | How long a terminal task is kept, counted from its status timestamp. |
| `OrphanContextRetention` | 7 days | How long a context that no task uses is kept, counted from its creation. |
| `SweepInterval` | 5 minutes | How often a sweep runs. At most 49 days. |
| `BatchSize` | 500 | Most items one step reads at once. 1 to 10,000. |

All time values must be greater than zero. <xref:A2A.Server.A2AServerOptions.InterruptedTimeout> is a server option, not a retention option. It defaults to 24 hours; null turns the timeout off; a set value must be positive.

The first sweep runs after one `SweepInterval`, not at startup.

## What a sweep does

A sweep runs four steps in this order. A failing step or item is logged as a warning (events 230 to 232), the other steps go on, and the next sweep tries again. Every change is idempotent.

1. **Dead executions.** An execution whose lease expired (its instance died) belongs to a task that is still `SUBMITTED` or `WORKING`. The sweep marks the task `FAILED` with the text "The agent stopped unexpectedly." and asks every instance to cancel it. It leaves the task alone when another execution of it still runs, when the task changed since it was read, or when it changed within the last `Coordination.LeaseTtl`. An execution that lapsed on the sweeping instance itself is not failed; it registers its lease again at its next heartbeat. An interrupted task is left to the next step. See [Durable stores](durable-stores.md#dead-executions).
2. **Interrupted tasks.** A task in `INPUT_REQUIRED` or `AUTH_REQUIRED` for longer than `InterruptedTimeout` is cancelled with the text "The task waited too long for input." This happens only if no agent runs it and nothing updated it since, such as a committed reply. It can be up to one sweep interval late. Skipped when `InterruptedTimeout` is null.
3. **Expired tasks.** A task in a terminal state (completed, failed, canceled, rejected) older than `TaskRetention` is deleted. Its events, push configs, pending push deliveries and dead letters go with it.
4. **Orphan contexts.** A context older than `OrphanContextRetention` that no task refers to is deleted. A context outlives its tasks until this step removes it.

Status changes in steps 1 and 2 go through the normal commit path. Streams and webhooks see them like any other final event.

Each step reads `BatchSize` items at a time. An item a step skips stays listed, and it does not hold back the items behind it.

### Not part of the sweep

- Store cleanup. The Postgres stores delete expired event logs and finished idempotency entries on their own timer (`CleanupInterval`).
- The replay window of resumed streams. That is `StreamingOptions.TerminalRetention` and `ReplayBufferSize`. See [Streaming](streaming.md).

## One sweep per cluster

Before each sweep the service takes a named lease, `a2a:retention`, from the <xref:A2A.Server.Coordination.ExecutionCoordinator> for one `SweepInterval`. An instance that does not get it skips that sweep. With the Redis coordinator the lease is shared by all instances. The in-memory coordinator is process-local, so with it every instance sweeps. Overlapping sweeps do no harm.

## Delete one task: A2ATaskAdmin

<xref:A2A.Server.Retention.A2ATaskAdmin> deletes a task with all its data on demand. `AddA2AServer` registers it. It is not exposed over A2A.

[!code-csharp[](../snippets/Retention.cs#Admin)]

`DeleteTaskAsync(TaskKey)` first cancels the task's executions on every instance, without waiting for them to stop. Then it deletes pending deliveries, dead letters, push configs and the event log, and the task row last, so a crash part way leaves a task that the next call or sweep finishes. The context stays until the sweep removes it as an orphan. It returns false when the task does not exist for that tenant and owner.

The <xref:A2A.Server.Storage.TaskKey> holds tenant, owner and task id. The caller of your endpoint supplies them, so decide who may.

## Protect admin endpoints

`A2ATaskAdmin` and `A2APushAdmin` (dead letters, see [Push notifications](push-notifications.md)) are plain services. They check nothing about the caller. Anything that exposes them must be protected by you:

- Use a separate authorization policy from the one on your A2A endpoints, as `A2AAdmin` above.
- Do not map admin routes inside the group that `MapA2A` returns.
- Keep them off the public listener if you can.

## What can go wrong

- **Retention is off.** Data grows. Look for warning 253.
- **Options out of range.** The service fails at startup with `InvalidOperationException`.
- **A task is not failed at once.** The sweep waits `LeaseTtl` after its last change on purpose.
- **Skewed clocks.** Task timestamps come from the instance that wrote them. Skew shifts when retention and `InterruptedTimeout` act on a task by that much.

## See also

- [Durable stores](durable-stores.md)
- [Task safety](task-safety.md)
- [Push notifications](push-notifications.md)
- [Auth and ownership](auth-and-ownership.md)
