# ClientOutbox sample

A shop asks a billing agent to invoice each order, through the client outbox in PostgreSQL
(`RomanAmbinder.A2A.Client.Postgres`). PostgreSQL runs in Docker on `127.0.0.1:5433`. The billing agent runs in the
same process on `127.0.0.1:5390`.

| Step | What it shows |
|---|---|
| `Setup` | `EnsureSchemaAsync`, `PostgresClientOutboxStore`, one `ClientOutboxDispatcher` with a peer resolver |
| `Enqueue` | the order row and the outgoing message in one transaction |
| A rolled-back order | the same writes, then a rollback: no row, and nothing is sent |
| `Harvest` | lease the results, update the order and acknowledge in one transaction |

The sample checks that the committed order was invoiced exactly once and its status became `Completed`, and that the
rolled-back order has no row and never reached the agent.

```bash
docker compose -f samples/ClientOutbox/docker-compose.yml up -d --wait
dotnet run --project samples/ClientOutbox
docker compose -f samples/ClientOutbox/docker-compose.yml down -v
```

Expected output ends with:

```text
order-<id>: Completed
billing agent invoiced: order-<id>
OK: the committed order was invoiced once and its result harvested; the rolled-back order sent nothing.
```

It takes a few seconds: the dispatcher sends at once, then asks the agent for the task every 5 seconds
(`PollInterval`) until it is final. The database password is a demo value. Anonymous access to the agent is on
because it is loopback only.

See the [client outbox guide](../../docs/site/guides/client-outbox.md).
