# Durable stores and several instances

The default stores live in memory. That is fine for development and tests. It is not enough when tasks must survive a restart, or when more than one instance serves the same agent. This guide shows the Postgres stores, the Redis event bus and coordinator, and what to check at startup.

## When in-memory stops being enough

- A restart loses every task, event, push config and idempotency key.
- A second instance cannot see tasks, follow a stream or cancel a task that the first instance runs.
- The in-memory idempotency store keeps at most `Idempotency.MaxEntries` entries (100,000 by default).

## Set it up

[!code-csharp[](../snippets/DurableStores.cs#Register)]

- <xref:A2A.Server.Postgres.A2APostgresServiceCollectionExtensions.AddPostgresStores*> (package `RomanAmbinder.A2A.Server.Postgres`) replaces the task, event, push config, push outbox and idempotency stores.
- `AddRedisEventBus` (package `RomanAmbinder.A2A.Server.Redis`) fans live events out to every instance. It also registers the Redis execution coordinator.
- `AddA2ARetention` turns on the sweep. See [Retention](retention.md).
- The two `DataProtection` lines give push secrets a key that every instance shares. See below.

A runnable setup with two instances, Postgres and Redis is in [samples/MultiInstance](https://github.com/roman-ambinder/a2a/tree/main/samples/MultiInstance).

## Postgres

Each task change, its event and its push deliveries are written in one transaction, with a version check. The event log follows the order of task versions, also across instances. The per-task lock of the request handler is per process; the version check is what protects you across instances.

<xref:A2A.Server.Postgres.PostgresStoreOptions> (second argument of `AddPostgresStores`):

| Option | Default | Meaning |
|---|---|---|
| `Schema` | `public` | Schema of the `a2a_*` tables. Letters, digits and underscores only. |
| `CreateSchemaOnStartup` | `true` | A hosted service creates missing tables and indexes at startup, under an advisory lock, so many instances can start together. |
| `CleanupInterval` | 5 minutes | How often expired event logs and finished idempotency entries are deleted. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `IdempotencyClaimTimeout` | 2 minutes | How long a running request holds its idempotency key before another request may take it. `Timeout.InfiniteTimeSpan` means never. |
| `IdempotencyPollInterval` | 200 ms | How often a request that waits on a key held elsewhere checks again. |
| `UseDatabaseClock` | `true` | Deadlines use the database clock (`statement_timestamp()`). `false` uses the `TimeProvider`, for tests with a fake clock. |

- **Schema.** To manage tables yourself, set `CreateSchemaOnStartup = false` and apply `PostgresSchema.ScriptFor("<schema>")` in your migration step, or call `PostgresSchema.EnsureSchemaAsync`. The script uses `IF NOT EXISTS`, so you can apply it again after an upgrade that adds tables.
- **Claim timeout.** The heartbeat of a running execution renews the idempotency claim. So a live request keeps its key, and the timeout only has to cover a missed heartbeat. A crashed instance frees its keys after this time.
- **Clocks.** Push outbox due times and leases, idempotency timeouts and event log retention use the database clock, so instances with skewed clocks agree on what has expired. Task timestamps still come from the instance that wrote them. Skew shifts when retention and `InterruptedTimeout` act on a task. Keep instance clocks in sync.
- **Cleanup scope.** The cleanup deletes event logs and idempotency entries. Expired tasks, orphan contexts, timed-out tasks and dead executions are the job of the [retention sweep](retention.md).

[!code-csharp[](../snippets/DurableStores.cs#PostgresOptions)]

The overload with an `NpgsqlDataSource` uses a data source you own. The container does not dispose it.

## Redis

The bus sends each event on a pub/sub channel `{ChannelPrefix}{tenant}\u001f{taskId}`. Local subscribers get events directly. Redis pub/sub is at most once. When a message is lost, the request handler fills the gap from the event store. So the Redis bus needs a shared event store such as Postgres.

<xref:A2A.Server.Redis.RedisEventBusOptions>:

| Option | Default | Meaning |
|---|---|---|
| `ChannelPrefix` | `a2a:events:` | Prefix of channels and coordinator keys. Use another prefix for each deployment that shares one Redis. |
| `RegisterCoordinator` | `true` | Whether `AddRedisEventBus` also registers the Redis execution coordinator. |

### Execution coordinator

The coordinator knows which executions run on which instance. It answers "is this task running anywhere?", carries CancelTask to the instance that runs the agent, and keeps one lease per running execution. The default is process-local. Use `AddRedisEventBus`, or `AddRedisExecutionCoordinator` for the coordinator alone:

[!code-csharp[](../snippets/DurableStores.cs#CoordinatorOnly)]

Rules:

- **Same connection.** The bus and the coordinator share one connection. Pass the same configuration string to both calls, or the same `IConnectionMultiplexer`. A different string throws `InvalidOperationException` at registration.
- **Redis Cluster.** The coordinator's scripts touch several keys at once, so the key prefix must map to one hash slot. Put the prefix in braces, for example `{a2a}:`.
- **Prefix.** `AddRedisExecutionCoordinator` reads only `ChannelPrefix`. Keep it equal to the bus's.

<xref:A2A.Server.Coordination.CoordinationOptions> (`A2AServerOptions.Coordination`):

| Option | Default | Meaning |
|---|---|---|
| `LeaseTtl` | 30 seconds | How long an execution's lease lasts without a heartbeat. Must be longer than 2 x `Heartbeat`. |
| `Heartbeat` | 10 seconds | How often a running execution renews its lease and its idempotency claim. |
| `RunningPollInterval` | 1 second | How often a waiting request checks whether an execution on another instance still runs. |

[!code-csharp[](../snippets/DurableStores.cs#Coordination)]

### Dead executions

An execution whose heartbeats lapse past `LeaseTtl` counts as dead. That happens when an instance crashes, pauses for a long time, or loses Redis. With [retention](retention.md) on, the sweep then:

- marks the task `FAILED` with the text "The agent stopped unexpectedly.",
- drops the late events of that execution,
- asks every instance to cancel it.

An agent that is still alive stops at its next heartbeat, which finds the lease lost and the task ended. Two cases are left for a later sweep: a lapsed execution on the sweeping instance itself (it registers again), and a task that changed within the last `LeaseTtl`. Without retention, nobody recovers a dead execution and its task stays `WORKING`.

## Push secrets across instances

Push configs hold tokens and credentials. They are stored protected. The default protector (`EphemeralPushSecretProtector`) uses a key per process. With a durable outbox, a restart or another instance could not read those secrets, and the deliveries would be dead-lettered. Call `AddDataProtection()` with a key ring that is persisted and shared by every instance, then `AddA2ADataProtectionPushSecrets()`. You can also register your own `IPushSecretProtector`.

## Startup diagnostics

The server logs these once at startup. Nothing throws, so a host that does not use the feature still starts.

| Event | Level | When |
|---|---|---|
| 251 | Error | The push outbox is durable and the push secret protector is the per-process one. |
| 252 | Warning | The task store is durable and the execution coordinator is the in-memory one. One instance can ignore it. |
| 253 | Warning | The task store is durable and the retention sweep is not registered. |
| 250 | Debug | The checks were skipped because the registrations are broken. |

## Write your own store

Register your stores before `AddA2AServer` (the defaults are added only when missing), or replace them after it. Replace the parts that belong together: `TaskStore`, `EventStore` and `PushOutbox` share one backend, because `TaskStore.CommitAsync` writes to all three.

Implement:

- <xref:A2A.Server.Storage.TaskStore>: `GetAsync`, `CreateAsync`, `UpdateAsync`, `CommitAsync`, `ListAsync`, `GetContextAsync`, `GetOrCreateContextAsync`, and the retention members `DeleteAsync`, `ListExpiredAsync`, `ListInterruptedBeforeAsync`, `DeleteContextAsync`, `ListOrphanContextsAsync`.
- <xref:A2A.Server.Storage.EventStore>: `AppendAsync`, `ReadAfterAsync`, `OldestRetainedAsync`, `LastSequenceAsync`, `MarkTerminalAsync`, `DeleteAsync`.
- `PushConfigStore`, `PushOutbox` and `IdempotencyStore` (including `RenewAsync`).
- `ExecutionCoordinator` is optional. The in-memory one is the default.

Rules that matter:

- **`CommitAsync` is atomic.** It writes the task record (the stored version must equal `ExpectedVersion`; 0 creates the task), appends the event with the next sequence, and stores the push deliveries, all or nothing. A wrong version throws `ConcurrencyException`. A create on an existing task throws `DuplicateTaskException`. Commits to one task must be serialized, so the log order equals the version order.
- **Keep the event bytes.** The server encodes each event once. `SequencedEvent.Json` holds those bytes. Store them and return them unchanged from `ReadAfterAsync`. They go to the wire without validation, so a store that alters, truncates or re-encodes them corrupts the stream.
- **Scope by tenant and owner.** Reads are owner-scoped. A task owned by someone else looks missing.
- **Own your records.** Callers never change what the store returns, and the store does not change what it is given.
- Run the same contract tests as the built-in stores: the `tests/A2A.Stores.Tests` project in the repository.

## What can go wrong

- **Two connection strings.** Registration throws. Use one string or one multiplexer.
- **No shared event store with Redis.** Lost pub/sub messages cannot be filled in.
- **Dead-lettered push deliveries after a restart.** The push secrets key was per process.
- **Cluster prefix without braces.** The coordinator's scripts fail across hash slots.
- **Tasks that never expire.** Retention is off. Look for warning 253.

## See also

- [Retention](retention.md)
- [Push notifications](push-notifications.md)
- [Multi-tenancy](multi-tenancy.md)
- [Postgres package README](https://github.com/roman-ambinder/a2a/blob/main/src/A2A.Server.Postgres/README.md)
- [Redis package README](https://github.com/roman-ambinder/a2a/blob/main/src/A2A.Server.Redis/README.md)
