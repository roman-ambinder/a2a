# Client outbox and work ledger

**Date:** 2026-11-09. **Status:** design for implementation.

## Problem

The server has a durable push outbox. An agent that calls a peer has nothing durable. Retries and idempotent `SendMessage` live only as long as the process. A crash between "save my state" and "call peer" loses the call. Nothing records "sent, task id X, result not collected".

## What we add

A row per outgoing message that goes through: queued, sent and tracked, final result stored, result collected. It survives a restart. An app can write the row in its own database transaction.

- `A2A.Client` namespace `A2A.Client.Outbox`: abstraction, in-memory store, dispatcher. All targets, netstandard2.0 too.
- New package `A2A.Client.Postgres` (net8/net10, Npgsql, references `A2A.Client` only, never `A2A.Server`).

Names carry `Client` because the server already has `PushOutbox` and `OutboxEntry`.

## Life of an entry

```
Enqueue -> Queued -> (send) -> Tracking -> (poll / webhook hint) -> Finished -> ack -> collected -> purged
              \                    \
               +----> Failed <-----+   (Failed is harvested too)
```

`ClientOutboxStatus`: `Queued`, `Tracking`, `Finished`, `Failed`.

- **Finished** means the peer gave a final answer. "Final" is the same rule as `PushTaskTracker`: terminal or interrupted, or a `Message` response. The answer can be a failed task or input-required. The caller reads it. To continue an interrupted task, enqueue a new message with that task id.
- **Failed** means the SDK could not get an answer: the peer rejected the send, time ran out, or the outcome is in doubt under `RetryInDoubt = false`.

## Delivery guarantee

At least once to the peer, deduplicated by `Idempotency-Key` = entry id. The SDK server honours it. A peer that ignores the key can see a duplicate only when the earlier attempt's outcome was unknown (`InDoubt`). `RetryInDoubt = false` trades that for at most once: an in-doubt entry becomes `Failed`, and the app decides.

`InDoubt` is set in the store when a `Queued` entry is leased, and cleared only when an attempt definitely did not create work. So a crash mid-send leaves it set with no extra write.

Harvest is at least once too: lease, process, ack. The ack can be in the app's own transaction, which makes it exactly once for the app's state.

## Types (`A2A.Client.Outbox`)

- `ClientOutboxMessage`: `Id`, `Peer`, `Correlation?`, `Request` bytes. `Create(peer, SendMessageRequest, correlation = null, id = null)` copies the request, stamps a missing message id, sets `ReturnImmediately = true` so a send does not block for the whole task, and serializes with `A2AJson`. Default id: the message id. `Peer` is a logical name. Credentials are never stored.
- `ClientOutboxEntry` (immutable): the message fields, `Status`, `Attempts`, `InDoubt`, `RemoteTaskId?`, `RemoteContextId?`, `RemoteState?`, `LastError?`, `CreatedAt`, `DueAt`, `CollectedAt?`, `Age`, `LeaseToken?`, `ResultPayload?`, and `GetRequest()` / `GetResult()` (a `SendMessageResponse?`).
- `ClientOutboxOutcome`: `Status` (Finished or Failed), remote ids and state, `Result` bytes, `Error`.
- `abstract class ClientOutboxStore`: abstract class per ADR-001. Takes `TimeSpan`s, not absolute times, so each store uses its own clock. All writes after a lease are fenced by lease token and return false for a stale holder.
  - `EnqueueAsync(message)` returns false when the id exists; the old row is kept.
  - `GetAsync(id)`.
  - `LeaseDueAsync(max, lease)`: `Queued` or `Tracking`, due, lease free. Increments `Attempts`. Returns the `InDoubt` value from *before* this lease, and sets it true for `Queued`.
  - `RescheduleAsync(leased, delay, error, inDoubt)`: clears the lease.
  - `MarkTrackingAsync(leased, taskId, contextId, state, delay)`: `Attempts` = 0, `InDoubt` = false, clears the lease.
  - `FinishAsync(leased, outcome)`: clears the lease.
  - `LeaseResultsAsync(correlation?, max, lease)`: `Finished` or `Failed`, not collected, lease free, oldest first.
  - `AcknowledgeAsync(leased)`: sets `CollectedAt`.
  - `HastenAsync(peer, taskId)`: makes a `Tracking` entry due now.
  - `PurgeCollectedAsync(olderThan, max)`.
- `InMemoryClientOutboxStore`: thread-safe, `TimeProvider`.
- `ClientOutboxOptions`, validated in the dispatcher constructor:

| Option | Default | Note |
|---|---|---|
| `SweepInterval` | 1 s | `Infinite` turns the timer off |
| `BatchSize` | 50 | |
| `MaxConcurrency` | 8 | |
| `Lease` | 5 min | Must be at least 2x `CallTimeout` |
| `CallTimeout` | 60 s | A send or `GetTask`; a timed-out send is in doubt |
| `RetryBaseDelay` / `RetryMaxDelay` | 1 s / 5 min | Exponential with jitter; `Retry-After` wins |
| `PollInterval` / `MaxPollInterval` | 5 s / 5 min | Backoff by attempts |
| `MaxQueueAge` | 24 h | From enqueue, then `Failed`. Keep inside the peer's idempotency retention |
| `MaxTrackingAge` | 7 d | From enqueue, then `Failed`; `RemoteTaskId` stays |
| `RetryInDoubt` | true | |
| `CollectedRetention` | 7 d | Also how long an id stays deduplicated |
| `PurgeInterval` | 1 h | |

- `ClientOutboxDispatcher(store, Func<string, CancellationToken, ValueTask<IA2AClient>> clients, options?, time?, logger?)`: `IAsyncDisposable`. `RunOnceAsync` is one sweep (the timer calls it; tests call it directly with the timer off). `NotifyAsync(peer, PushDelivery)` is a hint: a final state calls `HastenAsync`. `GetTask` remains the truth. Dispose stops the timer, cancels in-flight work and waits for it. Nothing is written for cancelled work, the same as a crash: the lease runs out.

## Sending: how failures are sorted

| Failure | Result |
|---|---|
| `A2AException` 503, 429, or `retryable`; 401, 403, 408 | Retry, not in doubt. `Retry-After` honoured |
| Other `A2AException` with HTTP status 5xx | Retry, in doubt |
| Any other `A2AException` (peer refused: 4xx, protocol error, key mismatch) | `Failed` |
| `HttpRequestException` that provably never reached the server (`RetryPolicy.ShouldRetry(UnsentOnly, ...)`) | Retry, not in doubt |
| Any other exception, including our call timeout | Retry, in doubt |
| Dispatcher token cancelled | Write nothing |
| `clients` resolver throws | Retry, not in doubt |

`Age > MaxQueueAge` is checked after a failure and when an entry is leased, then `Failed`. On lease: `InDoubt && !RetryInDoubt` goes to `Failed` without calling the peer.

## Tracking

`GetTask` leaves `HistoryLength` unset, because the stored result may need history. Final goes to `Finished`, stored as a `SendMessageResponse` holding the task. Not final: `MarkTrackingAsync` with poll backoff. Error: reschedule. `TaskNotFound`: `Failed`. Peer identity is `(peer, task id)`, because task ids from two peers can collide.

## Reuse of the push code

The `IsFinal` rule and `Inspect` of a `StreamResponse` move to one internal helper, used by `PushTaskTracker` and the dispatcher. `NotifyAsync` takes the `PushDelivery` that `PushNotificationReceiver` already hands over, and its `IPushDeliveryStore` keeps deduplicating webhooks.

We do not wrap `PushTaskTracker` itself: one tracker has one client and keys by task id alone, its locks are per process, and it removes an entry on a final webhook without the result. The ledger needs many peers, leases across instances and the final task stored. A lost webhook does not matter here because the ledger polls.

## Postgres (`A2A.Client.Postgres`)

Table `a2a_client_outbox` in a configurable schema. Script idempotent; `PostgresClientOutboxSchema.ScriptFor` / `EnsureSchemaAsync` (advisory lock), like `PostgresSchema`. Ticks, `"C"` collation, `bytea` payloads, partial indexes for due, results, `(peer, remote_task_id)` and purge.

`PostgresClientOutboxStore(dataSource, options?, time?)`. Leasing uses `FOR UPDATE SKIP LOCKED`. Lease token is a uuid. `UseDatabaseClock` defaults to true, as on the server. The clock helper is copied small, because the server package must not be referenced.

**The app's own transaction:** overloads `EnqueueAsync(message, NpgsqlConnection, NpgsqlTransaction?, ct)` and `AcknowledgeAsync(leased, NpgsqlConnection, NpgsqlTransaction?, ct)`. Rollback means nothing is sent.

No DI extension and no hosted service in this change: the dispatcher starts its own timer, so an app holds one as a singleton.

## Out of scope (follow-ups)

Ordering between entries, trace context carried across the queue, a cancel API for queued entries, DI registration, other databases.

## Tests

Contract suite run against in-memory and Postgres (`tests/A2A.Stores.Tests`). Fake clock, no fixed delays. Crash points:

1. Enqueue committed, crash before send: a new dispatcher sends.
2. App transaction rolled back: nothing is sent (Postgres).
3. Crash after the peer took it, before the store write: the lease runs out, the resend has the same key, the peer has one task (end to end over a real in-process server).
4. Crash after `MarkTracking`: a new dispatcher polls by stored task id and does not send again.
5. Result stored, not collected, restart: harvest returns it. Crash before ack: returned again after the lease.
6. Stale lease holder writes nothing.
7. Two dispatchers on one Postgres table: each entry sent once.
8. `RetryInDoubt = false`: crash mid-send gives `Failed`, no second call.
