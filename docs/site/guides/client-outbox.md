# Client outbox

An agent that calls a peer has nothing durable by default. A crash between "save my state" and "call the peer" loses the call. Nothing records "sent, task id X, result not collected". The client outbox fixes this. It keeps one row per outgoing message, and the row survives a restart.

## The life of an entry

```text
Enqueue -> Queued -> (send) -> Tracking -> (poll, or a webhook hint) -> Finished -> acknowledge -> purged
              \                    \
               +----> Failed <-----+     (Failed is collected too)
```

<xref:A2A.Client.Outbox.ClientOutboxStatus> has four values.

- **Queued**: waiting to be sent.
- **Tracking**: the peer took the message and made a task. The dispatcher asks the peer for the task until it is final.
- **Finished**: the peer gave a final answer. The answer is stored. It can be a failed task or an input-required task. You read it and decide. To continue an interrupted task, enqueue a new message with that task id.
- **Failed**: the SDK could not get an answer. The peer refused the send, time ran out, or the outcome is in doubt and you turned off `RetryInDoubt`.

Final means the same as in <xref:A2A.Client.Push.PushTaskTracker>: a terminal or interrupted task, or a message answer.

## Set up

<xref:A2A.Client.Outbox.ClientOutboxDispatcher> sends and tracks. A <xref:A2A.Client.Outbox.ClientOutboxStore> keeps the rows. <xref:A2A.Client.Outbox.InMemoryClientOutboxStore> loses everything on restart, so use it for tests. For real use, install `RomanAmbinder.A2A.Client.Postgres` and use <xref:A2A.Client.Postgres.PostgresClientOutboxStore>.

[!code-csharp[](../../../samples/ClientOutbox/Program.cs#Setup)]

A runnable version with PostgreSQL in Docker is in [samples/ClientOutbox](https://github.com/roman-ambinder/a2a/blob/main/samples/ClientOutbox/README.md).

The dispatcher runs its own timer, so keep one per process. Several instances of your app can share one table. Each entry is leased to one dispatcher at a time.

For tests, build the dispatcher with `SweepInterval = Timeout.InfiniteTimeSpan` and a `FakeTimeProvider`, and call `RunOnceAsync` yourself.

[!code-csharp[](../../../samples/Snippets/ClientOutbox.cs#InMemory)]

## Enqueue in your own transaction

With Postgres you can write the message in the same database transaction as your own state. If the transaction rolls back, nothing is sent.

[!code-csharp[](../../../samples/ClientOutbox/Program.cs#Enqueue)]

<xref:A2A.Client.Outbox.ClientOutboxMessage.Create*> copies your request, adds a message id if it has none, and sets `ReturnImmediately`, so a send does not wait for the whole task. The entry id is the message id unless you pass one. Enqueueing an id that exists returns false and keeps the old entry. The id stays known until the collected entry is purged (`CollectedRetention`).

## Harvest and acknowledge

Results are not pushed to you. Lease them, handle them, then acknowledge. A result you do not acknowledge comes back after its lease runs out, so harvest is at least once. When you acknowledge in the same transaction as your own change, your data sees each result exactly once.

[!code-csharp[](../../../samples/ClientOutbox/Program.cs#Harvest)]

Rows that are never harvested are never purged, so an app must harvest. Pass a correlation to `LeaseResultsAsync` to get only the entries you labelled. `Failed` entries are harvested too. Read `LastError`.

## A webhook is a hint

If you register a push webhook, hand each delivery to `NotifyAsync`. A final state makes the entry due now, so it is polled sooner. The dispatcher still calls `GetTask` and trusts only that answer. A lost webhook does not matter, because the dispatcher polls anyway. See [Push notifications](push-notifications.md).

[!code-csharp[](../../../samples/Snippets/ClientOutbox.cs#Webhook)]

## What is guaranteed

Every send carries `Idempotency-Key` set to the entry id. The SDK server honours the key: a second send with the same key returns the first task and does not run the agent again. So delivery to the peer is at least once, and the peer sees one task.

**In doubt** means a send may have reached the peer without the SDK learning it. A crash or a timeout in the middle of a send does that. The store marks a queued entry in doubt when it is leased, so a crash leaves the mark with no extra write. By default the dispatcher sends again with the same key. With an SDK server that is safe. A peer that ignores the key may create a second task, and only in this case.

On a `Failed` entry, `InDoubt` tells you whether the peer may have the message. A refusal on the first attempt (for example a 400) gives false. An entry that failed because `RetryInDoubt` is off, or that failed after an attempt of unknown outcome, gives true.

Set `RetryInDoubt = false` for at most once. An entry in doubt becomes `Failed` and your app decides.

Keep `MaxQueueAge` inside the retention of the peer's idempotency keys. After that the peer forgets the key, and a late resend of an in-doubt message could create a second task. The default is 24 hours, the same as the default of the SDK server (<xref:A2A.Server.Idempotency.IdempotencyOptions.Retention>). If the peer keeps keys for less, lower it.

A crashed dispatcher only delays its entries: the lease (5 minutes by default) runs out and another dispatcher takes them. Writes after a lease carry a token, so a dispatcher that is too slow cannot overwrite the new holder.

## What is stored

The request and the result are stored in your database as JSON, in the `a2a_client_outbox` table. They can hold personal data, so protect the table like the messages. Credentials are not stored. An entry has a logical peer name, and your resolver turns it into a client with its own credentials. Error text is stored, but never secrets.

## Limits and retries

<xref:A2A.Client.Outbox.ClientOutboxOptions> sets the timings. The important ones:

| Option | Default | Meaning |
|---|---|---|
| `MaxQueueAge` | 24 h | From enqueue. After it a message that was not sent becomes `Failed`. |
| `MaxTrackingAge` | 7 d | From enqueue. After it a tracked task becomes `Failed`. `RemoteTaskId` stays. |
| `CallTimeout` | 60 s | One send or one `GetTask`. A send that times out is in doubt. |
| `Lease` | 5 min | At least twice `CallTimeout`. |
| `RetryInDoubt` | true | See above. |
| `CollectedRetention` | 7 d | How long a collected entry is kept. Also how long its id is deduplicated. |

Failures are sorted like this:

| Failure | What happens |
|---|---|
| 503, 429, 401, 403, 408, or an error marked retryable | Retry, not in doubt. A `Retry-After` from the peer is used, up to `RetryMaxDelay`. |
| Another peer error with HTTP status 5xx | Retry, in doubt. |
| Any other error from the peer (4xx, protocol error, key mismatch) | `Failed`. |
| A connection that never reached the peer | Retry, not in doubt. |
| Anything else, including a timeout | Retry, in doubt. |
| Your client resolver throws | Retry, not in doubt. |

Waits between sends grow with each attempt, with jitter. An earlier doubt is kept until a send succeeds or fails for good.

When the app stops, the dispatcher cancels work in flight and writes nothing for it. This looks the same as a crash, and the lease handles it.
