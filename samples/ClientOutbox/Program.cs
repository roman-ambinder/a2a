using System.Collections.Concurrent;
using A2A;
using A2A.Client;
using A2A.Client.Outbox;
using A2A.Client.Postgres;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Npgsql;

// A shop that asks a billing agent to invoice each order, through the client outbox in PostgreSQL. The order row and
// the outgoing message are written in one transaction: both exist, or neither does. Postgres comes from
// docker-compose.yml next to this file:
//   docker compose -f samples/ClientOutbox/docker-compose.yml up -d --wait
//   dotnet run --project samples/ClientOutbox
// The billing agent runs in this process on 127.0.0.1. The database password is a demo value.

const string connectionString = "Host=127.0.0.1;Port=5433;Database=shop;Username=shop;Password=shop-demo";
await using var dataSource = NpgsqlDataSource.Create(connectionString);

// The shop's own table. The outbox table (a2a_client_outbox) is created by EnsureSchemaAsync below.
await using (var create = dataSource.CreateCommand("CREATE TABLE IF NOT EXISTS orders (id text PRIMARY KEY, status text NOT NULL)"))
    await create.ExecuteNonQueryAsync();

// The billing agent: a peer in this process. AllowAnonymous is fine for a sample that only listens on loopback.
// A real peer needs authentication, and the outbox's client resolver gives each peer client its own credentials.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5390");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<Invoices>();
builder.Services.AddA2A(a => a.AddAgentExecutor<BillingAgent>().AddAgentCard(new AgentCard
    {
        Name = "Billing Agent",
        Description = "Invoices orders.",
        Version = "1.0.0",
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "invoice", Name = "Invoice", Description = "Invoices an order", Tags = ["billing"] }]
    }))
    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);
var app = builder.Build();
app.UseA2A();
app.MapA2A("/a2a");
await app.StartAsync();
Console.WriteLine("Billing agent listening on http://127.0.0.1:5390/a2a");

using var http = new HttpClient();
var clientOptions = new A2AClientOptions();
var billing = new A2AClient(new JsonRpcTransport(http, new Uri("http://127.0.0.1:5390/a2a"), clientOptions), clientOptions);

var (store, dispatcher) = await Shop.StartAsync(dataSource, peer =>
    peer == "billing-agent" ? billing : throw new InvalidOperationException($"No client for peer '{peer}'."));

// 1. An order placed in a transaction that commits: the order and its message exist together.
var placed = $"order-{Guid.NewGuid():N}"[..14];
await Shop.PlaceOrderAsync(dataSource, store, placed);
Console.WriteLine($"placed {placed}: committed");

// 2. An order whose transaction rolls back: no order row, and no message is ever sent.
var abandoned = $"order-{Guid.NewGuid():N}"[..14];
await Shop.PlaceOrderAndRollBackAsync(dataSource, store, abandoned);
Console.WriteLine($"placed {abandoned}: rolled back");

// The dispatcher sends on its own timer, then asks the peer for the task until it is final. The shop harvests the
// results. A real app harvests on a timer or a background loop.
var status = "pending";
var deadline = DateTime.UtcNow.AddSeconds(30);
while (status == "pending" && DateTime.UtcNow < deadline)
{
    await Task.Delay(500);
    await Shop.CollectResultsAsync(dataSource, store);
    status = await Shop.StatusOfAsync(dataSource, placed) ?? "missing";
}

Console.WriteLine($"{placed}: {status}");
var invoices = app.Services.GetRequiredService<Invoices>();
Console.WriteLine($"billing agent invoiced: {string.Join(", ", invoices.Orders)}");

var failures = new List<string>();
if (status != nameof(TaskState.Completed))
    failures.Add($"{placed}: expected status Completed after the harvest, got '{status}'");
if (invoices.Orders.Count(o => o == placed) != 1)
    failures.Add($"{placed}: expected the billing agent to get the message once, got it {invoices.Orders.Count(o => o == placed)} time(s)");
if (invoices.Orders.Contains(abandoned))
    failures.Add($"{abandoned}: the transaction rolled back, but the billing agent got the message");
if (await Shop.StatusOfAsync(dataSource, abandoned) is { } leftover)
    failures.Add($"{abandoned}: the transaction rolled back, but the order row exists ({leftover})");

await dispatcher.DisposeAsync();
await app.StopAsync();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("OK: the committed order was invoiced once and its result harvested; the rolled-back order sent nothing.");
return 0;

/// <summary>The shop's side: the outbox setup, placing an order, and collecting results.</summary>
internal static class Shop
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

    /// <summary>The same writes as <see cref="PlaceOrderAsync" />, then a rollback, as when a later step of the order fails.</summary>
    public static async Task PlaceOrderAndRollBackAsync(NpgsqlDataSource dataSource, PostgresClientOutboxStore store, string orderId)
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
        await transaction.RollbackAsync();
    }

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

    public static async Task<string?> StatusOfAsync(NpgsqlDataSource dataSource, string orderId)
    {
        await using var select = dataSource.CreateCommand("SELECT status FROM orders WHERE id = $1");
        select.Parameters.AddWithValue(orderId);
        return await select.ExecuteScalarAsync() as string;
    }
}

/// <summary>The orders the billing agent invoiced, to check that each message arrived once.</summary>
internal sealed class Invoices
{
    private readonly ConcurrentQueue<string> _orders = new();
    public IReadOnlyCollection<string> Orders => _orders;
    public void Add(string orderId) => _orders.Enqueue(orderId);
}

/// <summary>Invoices the order named in the message. A resend with the same Idempotency-Key does not run it again.</summary>
internal sealed class BillingAgent(Invoices invoices) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var orderId = context.UserText.Split(' ').Last();
        invoices.Add(orderId);
        await updater.StartWorkAsync(cancellationToken: cancellationToken);
        await updater.CompleteAsync($"Invoiced {orderId}.", cancellationToken);
    }
}
