using A2A.Client;
using A2A.Server;
using A2A.TestKit;

namespace A2A.DocSnippets.Testing;

internal sealed class GreeterAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        await updater.StartWorkAsync(cancellationToken: cancellationToken);
        await updater.AddArtifactAsync([Part.FromText($"Hello, {context.UserText}")], name: "greeting", cancellationToken: cancellationToken);
        await updater.CompleteAsync("Done", cancellationToken);
    }
}

internal static class Snippets
{
    public static async Task StartHostAsync()
    {
        #region StartHost
        // The agent as a type, as an instance, as a delegate, or with the full server builder.
        await using var fromType = await A2ATestHost.StartAsync<GreeterAgent>();
        await using var fromInstance = await A2ATestHost.StartAsync(new GreeterAgent());
        await using var fromDelegate = await A2ATestHost.StartAsync(async (context, updater, ct) =>
            await updater.CompleteAsync($"You said: {context.UserText}", ct));
        await using var fromBuilder = await A2ATestHost.StartWithBuilderAsync(builder => builder.AddAgentExecutor<GreeterAgent>());
        #endregion
    }

    public static async Task SendAsync()
    {
        #region Send
        await using var host = await A2ATestHost.StartAsync<GreeterAgent>();
        var client = host.CreateClient(); // JSON-RPC; use ProtocolBindings.HttpJson for the other binding

        var response = await client.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("Ada") });

        var task = A2AAssert.HasState(response, TaskState.Completed);
        A2AAssert.HasArtifact(task, "greeting");
        #endregion
    }

    public static async Task OptionsAsync()
    {
        #region Options
        await using var host = await A2ATestHost.StartAsync<GreeterAgent>(options =>
        {
            options.ConfigureCard = card => card.Name = "Greeter";
            options.ConfigureServer = server => server.MaxTaskDuration = TimeSpan.FromMinutes(5);
            options.RequireAuthentication = true;
        });

        // Anonymous callers are refused. A caller with a user name is authenticated as that user.
        await A2AAssert.ThrowsA2AAsync(
            () => host.CreateClient().SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("Ada") }),
            A2AErrorCode.Unauthenticated);
        var alice = host.CreateClient(user: "alice");
        #endregion
        _ = alice;
    }

    public static async Task StreamAsync()
    {
        #region Stream
        await using var host = await A2ATestHost.StartAsync<GreeterAgent>();
        var client = host.CreateClient();

        var events = await client
            .SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("Ada") })
            .CollectAsync(); // never hangs: it throws with the events so far after 30 seconds

        A2AAssert.StateOrder(events, TaskState.Submitted, TaskState.Working, TaskState.Completed);
        A2AAssert.EventOrder(events,
            ExpectedEvent.Task(TaskState.Submitted),
            ExpectedEvent.Status(TaskState.Working),
            ExpectedEvent.Artifact("greeting"),
            ExpectedEvent.Status(TaskState.Completed));
        A2AAssert.ContainsEventOrder(events, ExpectedEvent.Artifact(), ExpectedEvent.Status(TaskState.Completed));
        #endregion
    }

    public static async Task PeerAsync()
    {
        #region Peer
        await using var peer = await FakePeerAgent.StartAsync(script => script
            .WhenText("weather", r => r.Working().Artifact("Sunny", "forecast").Complete())
            .WhenText("book", r => r.RequireInput("Which day?"))
            .Enqueue(r => r.Fail("Out of service")) // used once, for the next call, before any rule
            .Otherwise(r => r.Reply("I do not know")));

        // Give your agent the peer's client, then run your agent as usual.
        IA2AClient peerClient = peer.CreateClient();
        await using var host = await A2ATestHost.StartAsync(new GreeterAgent());
        #endregion
        _ = (peerClient, host);
    }

    public static async Task PeerCallsAsync()
    {
        await using var peer = await FakePeerAgent.StartAsync(script => script.Otherwise(r => r.Reply("ok")));
        #region PeerCalls
        var call = await peer.WaitForCallAsync(c => c.Text.Contains("weather", StringComparison.Ordinal));
        Console.WriteLine($"{call.Text} (new task: {call.IsNewTask})");

        // A call that no rule matched makes the peer fail the task, so a test fails instead of hanging.
        Console.WriteLine(peer.UnmatchedCalls.Count);

        // The peer has a card, so a client can find it the way a real client does.
        var viaCard = await A2AClientFactory.CreateAsync(peer.Url, peer.CreateHttpClient());
        #endregion
        _ = viaCard;
    }

    public static async Task ClockAsync()
    {
        #region Clock
        var clock = new TestClock();
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await Task.Delay(TimeSpan.FromHours(1), clock, ct); // the agent waits on the fake clock too
            await updater.CompleteAsync("late", ct);
        }, options =>
        {
            options.TimeProvider = clock;
            options.ConfigureServer = server => server.MaxTaskDuration = TimeSpan.FromMinutes(10);
        });

        var client = host.CreateClient();
        var response = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("go"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        });
        var task = A2AAssert.IsTask(response);

        clock.Advance(TimeSpan.FromMinutes(11)); // the task passes MaxTaskDuration
        await A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Failed);
        #endregion
    }

    public static async Task PushAsync()
    {
        #region Push
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
            await updater.CompleteAsync("done", ct));
        var client = host.CreateClient();

        await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("go"),
            Configuration = new SendMessageConfiguration
            {
                ReturnImmediately = true,
                TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = TestPushReceiver.DefaultUrl, Token = "my-token" },
            },
        });

        // Waits for a delivery that already came or comes later. It does not poll.
        var delivery = await host.Push.WaitForAsync(d => d.Event is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });
        Console.WriteLine($"{delivery.Token} {delivery.DeliveryId} {delivery.Sequence}");

        // To make the host retry, answer with a failure first.
        host.Push.Respond = _ => System.Net.HttpStatusCode.ServiceUnavailable;
        #endregion
    }

    public static async Task IdleAsync(A2ATestHost host)
    {
        #region Idle
        // Waits until no agent runs and no push delivery is pending.
        await host.WaitIdleAsync(TimeSpan.FromSeconds(10));
        #endregion
    }
}
