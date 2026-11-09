using A2A;
using A2A.TestKit;

namespace TestingAgent;

// These tests use A2A.TestKit. xUnit is only the test framework here: the kit works with any other one.
public sealed class ResearchAgentTests
{
    [Fact]
    public async Task Asks_the_peer_and_reports_the_answer()
    {
        // FakePeerAgent: an agent that runs in the test process and answers as you script.
        await using var peer = await FakePeerAgent.StartAsync(script => script
            .WhenText("tides", reply => reply.Reply("Tides come from the Moon.")));

        // A2ATestHost: your agent on a real server, without a network port. The agent gets a client for the peer.
        await using var host = await A2ATestHost.StartAsync(new ResearchAgent(peer.CreateClient()));
        var client = host.CreateClient();

        // CollectAsync reads the stream to its end, and never hangs.
        var events = await client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("tides") }).CollectAsync();

        // EventOrder checks the exact events. The failure message prints the expected and the actual ones.
        A2AAssert.EventOrder(events,
            ExpectedEvent.Task(TaskState.Submitted),
            ExpectedEvent.Status(TaskState.Working),
            ExpectedEvent.Artifact("report"),
            ExpectedEvent.Status(TaskState.Completed));

        // The peer records what it was asked.
        var call = Assert.Single(peer.Calls);
        Assert.Contains("tides", call.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_when_the_peer_fails()
    {
        await using var peer = await FakePeerAgent.StartAsync(script => script
            .Otherwise(reply => reply.Fail("The peer is down")));
        await using var host = await A2ATestHost.StartAsync(new ResearchAgent(peer.CreateClient()));

        var response = await host.CreateClient().SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("anything") });

        // HasState checks the response is a task in that state, and returns the task.
        var task = A2AAssert.HasState(response, TaskState.Failed);
        Assert.Equal("The peer could not help.", task.Status.Message!.GetText());
    }

    [Fact]
    public async Task Sends_push_notifications_for_the_task()
    {
        await using var peer = await FakePeerAgent.StartAsync(script => script.Otherwise(reply => reply.Reply("42")));
        await using var host = await A2ATestHost.StartAsync(new ResearchAgent(peer.CreateClient()));

        // Register the kit's webhook URL. The host sends push notifications to host.Push instead of the network.
        var response = await host.CreateClient().SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("the answer"),
            Configuration = new SendMessageConfiguration
            {
                ReturnImmediately = true,
                TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = TestPushReceiver.DefaultUrl, Token = "t-1" },
            },
        });
        var task = A2AAssert.IsTask(response);

        // WaitForAsync does not poll. It returns the first delivery that matches, past or future.
        var done = await host.Push.WaitForAsync(d => d.Event is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });
        Assert.Equal("t-1", done.Token);
        Assert.Equal(task.Id, ((StatusUpdateEvent)done.Event).StatusUpdate.TaskId);
    }

    [Fact]
    public async Task Fails_a_task_that_runs_past_MaxTaskDuration()
    {
        // TestClock only moves when you advance it. The host reads it for MaxTaskDuration.
        var clock = new TestClock();
        var limit = TimeSpan.FromMinutes(5);
        await using var peer = await FakePeerAgent.StartAsync(script => script
            .Otherwise(reply => reply.WaitFor(new TaskCompletionSource().Task).Reply("never"))); // the peer never answers
        await using var host = await A2ATestHost.StartAsync(new ResearchAgent(peer.CreateClient()), options =>
        {
            options.TimeProvider = clock;
            options.ConfigureServer = server => server.MaxTaskDuration = limit;
        });
        var client = host.CreateClient();
        var response = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("slow topic"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        });
        var task = A2AAssert.IsTask(response);
        await peer.WaitForCallAsync(); // the agent is now waiting for the peer

        clock.Advance(limit + TimeSpan.FromSeconds(1));

        var failed = await A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Failed);
        Assert.Contains("time limit", failed.Status.Message!.GetText(), StringComparison.Ordinal);
    }
}
