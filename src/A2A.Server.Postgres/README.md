# RomanAmbinder.A2A.Server.Postgres

PostgreSQL stores for an A2A (Agent2Agent) server: tasks, the per-task event log, push configs, the push outbox and idempotency keys. Plain Npgsql, no EF.
With these stores (and a shared event bus such as `RomanAmbinder.A2A.Server.Redis`) the server can run as more than one instance.

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = card)
    .AddAgentExecutor<MyAgent>()
    .AddPostgresStores(builder.Configuration.GetConnectionString("a2a")!);

// Push secrets are stored protected. Use a protector whose keys survive restarts and are shared by all instances:
builder.Services.AddDataProtection();               // persist the key ring where every instance can read it
builder.Services.AddA2ADataProtectionPushSecrets();  // from A2A.Server.AspNetCore
```

`AddA2APostgresStores(connectionString | NpgsqlDataSource, configure)` does the same on an `IServiceCollection`, before or after `AddA2AServer`.

## Schema

At startup a hosted service creates the tables if they are missing (`CREATE ... IF NOT EXISTS`, under an advisory lock, so many instances can start at once).
To manage the schema yourself, set `CreateSchemaOnStartup = false` and apply `PostgresSchema.ScriptFor("<schema>")`, or call `PostgresSchema.EnsureSchemaAsync(dataSource, schema)` from your migration step.

| Table | Key | Holds |
|---|---|---|
| `a2a_tasks` | `(tenant, task_id)` | Task JSON, owner, context, state, `status_ticks`, version. Index `(tenant, owner, status_ticks DESC, task_id)` serves ListTasks; a second one adds `context_id`. |
| `a2a_contexts` | `(tenant, context_id)` | The first owner of each context. |
| `a2a_event_logs` | `(tenant, owner, task_id)` | Last sequence, and when a terminal task's log expires (`purge_ticks`). |
| `a2a_events` | `(tenant, owner, task_id, seq)` | The retained events (last `Streaming.ReplayBufferSize`). |
| `a2a_push_configs` | `(tenant, owner, task_id, config_id)` | Push config JSON with protected secrets. |
| `a2a_push_outbox` | `(tenant, owner, task_id, config_id, seq)` | Push deliveries waiting to be sent: delivery id, payload (the event JSON), attempt, due time, lease, last error. Index on `due_ticks`. |
| `a2a_push_dead_letters` | `(tenant, owner, task_id, config_id, seq)` | Push deliveries that failed for good, with their payload, until re-driven. Index `(tenant, dead_ticks)`. |
| `a2a_idempotency` | `(tenant, principal, key)` | Request hash, running claim, outcome and expiry. |

Times are UTC ticks (`bigint`), JSON is `text`, and keys use the `"C"` collation, so values round-trip exactly and sort like the in-memory stores.

## Behavior

Same contract as the in-memory stores (the same test suite runs against both): owner and tenant scoping, optimistic concurrency on versions,
ListTasks newest first with keyset paging, per-task event sequences from 1, trimming to the replay buffer, terminal retention, and idempotency retention.
A task change, its event and its push deliveries are written in one transaction (`TaskStore.CommitAsync`), so the event log follows the order of task versions, also across instances, and no event loses its webhooks.

Differences, because the store is shared by several instances:
- A request that waits on an idempotency key held by another instance polls every `IdempotencyPollInterval` (200 ms).
- A running idempotency claim older than `IdempotencyClaimTimeout` (1 hour) counts as abandoned, so a crashed instance does not hold its keys forever.
- There is no `Idempotency.MaxEntries` cap; rows live until their retention ends.
- Every instance's `PushDeliveryService` polls the shared outbox (`PushDelivery.PollInterval`, 1 s). A delivery is leased (`FOR UPDATE SKIP LOCKED`, then hidden for `PushDelivery.Lease`, 1 minute), so two instances never send it at the same time, and a crashed instance's deliveries are sent by another one after the lease. Results are written with the lease's token, so an instance whose lease was taken over changes nothing. The lease must be longer than 2 × `PushNotifications.Timeout` + 5 s (checked at startup).
- Expired rows are hidden from reads at once and deleted by a background cleanup every `CleanupInterval` (5 minutes). `PostgresMaintenance.PurgeExpiredAsync()` runs it by hand.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
