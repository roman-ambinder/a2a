using A2A.Client;
using A2A.Client.Outbox;
using A2A.Client.Postgres;
using A2A.Client.Push;
using Npgsql;

namespace A2A.DocSnippets.ClientOutbox;

public static class Samples
{
    #region Setup
    public static async Task<(PostgresClientOutboxStore Store, ClientOutboxDispatcher Dispatcher)> StartAsync(
        NpgsqlDataSource dataSource, Func<string, IA2AClient> clientFor)
    {
        await PostgresClientOutboxSchema.EnsureSchemaAsync(dataSource);   // or apply PostgresClientOutboxSchema.Script yourself
        var store = new PostgresClientOutboxStore(dataSource);

        // The resolver turns a peer name into a client. The client brings its own credentials; none are stored.
        var dispatcher = new ClientOutboxDispatcher(store, (peer, _) => new(clientFor(peer)), new ClientOutboxOptions
        {
            MaxQueueAge = TimeSpan.FromHours(12),   // keep it inside the peer's idempotency retention
        });
        return (store, dispatcher);                 // keep one dispatcher per process
    }
    #endregion

    #region Enqueue
    public static async Task PlaceOrderAsync(NpgsqlDataSource dataSource, PostgresClientOutboxStore store, string orderId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var insert = new NpgsqlCommand("INSERT INTO orders (id, status) VALUES ($1, 'pending')", connection, transaction))
        {
            insert.Parameters.AddWithValue(orderId);
            await insert.ExecuteNonQueryAsync();
        }

        var request = new SendMessageRequest { Message = Message.FromUser($"Please invoice order {orderId}") };
        await store.EnqueueAsync(ClientOutboxMessage.Create("billing-agent", request, correlation: orderId), connection, transaction);

        await transaction.CommitAsync();   // the order and the message exist together, or not at all
    }
    #endregion

    #region Harvest
    public static async Task CollectResultsAsync(NpgsqlDataSource dataSource, PostgresClientOutboxStore store)
    {
        foreach (var entry in await store.LeaseResultsAsync(correlation: null, max: 20, lease: TimeSpan.FromMinutes(1)))
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            // Change your own state and acknowledge in one transaction: your data sees each result once.
            var status = entry.Status == ClientOutboxStatus.Finished ? entry.GetResult() switch
            {
                TaskResponse t => t.Task.Status.State.ToString(),
                MessageResponse => "replied",
                _ => "unknown",
            } : "failed: " + entry.LastError;
            await using (var update = new NpgsqlCommand("UPDATE orders SET status = $1 WHERE id = $2", connection, transaction))
            {
                update.Parameters.AddWithValue(status);
                update.Parameters.AddWithValue(entry.Correlation!);
                await update.ExecuteNonQueryAsync();
            }

            if (await store.AcknowledgeAsync(entry, connection, transaction)) await transaction.CommitAsync();
            else await transaction.RollbackAsync();   // the lease ran out and another worker has the result
        }
    }
    #endregion

    #region Webhook
    public static Task OnWebhookAsync(ClientOutboxDispatcher dispatcher, PushDelivery delivery) =>
        dispatcher.NotifyAsync("billing-agent", delivery);   // a hint: the dispatcher still asks GetTask
    #endregion

    #region InMemory
    public static ClientOutboxStore ForTests() => new InMemoryClientOutboxStore();
    #endregion
}
