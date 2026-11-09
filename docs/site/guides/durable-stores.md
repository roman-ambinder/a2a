# Durable stores and several instances

The default stores live in memory. That is fine for development and tests. It is not enough when tasks must survive a restart, or when more than one instance serves the same agent. This guide shows the Postgres stores and coordinator, the Redis event bus and coordinator, and what to check at startup.

## When in-memory stops being enough

- A restart loses every task, event, push config and idempotency key.
- A second instance cannot see tasks, follow a stream or cancel a task that the first instance runs.
- The in-memory idempotency store keeps at most `Idempotency.MaxEntries` entries (100,000 by default).

## Set it up

[!code-csharp[](../snippets/DurableStores.cs#Register)]

- <xref:A2A.Server.Postgres.A2APostgresServiceCollectionExtensions.AddPostgresStores*> (package `RomanAmbinder.A2A.Server.Postgres`) replaces the task, event, push config, push outbox and idempotency stores. It also registers the Postgres execution coordinator, unless you registered another one or set `RegisterCoordinator = false`.
- `AddRedisEventBus` (package `RomanAmbinder.A2A.Server.Redis`) fans live events out to every instance. It also registers the Redis execution coordinator, which replaces the Postgres one.
- `AddA2ARetention` turns on the sweep. See [Retention](retention.md).
- The two `DataProtection` lines give push secrets a key that every instance shares. See below.

A runnable setup with two instances, Postgres and Redis is in [samples/MultiInstance](https://github.com/roman-ambinder/a2a/tree/main/samples/MultiInstance).

## Postgres

Each task change, its event and its push deliveries are written in one transaction, with a version check. The event log follows the order of task versions, also across instances. The per-task lock of the request handler is per process; the version check is what protects you across instances.

<xref:A2A.Server.Postgres.PostgresStoreOptions> (second argument of `AddPostgresStores`):

| Option | Default | Meaning |
|---|---|---|
| `Schema` | `public` | Schema of the `a2a_*` tables. Letters, digits and underscores only. |
| `CreateSchemaOnStartup` | `true` | A hosted service applies the missing schema migrations at startup, in one transaction under an advisory lock, so many instances can start together. When `false`, the app refuses to start unless the database version matches the package. |
| `CleanupInterval` | 5 minutes | How often expired event logs and finished idempotency entries are deleted. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `IdempotencyClaimTimeout` | 2 minutes | How long a running request holds its idempotency key before another request may take it. `Timeout.InfiniteTimeSpan` means never. |
| `IdempotencyPollInterval` | 200 ms | How often a request that waits on a key held elsewhere checks again. |
| `RegisterCoordinator` | `true` | Whether the stores registration also registers the Postgres execution coordinator. It never replaces a coordinator you registered (for example Redis's). |
| `CancelPollInterval` | 1 second | How often the Postgres coordinator reads cancel requests from the other instances. This is the delay of a cross-instance CancelTask. |
| `UseDatabaseClock` | `true` | Deadlines use the database clock (`statement_timestamp()`). `false` uses the `TimeProvider`, for tests with a fake clock. |

- **Schema.** The schema is kept by numbered migrations. They are applied at startup under an advisory lock, in one transaction. A start on an up-to-date database takes no lock and changes nothing. With `CreateSchemaOnStartup = false` the app refuses to start when the database version differs from the package. The error says which version the database has and what to run. Export `PostgresSchema.ScriptFor("<schema>")` for a new database, and `PostgresSchema.ScriptFor("<schema>", <version in the error>)` for an upgrade. Run it in one transaction, for example `psql -1 -f`. You can also call `PostgresSchema.EnsureSchemaAsync` from your migration step, and `PostgresSchema.VerifySchemaAsync` to check without changing anything. A database newer than the package (after a rollback) is refused when auto-create is off, and logs a warning when it is on.
- **Claim timeout.** The heartbeat of a running execution renews the idempotency claim. So a live request keeps its key, and the timeout only has to cover a missed heartbeat. A crashed instance frees its keys after this time.
- **Retries after a crash.** When a request creates a task, the Postgres task store writes the idempotency outcome in the same transaction as the task. A crash cannot leave a task without it, so a retry attaches to the task and does not run the agent again. A direct reply (a message and no task) has no commit to join, so its outcome is best effort: a crash right after the reply can let a retry run the agent again. A crash before the first commit also runs the agent again. Delivery is at-least-once. A follow-up message to an existing task is safe: its message id is in the task history.
- **Claim ids.** Every claim of a key has an id. An instance whose claim went stale cannot complete, abandon or renew the claim of the instance that took the key over.
- **Clocks.** Push outbox due times and leases, idempotency timeouts and event log retention use the database clock, so instances with skewed clocks agree on what has expired. Task timestamps still come from the instance that wrote them. Skew shifts when retention and `InterruptedTimeout` act on a task. Keep instance clocks in sync.
- **Cleanup scope.** The cleanup deletes event logs and idempotency entries. Expired tasks, orphan contexts, timed-out tasks and dead executions are the job of the [retention sweep](retention.md).

[!code-csharp[](../snippets/DurableStores.cs#PostgresOptions)]

The overload with an `NpgsqlDataSource` uses a data source you own. The container does not dispose it.

### Execution coordinator

The coordinator knows which executions run, answers "is this task running anywhere?", carries CancelTask to the instance that runs the agent, and keeps one lease per running execution. The default one is process-local, so a crash loses every lease and nothing recovers the tasks that were running: they stay `WORKING`. A lease in Postgres survives the crash, expires after `LeaseTtl`, and the [retention sweep](retention.md) then fails the task. This holds for one instance too. With several instances, the Postgres coordinator also gives you cross-instance CancelTask and running checks, without Redis.

`AddPostgresStores` registers <xref:A2A.Server.Postgres.PostgresExecutionCoordinator> by default. Use `AddPostgresExecutionCoordinator` for the coordinator alone, or to replace another one:

[!code-csharp[](../snippets/DurableStores.cs#PostgresCoordinator)]

- **Tables.** `a2a_execution_leases`, `a2a_sweep_leases` and `a2a_cancel_requests`. Leases are decided on the database clock (`UseDatabaseClock`).
- **Cancel requests.** The coordinator polls `a2a_cancel_requests` every `CancelPollInterval`. Every instance reads every request and skips its own, so the instance that runs the agent always sees it. A plain poll needs no `LISTEN` connection, so it works through PgBouncer in transaction mode. Rows older than 5 minutes are deleted by the next request, so the table stays small. Delivery is at most once. An instance that was down for a long time misses old requests; its execution still stops when it finds its lease lost or the task ended.
- **Cost.** One small query per instance per `CancelPollInterval`, plus one write per heartbeat of each running execution.
- **Redis instead.** A coordinator you register yourself, such as Redis's, is never replaced by the Postgres default, in any registration order. `AddRedisEventBus` and `AddRedisExecutionCoordinator` replace the Postgres one when they come later.

## Redis

The bus sends each event on a pub/sub channel `{ChannelPrefix}{tenant}\u001f{taskId}`. Local subscribers get events directly. Redis pub/sub is at most once. When a message is lost, the request handler fills the gap from the event store. So the Redis bus needs a shared event store such as Postgres.

<xref:A2A.Server.Redis.RedisEventBusOptions>:

| Option | Default | Meaning |
|---|---|---|
| `ChannelPrefix` | `a2a:events:` | Prefix of channels and coordinator keys. Use another prefix for each deployment that shares one Redis. |
| `RegisterCoordinator` | `true` | Whether `AddRedisEventBus` also registers the Redis execution coordinator. |

### Execution coordinator

The Redis coordinator does the same as the Postgres one, with Redis pub/sub for cancel requests. Use it when you already run Redis for the event bus. `AddRedisEventBus` registers it, or use `AddRedisExecutionCoordinator` for the coordinator alone:

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
| `StreamCheckInterval` | 30 seconds | How often an idle SubscribeToTask stream looks in the store for events a lost bus message dropped. Costs one small set of store reads per idle stream per interval. |

[!code-csharp[](../snippets/DurableStores.cs#Coordination)]

### Dead executions

An execution whose heartbeats lapse past `LeaseTtl` counts as dead. That happens when an instance crashes, pauses for a long time, or loses its coordinator (Postgres or Redis). With [retention](retention.md) on, the sweep then:

- marks the task `FAILED` with the text "The agent stopped unexpectedly.",
- drops the late events of that execution,
- asks every instance to cancel it.

An agent that is still alive stops at its next heartbeat, which finds the lease lost and the task ended. Two cases are left for a later sweep: a lapsed execution on the sweeping instance itself (it registers again), and a task that changed within the last `LeaseTtl`. Without retention, nobody recovers a dead execution and its task stays `WORKING`. With the in-memory coordinator nobody recovers it either, because a crashed process takes its leases along.

## Push secrets across instances

Push configs hold tokens and credentials. They are stored protected. The default protector (`EphemeralPushSecretProtector`) uses a key per process. With a durable outbox, a restart or another instance could not read those secrets, and the deliveries would be dead-lettered. Call `AddDataProtection()` with a key ring that is persisted and shared by every instance, then `AddA2ADataProtectionPushSecrets()`. You can also register your own `IPushSecretProtector`.

## Startup diagnostics

The server logs these once at startup. Nothing throws, so a host that does not use the feature still starts. The Postgres schema check is separate: with `CreateSchemaOnStartup = false` it does stop startup on a version mismatch (see **Schema** above).

| Event | Level | When |
|---|---|---|
| 251 | Error | The push outbox is durable and the push secret protector is the per-process one. |
| 252 | Warning | The task store is durable and the execution coordinator is the in-memory one. A crash leaves the running tasks unrecovered. |
| 253 | Warning | The task store is durable and the retention sweep is not registered. |
| 257 | Warning | The event bus spans instances (Redis, or your own) and the task, event or idempotency store is the in-memory one. Events cross instances, but the other instances cannot read the tasks and events kept in this process. |
| 250 | Debug | The checks were skipped because the registrations are broken. |

## Write your own store

Register your stores before `AddA2AServer` (the defaults are added only when missing), or replace them after it. Replace the parts that belong together: `TaskStore`, `EventStore` and `PushOutbox` share one backend, because `TaskStore.CommitAsync` writes to all three.

Implement:

- <xref:A2A.Server.Storage.TaskStore>: `GetAsync`, `CreateAsync`, `UpdateAsync`, `CommitAsync`, `ListAsync`, `GetContextAsync`, `GetOrCreateContextAsync`, and the retention members `DeleteAsync`, `ListExpiredAsync`, `ListInterruptedBeforeAsync`, `DeleteContextAsync`, `ListOrphanContextsAsync`.
- <xref:A2A.Server.Storage.EventStore>: `AppendAsync`, `ReadAfterAsync`, `OldestRetainedAsync`, `LastSequenceAsync`, `MarkTerminalAsync`, `DeleteAsync`.
- `PushConfigStore`, `PushOutbox` and `IdempotencyStore` (including `RenewAsync`).
- `ExecutionCoordinator` is optional. The in-memory one is the default, but it cannot recover tasks after a crash. Prefer a durable one.

Rules that matter:

- **`CommitAsync` is atomic.** It writes the task record (the stored version must equal `ExpectedVersion`; 0 creates the task), appends the event with the next sequence, and stores the push deliveries, all or nothing. A wrong version throws `ConcurrencyException`. A create on an existing task throws `DuplicateTaskException`. Commits to one task must be serialized, so the log order equals the version order.
- **Idempotency outcome in the commit.** `TaskCommit.Idempotency` is set for the commit that creates a task when the request has an idempotency key. A store that shares a transaction with its idempotency store writes the outcome in the same transaction, only for the claim that still owns the entry. No matching claim is not an error. Any other store ignores it; the server records the outcome right after the commit, so a crash in between can still let a retry create a second task. `IdempotencyStore.CompleteAsync`, `AbandonAsync` and `RenewAsync` take the claim id from `IdempotencyResult.New` and must do nothing when the entry belongs to another claim.
- **Keep the event bytes.** The server encodes each event once. `SequencedEvent.Json` holds those bytes. Store them and return them unchanged from `ReadAfterAsync`. They go to the wire without validation, so a store that alters, truncates or re-encodes them corrupts the stream.
- **Scope by tenant and owner.** Reads are owner-scoped. A task owned by someone else looks missing.
- **Own your records.** Callers never change what the store returns, and the store does not change what it is given.
- Run the same contract tests as the built-in stores: the `tests/A2A.Stores.Tests` project in the repository.

## What can go wrong

- **Schema version mismatch.** With `CreateSchemaOnStartup = false`, the app does not start when the database is older or newer than the package. Apply the script named in the error, or upgrade the package.
- **Two connection strings.** Registration throws. Use one string or one multiplexer.
- **No shared event store with Redis.** Lost pub/sub messages cannot be filled in.
- **Dead-lettered push deliveries after a restart.** The push secrets key was per process.
- **In-memory coordinator with durable stores.** A crash leaves tasks `WORKING` for good. Look for warning 252.
- **Cluster prefix without braces.** The coordinator's scripts fail across hash slots.
- **Tasks that never expire.** Retention is off. Look for warning 253.

## See also

- [Retention](retention.md)
- [Push notifications](push-notifications.md)
- [Multi-tenancy](multi-tenancy.md)
- [Postgres package README](https://github.com/roman-ambinder/a2a/blob/main/src/A2A.Server.Postgres/README.md)
- [Redis package README](https://github.com/roman-ambinder/a2a/blob/main/src/A2A.Server.Redis/README.md)
