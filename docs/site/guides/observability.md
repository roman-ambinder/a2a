# Observability

The SDK traces with `System.Diagnostics.ActivitySource` and counts with `System.Diagnostics.Metrics.Meter`. The core packages need no OpenTelemetry package. Use this guide to wire the SDK to OpenTelemetry (or any listener), and to know which spans, tags and counters exist. All names are constants on <xref:A2A.Diagnostics.A2ATelemetry>.

Spans and counters cost almost nothing while nobody listens: no activity and no tag array is created.

## Set up

On the server, listen to the `A2A.Server` source and to the two meters:

[!code-csharp[](../snippets/Observability.cs#ServerOpenTelemetry)]

On a client process, listen to `A2A.Client`:

[!code-csharp[](../snippets/Observability.cs#ClientOpenTelemetry)]

| Name | Constant | Used for |
|---|---|---|
| `A2A.Server` | <xref:A2A.Diagnostics.A2ATelemetry.ServerSourceName> | server spans |
| `A2A.Client` | <xref:A2A.Diagnostics.A2ATelemetry.ClientSourceName> | client spans |
| `A2A.Server` | <xref:A2A.Diagnostics.A2ATelemetry.ServerMeterName> | server counters |
| `A2A` | none | card and idempotency counters (see Metrics) |

## Spans

| Source | Span | Kind | When |
|---|---|---|---|
| `A2A.Server` | `a2a.{Operation}` (`a2a.SendMessage`, `a2a.GetTask`, ...) | Server; Internal when it runs under another local span such as ASP.NET Core's request span | every operation, on all three bindings |
| `A2A.Server` | `a2a.agent.execute` | Internal | one agent run (execute or cancel); child of the operation span |
| `A2A.Server` | `a2a.push.deliver` | Client | one attempt to send one event to one webhook |
| `A2A.Client` | `a2a.{Operation}` | Client | every `A2AClient` operation, with its retries and stream reconnects |

The operation span is the outermost step of the operation pipeline. It wraps the host filters and authorization, so a refused or unauthenticated call still has a span with its error. Extension operations get `a2a.{method}` spans.

The delivery span continues the trace of the agent run that produced the event. The trace context is stored with the delivery, so a retry or another instance stays in the same trace.

## Propagation

- The client transports send the current span as W3C `traceparent` and `tracestate` on JSON-RPC, HTTP+JSON and gRPC (as metadata).
- The server reads both headers on all three bindings into `ServerCallContext.TraceParent`. Client and server spans then share one trace.
- A webhook POST carries the `traceparent` of its delivery span.
- A `traceparent` you set in `A2ARequestOptions.Headers` replaces the one the SDK wrote.

## Streams

One span covers a whole stream. Each event is a span event named `a2a.stream.event`, not a child span. Its tags are `a2a.event.kind` (`task`, `message`, `status_update` or `artifact_update`), `a2a.event.sequence` (server only) and `a2a.task.state` (status updates).

A client reconnect adds an `a2a.retry` span event with `a2a.retry.attempt`. A failed webhook attempt that will be tried again adds an `a2a.retry` event, without tags, to its delivery span.

## Tags

| Tag | On | Notes |
|---|---|---|
| `rpc.system` | operation spans | always `a2a` |
| `rpc.method` | operation spans | the operation name |
| `a2a.binding` | operation spans | `JSONRPC`, `HTTP+JSON` or `GRPC` |
| `a2a.task.id` | operation, run and delivery spans | when known |
| `a2a.context.id` | operation and run spans | when known |
| `a2a.tenant` | all server spans | only for a non-default tenant |
| `a2a.task.state` | operation and run spans | the task state after the call or run |
| `a2a.stream.event_count` | stream spans | events sent or received |
| `a2a.execution.kind` | run spans | `execute` or `cancel` |
| `a2a.execution.timed_out` | run spans | `true` when `MaxTaskDuration` ended the run |
| `a2a.push.config.id`, `a2a.push.sequence`, `a2a.push.attempts` | delivery spans | |
| `server.address` | delivery spans | the webhook host only |
| `http.response.status_code` | delivery spans | the webhook's answer |

## Errors

A failed operation gets status `Error`, the `error.type` tag and, for A2A errors, `a2a.error.code` (the <xref:A2A.A2AErrorCode> name, for example `TaskNotFound`). For a non-A2A exception, `error.type` is the exception type name.

A run span is an error when the agent throws, when it hits <xref:A2A.Server.A2AServerOptions.MaxTaskDuration> (`error.type` is `TimeLimit`), or when it leaves the task `FAILED` (`error.type` is `TaskFailed`). A cancellation the caller asked for sets `a2a.canceled` to `true` and is not an error.

## No content

Tags never hold message text, parts, metadata, tokens, credentials, webhook URLs or exception messages. Ids and outcomes only.

## Metrics

All counters are `Counter<long>`.

| Meter | Counter | Tags | Counts |
|---|---|---|---|
| `A2A.Server` | `a2a.server.requests` | `rpc.method`, `a2a.outcome` (`ok`, `canceled`, `error`), `error.type` on errors | operations handled |
| `A2A.Server` | `a2a.server.events` | none | task events stored |
| `A2A.Server` | `a2a.server.streams.opened` | none | streams admitted (`SendStreamingMessage`, `SubscribeToTask`) |
| `A2A.Server` | `a2a.server.push.deliveries` | `a2a.outcome` (`sent`, `retried`, `dead_lettered`) | one per delivery attempt, counted when its result is recorded |
| `A2A.Server` | `a2a.server.push.dead_letters` | none | deliveries that failed for good |
| `A2A` | `a2a.idempotency.evictions` | none | idempotency entries dropped early because the in-memory store was full |
| `A2A` | `a2a.card.changed` | `origin` | cards that differ from the pinned card of their origin |
| `A2A` | `a2a.card.unsigned` | `origin` | unsigned cards accepted because signatures are not required |

A stream whose reader stops early counts as `canceled`. A cancellation that the caller did not ask for, such as a server-side timeout, counts as `error`.

The push counters are also exposed as `PushDeliveryService.DeliveriesCounterName` and `PushDeliveryService.DeadLettersCounterName`. The idempotency counter is `InMemoryIdempotencyStore.EvictionsCounterName` on `InMemoryIdempotencyStore.MeterName`.

The card counters come from the client's `A2ACardResolver`, so they exist in client processes. The meter `A2A` therefore needs `AddMeter("A2A")` in the process that resolves cards or runs the in-memory idempotency store.

## Logs

The SDK logs through `ILogger` with fixed event ids. Use them in log filters and alerts.

| Ids | Area |
|---|---|
| 100-101 | client: card signing and pinning |
| 200-235 | server: agent runs, coordination, push-config cleanup, retention |
| 250-253 | server: startup diagnostics |
| 300-313 | server: push delivery |
| 400-402 | server: idempotency |
| 500-501, 600 | ASP.NET Core: unhandled errors, stream errors |
| 700-701, 800 | ASP.NET Core: startup validation, card signing warning |
| 900 | gRPC: unhandled errors |
| 1000-1001 | Postgres cleanup |
| 1100-1104 | Redis event bus and coordinator |

### Startup diagnostics

The server logs once at startup when a setup works on one instance but breaks across restarts or instances:

- 251 (Error): push secrets use the per-process key but the push outbox is durable. Configs with a token or credentials will be dead-lettered after a restart.
- 252 (Warning): the task store is durable but the execution coordinator is the in-process one. `CancelTask` and recovery stay on the instance that runs the agent.
- 253 (Warning): the task store is durable but the retention sweep is not registered.
- 700 (Warning): endpoints allow anonymous callers.

Code 250 (Debug) means the checks were skipped because of a broken registration. See [Durable stores](durable-stores.md) and [Retention](retention.md).

## What can go wrong

- No spans appear. Add the source name to `AddSource`. Client and server sources have different names.
- Metrics from `A2A` are missing. Add `AddMeter("A2A")` as well as `AddMeter("A2A.Server")`.
- A client span and a server span are in different traces. A proxy may drop `traceparent`, or a custom header in `A2ARequestOptions.Headers` may replace it.

## See also

- [Push notifications](push-notifications.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Task safety](task-safety.md)
- [Streaming](streaming.md)
