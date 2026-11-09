# RomanAmbinder.A2A.Client.Postgres

PostgreSQL store for the A2A (Agent2Agent) client outbox (`A2A.Client.Outbox` in `RomanAmbinder.A2A.Client`). Plain Npgsql, no EF. It does not reference the server packages.

The outbox is a durable list of the messages your agent sends to peers: queued, sent and tracked, final result stored, result collected. It survives a restart, and you can write an entry in your own database transaction. If the transaction rolls back, nothing is sent.

```csharp
await PostgresClientOutboxSchema.EnsureSchemaAsync(dataSource);           // or apply PostgresClientOutboxSchema.ScriptFor("<schema>") yourself
var store = new PostgresClientOutboxStore(dataSource);
await using var dispatcher = new ClientOutboxDispatcher(store, (peer, ct) => new(clientFor(peer)));  // keep one per process

// In your own transaction:
await using var conn = await dataSource.OpenConnectionAsync();
await using var tx = await conn.BeginTransactionAsync();
// ... your own writes ...
await store.EnqueueAsync(ClientOutboxMessage.Create("billing-agent", request), conn, tx);
await tx.CommitAsync();

// Later, collect results (at least once; acknowledge in your own transaction to make it exactly once for your data):
foreach (var result in await store.LeaseResultsAsync(null, 10, TimeSpan.FromMinutes(1)))
{
    // ... use result.GetResult() ...
    await store.AcknowledgeAsync(result);
}
```

Read the [client outbox guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/client-outbox.md) for the delivery guarantee and what "in doubt" means.

## Table

`PostgresClientOutboxSchema` creates `a2a_client_outbox` in the schema you choose (`PostgresClientOutboxOptions.Schema`, default `public`). The script is idempotent and runs under an advisory lock, so many instances can start at once.

| Column | Holds |
|---|---|
| `id` (key) | The entry id. It is the `Idempotency-Key` of every send. |
| `peer`, `correlation` | The logical peer name and your own label. |
| `request`, `result` | The `SendMessageRequest` and the final `SendMessageResponse`, as JSON bytes. |
| `status`, `attempts`, `in_doubt` | Queued 0, tracking 1, finished 2, failed 3. In doubt is set when a queued entry is leased. |
| `remote_task_id`, `remote_context_id`, `remote_state` | What the peer answered. |
| `created_ticks`, `due_ticks`, `collected_ticks` | UTC ticks (`bigint`). |
| `lease_ticks`, `lease_token` | Who works on the entry now. Writes carry the token, so a holder whose lease was taken over changes nothing. |

Request and result bodies are stored in this table. They may hold personal data, so protect the table like the messages themselves. Credentials are never stored: the entry has a peer name, and your code turns it into a client.

## Behavior

- Leasing uses `FOR UPDATE SKIP LOCKED`, so several dispatchers can share one table and each entry is sent by one of them at a time.
- Due times, leases and ages use the database clock (`statement_timestamp()`). `PostgresClientOutboxOptions.UseDatabaseClock = false` uses the `TimeProvider` you pass, for tests with a fake clock.
- No DI extension and no hosted service. The dispatcher runs its own timer, so keep one per process.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
