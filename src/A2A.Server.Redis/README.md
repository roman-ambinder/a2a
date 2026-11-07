# RomanAmbinder.A2A.Server.Redis

A Redis pub/sub event bus for an A2A (Agent2Agent) server. Live task events (SubscribeToTask, blocking SendMessage) reach subscribers on every server instance, not only the one that runs the task.

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = card)
    .AddAgentExecutor<MyAgent>()
    .AddPostgresStores(postgresConnectionString)   // a shared event store is required
    .AddRedisEventBus("redis:6379");
```

`AddA2ARedisEventBus(configuration | IConnectionMultiplexer, configure)` does the same on an `IServiceCollection`, before or after `AddA2AServer`.

## How it works

- Local subscribers get events directly, with no Redis round trip. Each event is also published on the channel `{ChannelPrefix}{tenant}\u001f{taskId}`.
- Every instance subscribes to `{ChannelPrefix}*`, drops its own messages, and skips topics it has no subscriber for without decoding them.
- `Publish` and `Complete` never block (they run under the task lock): messages are queued fire-and-forget on the multiplexer.
- Per-task order holds: an instance publishes a task's events in sequence order on one connection, and reads its subscription in order.
- Redis pub/sub is at-most-once. When a message is lost, the request handler fills the gap from the event store, so use a shared store (for example `RomanAmbinder.A2A.Server.Postgres`).
- The bus topic is tenant + task id. Owner checks happen in the request handler before anyone subscribes.
- Set a different `ChannelPrefix` (default `a2a:events:`) for each deployment that shares one Redis.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
