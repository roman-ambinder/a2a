# Observability

The SDK traces with `System.Diagnostics.ActivitySource` and counts with `System.Diagnostics.Metrics.Meter`. The core packages need no OpenTelemetry package. Use this guide to wire the SDK to OpenTelemetry (or any listener), and to know which spans, tags and counters exist. All names are constants on <xref:A2A.Diagnostics.A2ATelemetry>.

Spans and counters cost almost nothing while nobody listens: no activity and no tag array is created.

## Set up

On the server, listen to the `A2A.Server` source and to the two meters:

[!code-csharp[](../../../samples/Snippets/Observability.cs#ServerOpenTelemetry)]

On a client process, listen to `A2A.Client`:

[!code-csharp[](../../../samples/Snippets/Observability.cs#ClientOpenTelemetry)]

| Name | Constant | Used for |
|---|---|---|
| `A2A.Server` | <xref:A2A.Diagnostics.A2ATelemetry.ServerSourceName> | server spans |
| `A2A.Client` | <xref:A2A.Diagnostics.A2ATelemetry.ClientSourceName> | client spans |
| `A2A.Server` | <xref:A2A.Diagnostics.A2ATelemetry.ServerMeterName> | server counters |
| `A2A` | none | card, idempotency, registry and v0.3 compatibility counters (see Metrics) |

A runnable version is in [samples/ObservedAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/ObservedAgent/README.md). It keeps spans and metrics in memory instead of exporting them, and prints one trace from the client to the agent run.

## Spans

| Source | Span | Kind | When |
|---|---|---|---|
| `A2A.Server` | `{method}` (`SendMessage`, `GetTask`, ...) | Server; Internal when it runs under another local span such as ASP.NET Core's request span | every operation, on all three bindings |
| `A2A.Server` | `a2a.agent.execute` | Internal | one agent run (execute, cancel or continue); child of the operation span |
| `A2A.Server` | `a2a.push.deliver` | Client | one attempt to send one event to one webhook |
| `A2A.Client` | `{method}` | Client | every `A2AClient` operation, with its retries and stream reconnects |

The operation span is the outermost step of the operation pipeline. It wraps the host filters and authorization, so a refused or unauthenticated call still has a span with its error. Extension operations use their method name too.

The delivery span continues the trace of the agent run that produced the event. The trace context is stored with the delivery, so a retry or another instance stays in the same trace.

## Propagation

- The client transports send the current span as W3C `traceparent` and `tracestate` on JSON-RPC, HTTP+JSON and gRPC (as metadata).
- The server reads both headers on all three bindings into `ServerCallContext.TraceParent`. Client and server spans then share one trace.
- A webhook POST carries the `traceparent` of its delivery span.
- A `traceparent` you set in `A2ARequestOptions.Headers` replaces the one the SDK wrote.
- With `A2AClientOptions.PropagateTraceInMetadata` (default false), the client also writes the send's span into the message metadata, under the keys `traceparent` and `tracestate`. Use it when a proxy or gateway between client and agent drops the headers. It applies to `SendMessage` and `SendStreamingMessage`, and a `traceparent` already in the metadata is kept.
- The server uses the metadata only when the request carries no `traceparent` header: the header wins. The idempotency fingerprint ignores both keys, because every attempt of a retry runs in its own span.
- The metadata is part of the message, so it is stored in the task history. Everyone who can read the task sees the trace and span ids. The helpers are `A2ATelemetry.InjectTraceContext(message, activity)` and `A2ATelemetry.TryExtractTraceContext(message, out context)`.

## Streams

One span covers a whole stream. Each event is a span event named `a2a.stream.event`, not a child span. Its tags are `a2a.event.kind` (`task`, `message`, `status_update` or `artifact_update`), `a2a.event.sequence` (server only) and `a2a.task.state` (status updates, and tasks that have a status).

A client reconnect adds an `a2a.retry` span event with `a2a.retry.attempt`. A failed webhook attempt that will be tried again adds an `a2a.retry` event, without tags, to its delivery span.

## Tags

| Tag | On | Notes |
|---|---|---|
| `a2a.method.name` | operation spans | the operation name (for example `SendMessage`) |
| `a2a.binding` | operation spans | `JSONRPC`, `HTTP+JSON` or `GRPC` |
| `a2a.task.id` | operation, run and delivery spans | when known |
| `gen_ai.conversation.id` | operation and run spans | the context id, when known |
| `a2a.message.id` | `SendMessage` and `SendStreamingMessage` spans | the id of the message sent |
| `a2a.message.reference_task_ids` | `SendMessage` and `SendStreamingMessage` spans | string array, only when the message refers to tasks |
| `a2a.protocol.version` | operation spans | the protocol version of the call, for example `1.0`. On the client it comes from `A2AClientOptions.ProtocolVersion`. On the server it is set when the caller's version is known |
| `gen_ai.agent.name`, `gen_ai.agent.description`, `gen_ai.agent.version` | operation spans | from the Agent Card, each only when not empty. On the client, only when the client has an Agent Card. On the server, when the operation resolved the tenant's card, so a call refused before that has none |
| `a2a.tenant` | all server spans, and client operation spans | only for a non-default tenant. On the client, when the request has a tenant |
| `a2a.task.state` | operation and run spans | the task state after the call or run, as its proto name (`TASK_STATE_COMPLETED`). A stream span has it only when the stream saw one state |
| `a2a.stream.event_count` | stream spans | events sent or received |
| `a2a.execution.kind` | run spans | `execute`, `cancel` or `continue` |
| `a2a.execution.timed_out` | run spans | `true` when `MaxTaskDuration` ended the run |
| `a2a.push.config.id`, `a2a.push.sequence`, `a2a.push.attempts` | delivery spans | |
| `server.address` | delivery spans | the webhook host only |
| `http.response.status_code` | delivery spans | the webhook's answer |

## Errors

A failed operation gets status `Error`, the `error.type` tag and, for A2A errors, `a2a.error.code` (the <xref:A2A.A2AErrorCode> name, for example `TaskNotFound`). For an A2A error, `error.type` is the A2A reason (`TASK_NOT_FOUND`). For a non-A2A exception, it is the exception type name.

A run span is an error when the agent throws, when it hits <xref:A2A.Server.A2AServerOptions.MaxTaskDuration> (`error.type` is `TimeLimit`), when it goes past its usage budget (`error.type` is `BudgetExceeded`; see [Execution limits](execution-limits.md)), or when it leaves the task `FAILED` (`error.type` is `TaskFailed`). A cancellation the caller asked for sets `a2a.canceled` to `true` and is not an error.

## Health checks

`services.AddHealthChecks().AddA2AHealthChecks()` adds four checks, all tagged `a2a`: `a2a_task_store` and `a2a_coordinator` each do one cheap read, `a2a_push_outbox` reports the age of the oldest due delivery and the dead letter count, and `a2a_shutdown` turns Unhealthy when `ApplicationStopping` fires, while running tasks drain. Everything except the push outbox check also has the `ready` tag, so one dead webhook does not take every instance out of the load balancer. The shutdown check cannot help for long: the server stops accepting connections soon after, so on Kubernetes still add a preStop delay if you want probes to see it.

Set the thresholds on `A2AHealthCheckOptions`: `PushBacklogDegradedAge`, `PushBacklogUnhealthyAge`, `DeadLetterDegradedCount` and `DeadLetterUnhealthyCount` (null turns one off), `Timeout` per check, and `IncludePushOutbox`. The push check needs a `PushOutbox` that implements `IPushOutboxStatistics` (the in-memory and PostgreSQL outboxes do); the host fails at startup if it does not. The output holds no connection details, tenant, task id or URL: a failure shows only the exception type, and the full exception goes to the log.

## No content

Tags never hold message text, parts, metadata, tokens, credentials, webhook URLs or exception messages. Ids and outcomes only. The `gen_ai.agent.*` fields come from the public Agent Card.

## OpenTelemetry semantic conventions

The SDK follows the A2A page of `open-telemetry/semantic-conventions-genai` (`docs/gen-ai/a2a.md`, commit `da2b5736d7a3`). Its status is Development, so names can change before it is stable.

The SDK emits no `gen_ai.operation.name` and no `invoke_agent` span. The convention says A2A instrumentation reports A2A requests only. The agent framework reports the agent run.

Differences from the convention:

- The operation span is a separate span under the ASP.NET Core request span. It does not enrich that span.
- `server.address` and `client.address` come from HTTP or gRPC transport instrumentation when it is on.

Some names have no convention, so they stay: the span names `a2a.agent.execute` and `a2a.push.deliver`, the `a2a.*` counters, and the tags `a2a.binding` and `a2a.error.code`.

## Metrics

All counters are `Counter<long>`.

| Meter | Counter | Tags | Counts |
|---|---|---|---|
| `A2A.Server` | `a2a.server.requests` | `a2a.method.name`, `a2a.outcome` (`ok`, `canceled`, `error`), `error.type` on errors | operations handled |
| `A2A.Server` | `a2a.server.events` | none | task events stored |
| `A2A.Server` | `a2a.server.streams.opened` | none | streams admitted (`SendStreamingMessage`, `SubscribeToTask`) |
| `A2A.Server` | `a2a.server.push.deliveries` | `a2a.outcome` (`sent`, `retried`, `dead_lettered`) | one per delivery attempt, counted when its result is recorded |
| `A2A.Server` | `a2a.server.push.dead_letters` | none | deliveries that failed for good |
| `A2A.Server` | `a2a.server.executions.active` | none | agents holding an execution slot on this instance (up-down counter). See [Execution limits](execution-limits.md) |
| `A2A.Server` | `a2a.server.executions.queued` | none | requests waiting for an execution slot (up-down counter) |
| `A2A.Server` | `a2a.server.executions.rejected` | `a2a.rejection.reason` (`queue_full`, `queue_timeout`, `shutdown`) | requests refused for lack of an execution slot |
| `A2A.Server` | `a2a.server.tasks.budget_exceeded` | none | tasks failed for going past their budget |
| `A2A.Server` | `a2a.server.security.events` | `a2a.security.event` (`unauthenticated`, `permission_denied`, `context_denied`, `webhook_refused`, `idempotency_key_mismatch`), `a2a.method.name` when known | security events (spec 13.4). See [Security events](#security-events) |
| `A2A.Server` | `a2a.server.artifact.objects.written` | none | objects written to the artifact object store |
| `A2A.Server` | `a2a.server.artifact.bytes.written` | none | bytes written to the artifact object store |
| `A2A.Server` | `a2a.server.artifact.bytes.served` | none | artifact bytes the server streamed to callers (redirects are not counted) |
| `A2A.Server` | `a2a.server.artifact.orphans.deleted` | none | orphan objects the retention sweep deleted. See [Artifact storage](artifact-storage.md) |
| `A2A` | `a2a.idempotency.evictions` | none | idempotency entries dropped early because the in-memory store was full |
| `A2A` | `a2a.client.endpoint.refused` | `binding` | Agent Card endpoints the client's endpoint policy refused. See [Task safety](task-safety.md) |
| `A2A` | `a2a.card.changed` | `origin` | cards that differ from the pinned card of their origin |
| `A2A` | `a2a.card.unsigned` | `origin` | unsigned cards accepted because signatures are not required |
| `A2A` | `a2a.registry.name_lookalike` | `origin` | agent names that look like the name of another registered agent. See [Peer identity and registry](peer-identity.md) |
| `A2A` | `a2a.client.circuit.state_changes` | `gen_ai.agent.name` (when known), `a2a.circuit.state` (`closed`, `open`, `half_open`) | circuit breaker state changes of a client. See [Client resilience](client-resilience.md) |
| `A2A` | `a2a.compat.v0_3.dropped` | `field` (for example `part.filename`) | values dropped because v0.3 cannot carry them. See [v0.3 compatibility](v03-compatibility.md) |
| `A2A` | `a2a.compat.v0_3.requests` | `binding`, `operation` | requests served on the v0.3 surface. See [v0.3 compatibility](v03-compatibility.md) |

A stream whose reader stops early counts as `canceled`. A cancellation that the caller did not ask for, such as a server-side timeout, counts as `error`.

The push counters are also exposed as `PushDeliveryService.DeliveriesCounterName` and `PushDeliveryService.DeadLettersCounterName`. The idempotency counter is `InMemoryIdempotencyStore.EvictionsCounterName` on `InMemoryIdempotencyStore.MeterName`.

The card counters come from the client's `A2ACardResolver`, so they exist in client processes. `a2a.registry.name_lookalike` comes from `AgentRegistry`, also on the client side. The v0.3 counters come from the v0.3 compatibility packages. The meter `A2A` therefore needs `AddMeter("A2A")` in any process that resolves cards, keeps an agent registry, serves or converts v0.3, runs the in-memory idempotency store, or uses a circuit breaker.

## Logs

The SDK logs through `ILogger` with fixed event ids. Use them in log filters and alerts.

| Ids | Area |
|---|---|
| 100-101 | client: card signing and pinning |
| 150-152 | client: agent registry (lookalike name, mixed-script name, identity change) |
| 200-242 | server: agent runs, coordination, push-config cleanup, retention |
| 250-253, 255 | server: startup diagnostics (254 is not one: it is a task past its size cap) |
| 300-313 | server: push delivery |
| 400-402 | server: idempotency |
| 1200-1201 | server: security events (1200 information, 1201 warning) |
| 500-501, 600 | ASP.NET Core: unhandled errors, stream errors |
| 700-701, 800 | ASP.NET Core: startup validation, card signing warning |
| 900 | gRPC: unhandled errors |
| 1000-1001 | Postgres cleanup; v0.3 compatibility (field dropped, card conversion problem) |
| 1100-1104 | Redis event bus and coordinator; 1100 is also used by the v0.3 server surface (error after a response started) |

### Security events

Each security event counts `a2a.server.security.events` and writes one log line (category `A2A.Server.Security`) with the event, the operation, the tenant, the resource id (a task or context id, or none) and the principal hash. The hash is the first 16 hex characters of the SHA-256 of the principal id, so you can tell callers apart without storing who they are. The line never has the principal, a token or a webhook URL. `unauthenticated` logs at Information, the others at Warning.

| Event | When |
|---|---|
| `unauthenticated` | Anonymous caller and `AllowAnonymous` is off. |
| `permission_denied` | The authorization handler denied the call, also on an open stream at re-authorization. Counted once, even when the caller sees `TaskNotFound`. |
| `context_denied` | The caller named a context of another principal (and `ContextAccess` did not allow it). |
| `webhook_refused` | A webhook URL was refused when its config was created, or when a delivery was attempted. |
| `idempotency_key_mismatch` | An idempotency key was reused with a different request. |

**What is not counted.** A caller who reads another caller's task gets `TaskNotFound`. The store lookup is scoped by owner, so the server cannot tell that case from a task that does not exist, and the SDK does not add a lookup to find out. A denial by your own authorization handler is counted.

### Startup diagnostics

The server logs once at startup when a setup works on one instance but breaks across restarts or instances:

- 251 (Error): push secrets use the per-process key but the push outbox is durable. Configs with a token or credentials will be dead-lettered after a restart.
- 252 (Warning): the task store is durable but the execution coordinator is the in-process one. `CancelTask` and recovery stay on the instance that runs the agent.
- 253 (Warning): the task store is durable but the retention sweep is not registered.
- 255 (Warning): the task store is durable but artifact objects are in the in-memory object store. See [Artifact storage](artifact-storage.md).
- 700 (Warning): endpoints allow anonymous callers.

Code 250 (Debug) means the checks were skipped because of a broken registration. See [Durable stores](durable-stores.md) and [Retention](retention.md).

## What can go wrong

- No spans appear. Add the source name to `AddSource`. Client and server sources have different names.
- Metrics from `A2A` are missing. Add `AddMeter("A2A")` as well as `AddMeter("A2A.Server")`. This also hides the registry and v0.3 counters.
- Two log lines share an id such as 1000 or 1100. They come from different packages. Filter by category as well as by id.
- A client span and a server span are in different traces. A proxy may drop `traceparent`, or a custom header in `A2ARequestOptions.Headers` may replace it. If a proxy drops it, set `A2AClientOptions.PropagateTraceInMetadata`.

## See also

- [Push notifications](push-notifications.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Task safety](task-safety.md)
- [Streaming](streaming.md)
- [v0.3 compatibility](v03-compatibility.md)
- [Peer identity and registry](peer-identity.md)
