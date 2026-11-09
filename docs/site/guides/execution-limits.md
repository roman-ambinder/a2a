# Execution limits and budgets

A burst of requests can start many agents at once and use up your LLM quota or your memory. A long task can spend without end. This guide covers the two guards against that: a limit on running agents, and a usage budget per task.

For size limits and the request rate limit, see [limits and rate limiting](limits-and-rate-limiting.md). For the time limit, see [task safety](task-safety.md).

## Limit running agents

[!code-csharp[](../snippets/ExecutionLimits.cs#Concurrency)]

| Option | Default | Meaning |
|---|---|---|
| <xref:A2A.Server.A2ALimits.MaxConcurrentExecutions> | 1024 | Most agents running on this instance. Zero or negative turns it off. |
| <xref:A2A.Server.A2ALimits.MaxConcurrentExecutionsPerTenant> | 0 (off) | Most agents running for one tenant on this instance. Zero or negative turns it off. |
| <xref:A2A.Server.A2ALimits.MaxQueuedExecutions> | 256 | Most requests that may wait for a free slot. Zero or negative means no queue. |
| <xref:A2A.Server.A2ALimits.MaxExecutionQueueWait> | 10 seconds | Longest one request waits. Zero, negative or about 24.8 days or more means no queue. A wait is never unbounded. |

The defaults are high, so a normal host does not reach them. Set them from what your LLM provider allows and what your agents use.

### What counts

- One running agent holds one slot, from the start of the run until the agent has returned and its events are stored.
- A task waiting in `INPUT_REQUIRED` or `AUTH_REQUIRED` has no agent running, so it holds no slot.
- `SendMessage`, `SendStreamingMessage` and <xref:A2A.Server.Execution.TaskExecutionEngine.ContinueTaskAsync*> need a slot.
- `CancelTask` never needs a slot and is never counted. You can always cancel, also when the server is full.
- A retry that only replays an earlier result needs no slot.

### When the server is full

A request that finds no free slot waits in a queue, in arrival order. A tenant at its own limit does not hold up other tenants. With a per-tenant limit, one tenant may queue at most that many requests, so it cannot fill the queue.

The request is refused when the queue is full, when the wait runs out, or when the host is stopping. The caller gets `RATE_LIMITED` (HTTP 429) with `Retry-After`, and `metadata.reason` is `execution_capacity`. Nothing is stored: there is no new task, a follow-up leaves its task as it was, and the same request can be sent again. The A2A client retries unary calls after this error. It does not retry streams.

`ContinueTaskAsync` waits in the same queue. When it is refused it does not throw: it returns <xref:A2A.Server.TaskContinuationStatus> `AtCapacity`.

### Several instances

**These limits are per instance, and the per-tenant limit is per tenant on that instance.** With 4 instances and `MaxConcurrentExecutions = 50`, up to 200 agents can run in total. There is no cluster-wide count: it would add a store call to every start, and a crashed instance would keep its slots until a lease expired.

For a cluster-wide cap, divide your quota by the number of instances, or enforce the quota at the LLM provider or gateway. When instances scale up and down, set the limit from the smallest quota share you are happy with.

## Set a budget per task

An agent reports what it spends. The server adds it up and stops the task when it goes past the budget.

[!code-csharp[](../snippets/ExecutionLimits.cs#Budget)]

| Option | Default | Meaning |
|---|---|---|
| <xref:A2A.Server.A2ALimits.MaxTaskTokens> | null (off) | Most tokens one task may use. Null, zero or negative turns it off. |
| <xref:A2A.Server.A2ALimits.MaxTaskCostUnits> | null (off) | Most cost units one task may use. The unit is yours: cents, credits, anything. Null, zero or negative turns it off. |

Report usage after each LLM call with <xref:A2A.Server.TaskUpdater.ReportUsageAsync*>:

[!code-csharp[](../snippets/ExecutionLimits.cs#Report)]

A host that wraps agents can report too: any code that has the <xref:A2A.Server.TaskUpdater> can call it. <xref:A2A.Server.TaskUpdater.UsedTokens> and <xref:A2A.Server.TaskUpdater.UsedCostUnits> give the task's total so far, for an agent that wants to wind down early.

### What happens past the budget

A budget is passed when the total is greater than the budget. Exactly at the budget is allowed.

The task moves to `FAILED` with the text "Task exceeded its budget". Streams and webhooks get it like any final event. The run's cancellation token fires, and the `ReportUsageAsync` call that went over throws `OperationCanceledException`, so an agent that ignores its token still stops. Let that exception go up.

This works like the time limit, with one difference: the time limit applies to one run, and each follow-up gets the full time again. **A budget applies to the whole task**, over all its runs and on every instance. A `CancelTask` run counts its usage but has no budget, so a cancel can always finish.

### Where the total is kept

The total is in the task's metadata, under `a2a.usage`:

```json
{ "a2a.usage": { "tokens": 1840, "costUnits": 0.42 } }
```

The server owns this key: an agent cannot write it. Callers can read it, like the other `a2a.*` metadata. If your costs are private, report units that mean nothing outside your system. No store changes are needed, so every `TaskStore` supports it.

### Limits of budgets

- The check runs after the money is spent. One report can go past the budget by its own size. Keep single calls small compared to the budget.
- Report before you set the task's status. Usage is saved with the run's next event, so a report after the run's last event is not saved.
- Two runs of one task at the same time can go past the budget by what the other reported. The total adds up both runs, with one exception: when one run ends the task, an event of the other that is not stored yet is dropped, with the usage it carried. The default <xref:A2A.Server.A2AServerOptions.FollowUpPolicy> runs one at a time.
- A task that ended with a direct reply has no stored total.
- One budget applies to every tenant. For a budget per tenant or per plan, check <xref:A2A.Server.TaskUpdater.UsedCostUnits> in your agent and call `FailAsync` yourself.

## Watch it

| Meter | Name | Kind | Tags |
|---|---|---|---|
| `A2A.Server` | `a2a.server.executions.active` | up-down counter | none |
| `A2A.Server` | `a2a.server.executions.queued` | up-down counter | none |
| `A2A.Server` | `a2a.server.executions.rejected` | counter | `a2a.rejection.reason` (`queue_full`, `queue_timeout`, `shutdown`) |
| `A2A.Server` | `a2a.server.tasks.budget_exceeded` | counter | none |

A queue that is rarely empty means the limit is too low for your load, or you need more instances. Rising `queue_timeout` rejections mean agents run longer than callers can wait.

## What can go wrong

- **Callers get 429 with `reason: execution_capacity`.** The instance is full. Raise the limits, add instances, or lower `MaxQueuedExecutions` and let callers retry.
- **A request hangs for a few seconds, then starts.** It waited in the queue. Set `MaxExecutionQueueWait` to zero to refuse at once.
- **Requests stay queued while agents look idle.** A slot is held until the agent returns. An agent that ignores its cancellation token or never returns keeps its slot. Set <xref:A2A.Server.A2AServerOptions.MaxTaskDuration>, and honour the token.
- **An agent that calls an agent on the same instance times out with 429.** The caller holds its slot while it waits for the callee. If every slot is held by callers, the callees queue until their wait ends. Keep the limit well above the depth of your agent calls, or run the callee elsewhere.
- **A budget seems not to apply.** The agent must call `ReportUsageAsync`. The server does not know what an agent spends.

## See also

- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Task safety](task-safety.md)
- [Multi-tenancy](multi-tenancy.md)
- [Observability](observability.md)
