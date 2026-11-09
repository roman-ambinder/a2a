# RomanAmbinder.A2A.Server.Redis

A Redis pub/sub event bus and execution coordinator for an A2A (Agent2Agent) server. Live task events (SubscribeToTask, blocking SendMessage) reach subscribers on every server instance, not only the one that runs the task, and CancelTask and blocking waits work across instances.

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = card)
    .AddAgentExecutor<MyAgent>()
    .AddPostgresStores(postgresConnectionString)   // a shared event store is required
    .AddRedisEventBus("redis:6379");
```

`AddA2ARedisEventBus(configuration | IConnectionMultiplexer, configure)` does the same on an `IServiceCollection`, before or after `AddA2AServer`.

## How it works

- Local subscribers get events directly, with no Redis round trip, through the same bounded subscriptions as the in-memory bus (`StreamingOptions.ChannelCapacity`; a lagging subscriber loses its oldest queued events and fills the gap from the event store). Each event is also published on the channel `{ChannelPrefix}{tenant}\u001f{taskId}`.
- The message carries the event's encoded bytes as the server encoded them, and the receiving instance passes the same bytes on: no instance encodes an event again.
- Every instance subscribes to `{ChannelPrefix}*`, drops its own messages, and skips topics it has no subscriber for without decoding them.
- `Publish` and `Complete` never block (they run under the task lock): messages are queued fire-and-forget on the multiplexer.
- Per-task order holds: an instance publishes a task's events in sequence order on one connection, and reads its subscription in order.
- Redis pub/sub is at-most-once. When a message is lost, the request handler fills the gap from the event store, so use a shared store (for example `RomanAmbinder.A2A.Server.Postgres`).
- The bus topic is tenant + task id. Owner checks happen in the request handler before anyone subscribes.
- Set a different `ChannelPrefix` (default `a2a:events:`) for each deployment that shares one Redis.

## Execution coordinator

`AddRedisEventBus` also registers `RedisExecutionCoordinator` on the same connection and prefix (set `RegisterCoordinator = false` to keep the process-local one; `AddRedisExecutionCoordinator(...)` registers it on its own). It gives every instance:

- a cluster-wide running check: each execution holds a lease (`{prefix}lease:{task}`, renewed every `Coordination.Heartbeat`, expiring after `Coordination.LeaseTtl`), so a blocking or retried SendMessage on one instance waits for an agent running on another;
- cancel requests on the channel `{prefix}cancel`, tagged with the sender, which ignores its own echo: CancelTask on any instance stops the agent wherever it runs;
- the leases of dead instances (`{prefix}leases`, a sorted set by expiry) for the retention sweep, and named sweep leases (`{prefix}sweep:{name}`).

Lease expiry uses Redis `TIME` inside Lua scripts, never the instance clock, so skewed clocks do not expire live executions. The heartbeat also renews the request's idempotency claim, so a long direct reply keeps its key. On Redis Cluster, the key prefix must map to one hash slot (for example `{a2a}:`), because the scripts touch several keys at once.

Registration rules:
- The coordinator is **in memory by default**. `AddRedisEventBus` replaces it with the Redis one unless `RegisterCoordinator = false`; `AddRedisExecutionCoordinator` registers it alone.
- Pass the **same connection string** to `AddRedisEventBus` and `AddRedisExecutionCoordinator`. They share one connection, and a different string throws `InvalidOperationException`. (With your own `IConnectionMultiplexer`, pass the same instance.)
- Timing is `A2AServerOptions.Coordination` (`CoordinationOptions`): `LeaseTtl` 30 s, `Heartbeat` 10 s, `RunningPollInterval` 1 s, `StreamCheckInterval` 30 s (how often an idle SubscribeToTask stream looks in the store, which finds events a lost bus message dropped; one small set of store reads per idle stream per interval). `LeaseTtl` must be longer than 2 x `Heartbeat`. An execution whose heartbeats lapse past `LeaseTtl` is treated as dead: with `services.AddA2ARetention()` its task is marked `FAILED`. Without retention nobody recovers it.

A runnable example with two instances is in [samples/MultiInstance](https://github.com/roman-ambinder/a2a/tree/main/samples/MultiInstance).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
