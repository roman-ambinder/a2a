# ObservedAgent sample

An agent with OpenTelemetry tracing and metrics, and a token budget per task. A client calls it twice: once within
the budget, once past it. There is no collector: the sample keeps spans and metrics in memory and checks them. It
listens on `127.0.0.1` only and needs no keys.

| Shows | How |
|---|---|
| OpenTelemetry setup | `AddOpenTelemetry()` with `AddSource(A2ATelemetry.ServerSourceName, A2ATelemetry.ClientSourceName)` and `AddMeter(A2ATelemetry.ServerMeterName, "A2A")` |
| One trace from client to agent | the client sends `traceparent`, so the client span, the ASP.NET Core request span, the `SendMessage` span and the `a2a.agent.execute` span share one trace |
| A budget per task | `o.Limits.MaxTaskTokens = 2_000` and `o.Limits.MaxTaskCostUnits` |
| Reporting usage | the agent calls `updater.ReportUsageAsync(tokens, cost, ct)` after each model call |
| What happens past the budget | the task ends `FAILED` with "Task exceeded its budget", and `a2a.server.tasks.budget_exceeded` goes up by 1 |

The model is pretend: it charges 400 tokens per word of the prompt. "Say hello" costs 800 tokens. "Write a long essay
about tracing" costs 2400, which is past the budget of 2000.

A real host exports with `.AddOtlpExporter()` instead of `.AddInMemoryExporter(...)`. See the
[observability guide](../../docs/site/guides/observability.md) and the
[execution limits guide](../../docs/site/guides/execution-limits.md).

```bash
dotnet run --project samples/ObservedAgent
```

Expected output ends with:

```text
--- trace of the call past the budget
SendMessage [A2A.Client, Client] a2a.task.state=TASK_STATE_FAILED
  POST /a2a/ [Microsoft.AspNetCore, Server]
    SendMessage [A2A.Server, Internal] a2a.task.state=TASK_STATE_FAILED
      a2a.agent.execute [A2A.Server, Internal] a2a.task.state=TASK_STATE_FAILED error.type=BudgetExceeded

--- counters
a2a.server.requests: 2
a2a.server.tasks.budget_exceeded: 1

OK: the call was traced from client to agent run, and the task past its budget ended FAILED.
```

Anonymous access is on because the sample is loopback only. A real agent needs authentication and
`RequireAuthorization()` on `MapA2A`.
