# Durable stores and several instances

The default stores live in memory. That is fine for development and tests. It is not enough when tasks must survive a restart, or when more than one instance serves the same agent. This guide shows the Postgres stores and coordinator, the Redis event bus and coordinator, and what to check at startup. The execution coordinator is the part that tracks which agent runs are live, holds a lease (a claim renewed by heartbeats) for each, and carries CancelTask between instances.

## When in-memory stops being enough

- A restart loses every task, event, push config and idempotency key.
- A second instance cannot see tasks, follow a stream or cancel a task that the first instance runs.
- The in-memory idempotency store keeps at most `Idempotency.MaxEntries` entries (100,000 by default).

## Set it up

[!code-csharp[](../../../samples/Snippets/DurableStores.cs#Register)]

- <xref:A2A.Server.Postgres.A2APostgresServiceCollectionExtensions.AddPostgresStores*> (package `RomanAmbinder.A2A.Server.Postgres`) replaces the task, event, push config, push outbox and idempotency stores. It also registers the Postgres execution coordinator, unless you registered another one or set `RegisterCoordinator = false`.
- `AddRedisEventBus` (package `RomanAmbinder.A2A.Server.Redis`) fans live events out to every instance. It also registers the Redis execution coordinator, which replaces the Postgres one.
- `AddRetention` turns on the sweep. See [Retention](retention.md).
- The two `DataProtection` lines give push secrets a key that every instance shares. See below.

A runnable setup with two instances, Postgres and Redis is in [samples/MultiInstance](https://github.com/roman-ambinder/a2a/tree/main/samples/MultiInstance).

## Postgres

Each task change, its event and its push deliveries are written in one transaction, with a version check. The event log follows the order of task versions, also across instances. The per-task lock of the request handler is per process; the version check is what protects you across instances.

<xref:A2A.Server.Postgres.PostgresStoreOptions> (second argument of `AddPostgresStores`):

| Option | Default | Meaning |
|---|---|---|
| `Schema` | `public` | Schema of the `a2a_*` tables. Letters, digits and underscores, not starting with a digit, at most 63 characters. |
| `CreateSchemaOnStartup` | `true` | A hosted service applies the missing schema migrations at startup, in one transaction under an advisory lock, so many instances can start together. When `false`, the app refuses to start unless the database version matches the package. |
| `CleanupInterval` | 5 minutes | How often expired event logs and finished idempotency entries are deleted. Zero or `Timeout.InfiniteTimeSpan` turns it off. |
| `IdempotencyClaimTimeout` | 2 minutes | How long a running request holds its idempotency key before another request may take it. `Timeout.InfiniteTimeSpan` means never. |
| `IdempotencyPollInterval` | 200 ms | How often a request that waits on a key held elsewhere checks again. |
| `RegisterCoordinator` | `true` | Whether the stores registration also registers the Postgres execution coordinator. It never replaces a coordinator you registered (for example Redis's). |
| `CancelPollInterval` | 1 second | How often the Postgres coordinator reads cancel requests from the other instances. This is the delay of a cross-instance CancelTask. |
| `UseDatabaseClock` | `true` | Deadlines use the database clock (`statement_timestamp()`). `false` uses the `TimeProvider`, for tests with a fake clock. |
| `ArtifactRows` | `false` | Store artifact parts in their own rows, so an append writes only its new parts. See **Artifact rows** below. |
| `ArtifactRowsCompactAt` | 256 | With `ArtifactRows`: once an artifact has this many rows, its next write stores all its parts in one row again. At least 2. |
| `HistoryRows` | `false` | Store each task's history messages and timeline entries in their own rows, so a status update writes only its new message and entry. See **History rows** below. |

- **Schema.** The schema is kept by numbered migrations. They are applied at startup under an advisory lock, in one transaction. A start on an up-to-date database takes no lock and changes nothing. With `CreateSchemaOnStartup = false` the app refuses to start when the database version differs from the package. The error says which version the database has and what to run. Export `PostgresSchema.ScriptFor("<schema>")` for a new database, and `PostgresSchema.ScriptFor("<schema>", <version in the error>)` for an upgrade. Run it in one transaction, for example `psql -1 -f`. You can also call `PostgresSchema.EnsureSchemaAsync` from your migration step, and `PostgresSchema.VerifySchemaAsync` to check without changing anything. A database newer than the package (after a rollback) is refused when auto-create is off, and logs a warning when it is on.
- **Claim timeout.** The heartbeat of a running execution renews the idempotency claim. So a live request keeps its key, and the timeout only has to cover a missed heartbeat. A crashed instance frees its keys after this time.
- **Retries after a crash.** When a request creates a task, the Postgres task store writes the idempotency outcome in the same transaction as the task. A crash cannot leave a task without it, so a retry attaches to the task and does not run the agent again. A direct reply (a message and no task) has no commit to join, so its outcome is best effort: a crash right after the reply can let a retry run the agent again. A crash before the first commit also runs the agent again. Delivery is at-least-once. A follow-up message to an existing task is safe: its message id is in the task history.
- **Claim ids.** Every claim of a key has an id. An instance whose claim went stale cannot complete, abandon or renew the claim of the instance that took the key over.
- **Clocks.** Push outbox due times and leases, idempotency timeouts and event log retention use the database clock, so instances with skewed clocks agree on what has expired. Task timestamps still come from the instance that wrote them. Skew shifts when retention and `InterruptedTimeout` act on a task. Keep instance clocks in sync.
- **Artifact rows.** By default every commit rewrites the whole task, its artifacts included. So an artifact streamed in *n* commits writes about *n*²/2 chunks' worth of bytes. Batch commits merge chunks that queue up, but a producer slower than a commit, such as a model streaming tokens, gets one commit per chunk.
  - **What the option changes:** with `ArtifactRows`, the parts go in `a2a_artifact_parts` (migration 0007), and an append writes one row with its new parts. It does not read the artifact's whole part list in memory either: it counts the parts and reads only the new ones, so an append costs O(its own parts) in memory too. The ADR-003 benchmark (`tests/A2A.Benchmarks.Postgres`) has the numbers; `-- paced` measures the memory of slow appends.
  - **Before you turn it on:** every instance that shares the database must run a version with the option. An older version reads such a task with empty parts.
  - **Reads:** they handle both forms either way, and a task stored inline moves to rows on its next commit.
  - **Before a downgrade:** turn it off everywhere, then call `PostgresTaskStore.InlineArtifactPartsAsync()` until it returns 0. The instances can keep serving meanwhile. Each converted task gets a new version, so a commit based on an older read fails its version check and is retried.
  - **Alternative:** moving long artifacts to an [artifact object store](artifact-storage.md) also keeps them out of the task.
- **History rows.** By default every commit also rewrites the task's whole `History` and `Timeline`, with or without `ArtifactRows`. Status updates are never batched, and a status update with a message adds it to both lists. So a task with *n* status messages writes O(*n*) bytes per update: in the [ADR-005](https://github.com/roman-ambinder/a2a/blob/main/docs/adr/0005-history-and-timeline-rows.md) benchmark, 4,000 updates of 100 bytes wrote 2.0 GB of WAL.
  - **What the option changes:** with `HistoryRows`, each history message and each timeline entry is a row of `a2a_task_entries` (migration 0008). A status update inserts its message and its timeline entry, and once the timeline is at `A2ALimits.MaxTimelineEntries` it deletes the oldest timeline row in the same commit. The same 4,000 updates wrote 13.2 MB. Reads, `ListTasks` and the conversation memory read the rows in the same statement as the task, so they never see half a commit. The store still reads the committed task's two lists in memory once per commit (O(n) references, not O(n) bytes written). `tests/A2A.Benchmarks.Postgres -- status` measures it.
  - **It is independent of `ArtifactRows`:** turn on either or both.
  - **Before you turn it on:** every instance that shares the database must run a version with the option. An older version reads such a task with an empty history and timeline.
  - **Reads:** they handle both forms either way, and a task stored inline moves to rows on its next commit.
  - **Before a downgrade:** turn it off everywhere, then call `PostgresTaskStore.InlineHistoryAndTimelineAsync()` until it returns 0 (and `InlineArtifactPartsAsync()` if you used `ArtifactRows`). The instances can keep serving meanwhile. Each converted task gets a new version, so a commit based on an older read fails its version check and is retried.
- **Cleanup scope.** The cleanup deletes event logs and idempotency entries. Expired tasks, orphan contexts, timed-out tasks and dead executions are the job of the [retention sweep](retention.md).

[!code-csharp[](../../../samples/Snippets/DurableStores.cs#PostgresOptions)]

The overload with an `NpgsqlDataSource` uses a data source you own. The container does not dispose it.

### Execution coordinator

The coordinator knows which executions run, answers "is this task running anywhere?", carries CancelTask to the instance that runs the agent, and keeps one lease per running execution. The default one is process-local, so a crash loses every lease and nothing recovers the tasks that were running: they stay `WORKING`. A lease in Postgres survives the crash, expires after `LeaseTtl`, and the [retention sweep](retention.md) then fails the task. This holds for one instance too. With several instances, the Postgres coordinator also gives you cross-instance CancelTask and running checks, without Redis.

`AddPostgresStores` registers <xref:A2A.Server.Postgres.PostgresExecutionCoordinator> by default. Use `AddPostgresExecutionCoordinator` for the coordinator alone, or to replace another one:

[!code-csharp[](../../../samples/Snippets/DurableStores.cs#PostgresCoordinator)]

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

[!code-csharp[](../../../samples/Snippets/DurableStores.cs#CoordinatorOnly)]

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

[!code-csharp[](../../../samples/Snippets/DurableStores.cs#Coordination)]

### Dead executions

An execution whose heartbeats lapse past `LeaseTtl` counts as dead. That happens when an instance crashes, pauses for a long time, or loses its coordinator (Postgres or Redis). With [retention](retention.md) on, the sweep then:

- marks the task `FAILED` with the text "The agent stopped unexpectedly.",
- drops the late events of that execution,
- asks every instance to cancel it.

An agent that is still alive stops at its next heartbeat, which finds the lease lost and the task ended. Two cases are left for a later sweep: a lapsed execution on the sweeping instance itself (it registers again), and a task that changed within the last `LeaseTtl`. Without retention, nobody recovers a dead execution and its task stays `WORKING`. With the in-memory coordinator nobody recovers it either, because a crashed process takes its leases along.

## Push secrets across instances

Push configs hold tokens and credentials. They are stored protected. The default protector (`EphemeralPushSecretProtector`) uses a key per process. With a durable outbox, a restart or another instance could not read those secrets, and the deliveries would be dead-lettered. Call `AddDataProtection()` with a key ring that is persisted and shared by every instance, then `AddDataProtectionPushSecrets()` on the A2A builder. You can also register your own `IPushSecretProtector`.

## Startup diagnostics

The server logs these once at startup. They never stop the host, so a host that does not use the feature still starts. Two checks are separate and do stop startup: the Postgres schema check with `CreateSchemaOnStartup = false` on a version mismatch (see **Schema** above), and an artifact object store registered without `ArtifactStorage.PublicBaseUrl` (see [Artifact storage](artifact-storage.md)).

| Event | Level | When |
|---|---|---|
| 251 | Error | The push outbox is durable and the push secret protector is the per-process one. |
| 252 | Warning | The task store is durable and the execution coordinator is the in-memory one. A crash leaves the running tasks unrecovered. |
| 253 | Warning | The task store is durable and the retention sweep is not registered. |
| 255 | Warning | The task store is durable and artifact objects are in the in-memory object store. Stored links stop working after a restart. |
| 257 | Warning | The event bus spans instances (Redis, or your own) and the task, event or idempotency store is the in-memory one. Events cross instances, but the other instances cannot read the tasks and events kept in this process. |
| 250 | Debug | The checks were skipped because the registrations are broken. |

## Write your own backend

Every backend part is a public base class. Register yours before `AddA2AServer` (the defaults are added only when missing), or replace them after it.

| Part | Base class | Default | Needed with several instances |
|---|---|---|---|
| Tasks, contexts | <xref:A2A.Server.Storage.TaskStore> | in memory | yes, shared |
| Event log | <xref:A2A.Server.Storage.EventStore> | in memory | yes, shared |
| Push deliveries | `PushOutbox` | in memory | yes, shared |
| Push configs | `PushConfigStore` | in memory | yes, shared |
| Idempotency keys | `IdempotencyStore` | in memory | yes, shared |
| Running executions, cancel, sweep lease | <xref:A2A.Server.Coordination.ExecutionCoordinator> | in memory | yes, shared |
| Live events to subscribers | <xref:A2A.Server.TaskEventBus> | in process | yes, spans instances |
| Large artifact bytes | <xref:A2A.Server.Storage.ArtifactObjectStore> | none | yes, shared, when you use artifact storage |

New members of these classes follow the rule in [Versioning](versioning.md#store-contracts): your backend keeps building against later versions.

### Stores

Replace the parts that belong together: `TaskStore`, `EventStore` and `PushOutbox` share one backend, because `TaskStore.CommitAsync` writes to all three.

Implement:

- <xref:A2A.Server.Storage.TaskStore>: `GetAsync`, `CreateAsync`, `UpdateAsync`, `CommitAsync`, `ListAsync`, `GetContextAsync`, `GetOrCreateContextAsync`, and the maintenance members that retention uses: `DeleteAsync`, `ListExpiredAsync`, `ListInterruptedBeforeAsync`, `GetForMaintenanceAsync` (reads a task whoever owns it), `DeleteContextAsync`, `ListOrphanContextsAsync`.
- <xref:A2A.Server.Storage.EventStore>: `AppendAsync`, `ReadAfterAsync`, `OldestRetainedAsync`, `LastSequenceAsync`, `MarkTerminalAsync`, `DeleteAsync`.
- `PushConfigStore`, `PushOutbox` and `IdempotencyStore` (including `RenewAsync`).
- `ExecutionCoordinator` is optional. The in-memory one is the default, but it cannot recover tasks after a crash. Prefer a durable one.
- Optional, for speed: <xref:A2A.Server.Storage.IBatchTaskCommit> on the task store, and an override of `EventStore.ReadRangeAsync`. See below.
- <xref:A2A.Server.Storage.ITaskCommitLookup> on the task store, when a commit's answer can be lost or your store retries commits on its own. Optional in the type system, but without it a lost answer fails the run. See below.

Rules that matter:

- **`CommitAsync` is atomic.** It writes the task record (the stored version must equal `ExpectedVersion`; 0 creates the task), appends the event with the next sequence, and stores the push deliveries, all or nothing. A wrong version throws `ConcurrencyException`. A create on an existing task throws `DuplicateTaskException`. Commits to one task must be serialized, so the log order equals the version order.
- **Idempotency outcome in the commit.** `TaskCommit.Idempotency` is set for the commit that creates a task when the request has an idempotency key. A store that shares a transaction with its idempotency store writes the outcome in the same transaction, only for the claim that still owns the entry. No matching claim is not an error. Any other store ignores it; the server records the outcome right after the commit, so a crash in between can still let a retry create a second task. `IdempotencyStore.CompleteAsync`, `AbandonAsync` and `RenewAsync` take the claim id from `IdempotencyResult.New` and must do nothing when the entry belongs to another claim.
- **Batch commits are optional.** With `IBatchTaskCommit`, the server stores artifact chunks that queue up as one commit: one task write, one version, and each event with the next sequence, in order, with its own push deliveries. All or nothing, with the same version check as `CommitAsync`. Without it, the server makes one `CommitAsync` per event. Long chunked streams then cost more, because each commit writes the whole task.
- **Commit lookup is optional, and recommended for a database.** A database can commit a transaction and then lose the answer: the connection times out or resets, and the caller sees an error although the write is stored. A store that retries writes on its own can also meet its first attempt and report a version conflict for its own write. Without help, the server fails the run in the first case. In the second it cannot tell the conflict from another writer's change. So it checks whether the task holds exactly its write: one version past the task it wrote, with the same artifact for an append, and the same task for any other event. Then it keeps the event as stored (log event 226), so it is not added twice. Otherwise it applies the event again on the task as read now. A store that does not give the task back exactly as it was written fails that check for events other than appends, and then the event log holds the event twice. With `ITaskCommitLookup`, keep `TaskCommit.MutationId` (and `TaskBatchCommit.MutationId`, on the batch's first event) with the stored event, and return that event's sequence from `FindCommitAsync`, or null when there is no such commit. After a failed commit call, the server asks, and when the commit is stored it carries on as if the call had succeeded (log event 226). This includes the commit that creates a task: a create that was stored although the call failed (a lost answer, or a `DuplicateTaskException` from the store's own retry) is kept, with its inline push config. Without the lookup, only the duplicate can be checked: the stored task at version 1, equal to the one written. Without the lookup, the task is failed, the config is deleted, and a retried request creates a second task. It asks right after the commit, so the id only has to stay findable while its event is in the replay buffer. The Postgres store keeps it in `a2a_events.mutation_id` (migration 0006), and the in-memory store keeps the last 16 ids per task.
- **`ReadRangeAsync` reads part of the log.** Streams use it to fill a gap. The default reads `ReadAfterAsync` and stops at the bound. Override it to read only the range.
- **Keep the event bytes.** The server encodes each event once. `SequencedEvent.Json` holds those bytes. Store them and return them unchanged from `ReadAfterAsync`. They go to the wire without validation, so a store that alters, truncates or re-encodes them corrupts the stream.
- **Scope by tenant and owner.** Reads are owner-scoped. A task owned by someone else looks missing. The one exception is `GetForMaintenanceAsync`: it reads by tenant and id, whoever owns the task. The orphan sweeps of artifact objects and uploads use it to decide whether a task still links to an object, so a store that hides another owner's task there makes the sweeps delete objects that a live task uses.
- **Own your records.** Callers never change what the store returns, and the store does not change what it is given.
- **Read the task lists only when you need them.** The task the server commits shares its `History`, `Timeline` and each artifact's `Parts` with the task's earlier versions: one buffer per list, which each version sees up to its own length. So a status update with a message, or an append to an artifact, costs the server O(1) in memory however long the task is ([ADR-004](https://github.com/roman-ambinder/a2a/blob/main/docs/adr/0004-task-copies-per-event.md)). The first read of one of these properties on an instance builds its `List<T>`: O(n), once per instance, and every reader of that instance gets the same list. A store that keeps the instances and reads them only when asked (as the in-memory store does) pays that once for each version it hands out. Serializing the task with `A2AJson` (`Serialize`, `SerializeToUtf8Bytes`, or any of its options, `CreateOptions` included) does not build the lists: the JSON contract writes them from the shared buffer, and the bytes are the same as for plain lists. So a store that serializes the whole task on each commit pays O(n) per event only for the bytes it writes. Code that reads the properties itself, such as a mapping of your own or a serializer with your own `JsonSerializerContext`, builds them as before. A store that keeps an artifact's parts apart from the task, as the Postgres store does with `ArtifactRows`, need not: `ModelCopies.CountParts` and `CopyLastParts` count an artifact's parts and read the newest ones without building the list, so it pays only for the parts an append adds. The history and the timeline have no such accessors yet, so the Postgres store with `HistoryRows` builds those two lists once per commit, though it writes only the new entries. To change a task in your store, copy it with `ModelCopies.CopyForUpdate` (or an artifact with `CopyWithParts`) and append with `AppendToHistory`, `AppendToTimeline` and `AppendParts`: they do not build the lists. The history has no count cap; the timeline keeps the newest `A2ALimits.MaxTimelineEntries` entries.

### Event bus

<xref:A2A.Server.TaskEventBus> hands stored events to the live subscribers of a task (SubscribeToTask and streams). The default is process-local, so a subscriber on another instance sees nothing. Derive from it and override `Publish` and `Complete`, as `RedisTaskEventBus` does.

- **Call the base class for local subscribers.** It keeps the bounded subscriptions and drops a lagging subscriber's oldest events, never the newest. `HasSubscribers(taskId)` tells you whether anyone in this process reads the task.
- **Never block.** `Publish` and `Complete` run while the engine holds the task's lock, in sequence order. Do not wait on the network there (queue the message), and do not call back into `A2ARequestHandler` for the same task: that deadlocks.
- **Keep per-task order.** Every subscriber must get a task's events in sequence order.
- **Delivery may be at most once, if the event store is shared.** The request handler drops duplicates and fills gaps in sequences from the <xref:A2A.Server.Storage.EventStore>. That works across instances only when every instance reads the same event store. Startup warning 257 fires when a bus that spans instances runs with an in-memory task, event or idempotency store.
- **Pass the bytes on.** Send `SequencedEvent.Json` as it is, and hand the receiving subscribers the same bytes, so no instance encodes an event again.
- **Scope by tenant.** The handler checks the owner before it subscribes, so the bus topic only needs tenant and task id.

### Execution coordinator

<xref:A2A.Server.Coordination.ExecutionCoordinator> tracks the leases of running executions, carries CancelTask to the instance that runs the task, finds executions whose instance died, and makes sure only one instance runs the retention sweep per period. The in-memory default cannot do any of that across instances, and its leases die with the process (startup warning 252).

- **Override `TryRegisterExclusiveAsync` with an atomic check.** The default checks `IsRunningAsync` and then calls `RegisterAsync`, which is not atomic: two instances can both win. A coordinator that several instances share must override it.
- **Fence every lease operation by task and execution id.** An operation with another execution's id never touches that execution's lease.
- **Expire leases by the store's clock,** never the instance's, so instances with skewed clocks agree. An expired lease no longer counts as running, a late `HeartbeatAsync` does not bring it back (it returns false), and `ListExpiredAsync` reports it until it is unregistered.
- **Cancel requests are fire and forget.** `RequestCancelAsync` reaches the other instances' `CancelRequestsAsync` readers that listen now, at most once. A reader never gets its own instance's requests, and every reader gets every request.
- **`TryAcquireSweepLeaseAsync` is a named cluster-wide lease.** True when it was free or expired. False while it is held, also by the caller. It is not released early.

### Artifact object store

<xref:A2A.Server.Storage.ArtifactObjectStore> keeps the bytes of large artifacts and uploads. Every provider must:

- read the stream once, and never buffer the whole object;
- stop with `ArtifactObjectTooLargeException` when the stream is longer than `maxBytes`;
- leave no object behind when a write fails;
- replace an existing object with the same key, and do nothing when a delete finds no object.

Override `ExistsAsync` when you have a cheaper check than opening the object, and `TryCreateDownloadUrlAsync` when the backend can make links that callers fetch directly (presigned or SAS URLs). See [Artifact storage](artifact-storage.md).

### Contract tests

Run the same contract tests as the built-in backends: the `tests/A2A.Stores.Tests` project in the repository. It has a contract for each part: `TaskStoreContract`, `EventStoreContract`, `PushOutboxContract`, `PushConfigStoreContract`, `IdempotencyStoreContract`, `ExecutionCoordinatorContract`, `TaskEventBusContract` and `ArtifactObjectStoreContract`.

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
