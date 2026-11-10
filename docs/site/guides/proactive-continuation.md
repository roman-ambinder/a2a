# Proactive continuation

Some tasks finish later, with no client request to trigger the work. A report is ready after an hour. A payment provider calls your webhook. A timer fires. Proactive continuation lets your host run the agent on an existing task in these cases.

The events of a continuation take the normal path. They go to the event store and to stream subscribers. They also go to push when the task has push configs and the card declares push. The task does not need a push registration. See [Push notifications](push-notifications.md).

## Implement IContinuableAgentExecutor

<xref:A2A.Server.IContinuableAgentExecutor> extends `IAgentExecutor` with one method, `ContinueAsync`. It gets a <xref:A2A.Server.TaskContinuationContext> and a `TaskUpdater`. Report progress through the updater, as in `ExecuteAsync`.

[!code-csharp[](../../../samples/Snippets/Continuation.cs#Agent)]

The context holds `Key`, a copy of the stored `Task`, and the `Metadata` the caller passed. Changing the copy does not change the stored task.

## Keep the TaskKey

The host needs a <xref:A2A.Server.Storage.TaskKey> to continue a task. It holds tenant, owner and task id. Store it when the task starts. In `ExecuteAsync`, build it from `context.Call.Tenant`, `context.Call.Principal` and `context.TaskId`. In `ContinueAsync`, use `context.Key`.

Keep the key in durable storage if the continuation may come after a restart.

## Start a continuation

Call <xref:A2A.Server.Execution.TaskExecutionEngine.ContinueTaskAsync*>. <xref:A2A.Server.Execution.TaskExecutionEngine> is registered by `AddA2AServer`. Inject it. The method returns a <xref:A2A.Server.TaskContinuationResult> with a `Status`, a `Started` flag and a `Completion` task. `Completion` ends when the agent returned and every event is stored. It never faults. For a refused request it is already complete.

A runnable version is in [samples/MemoryAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/MemoryAgent/README.md). Its host continues a report task and awaits `Completion`.

From a timer:

[!code-csharp[](../../../samples/Snippets/Continuation.cs#Timer)]

From a callback:

[!code-csharp[](../../../samples/Snippets/Continuation.cs#Callback)]

The `cancellationToken` only cancels the attempt to start. A started run does not stop with it.

## Security

**The API does not check the caller.** `ContinueTaskAsync` trusts the `TaskKey` it gets. A host that exposes a callback must authenticate the caller and protect that endpoint. Use a policy of your own, for example one named `A2AContinuation` as in the sample. The `Callback` sample trusts its route values only because that policy limits callers to your own backend. Do not map it inside the group that `MapA2A` returns. Map the caller to a `TaskKey` yourself. Do not take tenant and owner from untrusted input. See [Auth and ownership](auth-and-ownership.md).

## Statuses

<xref:A2A.Server.TaskContinuationStatus>:

| Status | Meaning | What to do |
|---|---|---|
| `Started` | The agent was started. | Nothing. Await `Completion` if you need the end. |
| `TaskNotFound` | The task does not exist, or belongs to another owner or tenant. | Drop the key. |
| `TaskTerminal` | The task is completed, failed, canceled or rejected. | Drop the key. |
| `InterruptedTimedOut` | The task waited for input longer than `InterruptedTimeout`. | Drop the key. The retention sweep cancels the task when retention is on. |
| `AlreadyRunning` | An execution of the task runs, or another continuation is starting. | Try again later, or ignore it. |
| `NotSupported` | The tenant has no agent, or its agent is not an `IContinuableAgentExecutor`. | Fix the registration. |

A refused request changes nothing: no run, no event. It is logged as event 240 (Debug).

Exceptions are for an empty task id or a null tenant or owner (`ArgumentException`; an empty tenant is the default tenant), a failing agent factory, a failing store or coordinator, and a cancelled token.

## Rules

- **Exclusive.** A continuation does not start while another continuation or a registered execution of the task runs, on any instance. A client reply or CancelTask can still start next to a running continuation, so your agent must allow for that. `AlreadyRunning` covers duplicates during a run only: a replica that fires just after a fast run ended, on a task left open, gets a second `Started`. Deduplicate in the host. With several instances, use `AddRedisEventBus`, which brings the Redis coordinator. See [Durable stores](durable-stores.md). The in-memory coordinator is local to one process.
- **Interrupted timeout.** A task in `INPUT_REQUIRED` or `AUTH_REQUIRED` that waited longer than `InterruptedTimeout` gives `InterruptedTimedOut`. This holds with retention off. See [Retention](retention.md).
- **Terminal.** A finished task cannot be continued.
- **A normal run.** It holds a lease, is stopped by CancelTask, and ends after `MaxTaskDuration`.
- **Failure.** An agent that throws fails the task, as in `ExecuteAsync`.
- **No token exchange.** There is no inbound request, so no bearer token. The context has no `ExchangeTokenAsync`. Get your own credentials for downstream calls.

A failed release of the start claim is logged as event 241 (Warning). The claim expires, and the sweep removes it without touching the task. A failed re-read after the start is logged as event 242 (Warning); the run goes on.

## See also

- [Push notifications](push-notifications.md)
- [Retention](retention.md)
- [Durable stores](durable-stores.md)
- [Observability](observability.md)
- [Auth and ownership](auth-and-ownership.md)
