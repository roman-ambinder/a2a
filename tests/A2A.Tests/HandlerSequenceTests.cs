using System.Security.Claims;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Emits artifacts from both ExecuteAsync and CancelAsync, so two executions append to one task at the same time.</summary>
public sealed class InterleavingAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        await updater.StartWorkAsync(cancellationToken: default);
        for (var i = 0; i < 60; i++)
        {
            await updater.AddArtifactAsync([Part.FromText("x")], artifactId: $"e{i}", cancellationToken: default);
            await Task.Delay(1);
        }
    }

    public async Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            await updater.AddArtifactAsync([Part.FromText("c")], artifactId: $"c{i}", cancellationToken: default);
            await Task.Delay(1);
        }
        await updater.CancelAsync(cancellationToken: default);
    }
}

public class HandlerSequenceTests
{
    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    private static async Task<List<SequencedEvent>> StoredAsync(TestAgentHost host, string taskId)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(new TaskKey("", "alice", taskId), 0)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Concurrent_executions_publish_in_sequence_order_to_subscribers_and_push()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddScoped<IAgentExecutor, InterleavingAgent>());
        for (var round = 0; round < 8; round++)
        {
            var route = $"seq{round}";
            var request = new SendMessageRequest
            {
                Message = Message.FromUser("go"),
                Configuration = new SendMessageConfiguration
                {
                    TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = $"http://hooks.test/{route}" },
                },
            };
            await using var stream = host.Handler.SendStreamingMessageAsync(request, Call()).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            var taskId = Assert.IsType<TaskEvent>(stream.Current.Event).Task.Id;

            var live = new List<SequencedEvent>();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var collector = Task.Run(async () =>
            {
                await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()))
                {
                    live.Add(e);
                    first.TrySetResult();
                }
            });
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, Call());
            await collector.WaitAsync(TimeSpan.FromSeconds(10));
            await host.WaitIdleAsync();

            Assert.True(live.Count > 1);
            for (var i = 1; i < live.Count; i++) Assert.Equal(live[i - 1].Sequence + 1, live[i].Sequence);
            Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(live[^1].Event).StatusUpdate.Status.State);

            var headers = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/{route}")
                .Select(c => long.Parse(c.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single())).ToList();
            Assert.Equal(Enumerable.Range(1, headers.Count).Select(i => (long)i), headers);
            Assert.Equal((await StoredAsync(host, taskId)).Select(e => e.Sequence), headers);
        }
    }

    [Fact]
    public async Task SubscribeToTask_sends_snapshot_then_only_later_events()
    {
        await using var host = await TestAgentHost.StartAsync();
        await using var stream = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("slow") }, Call()).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(1, stream.Current.Sequence);
        var taskId = Assert.IsType<TaskEvent>(stream.Current.Event).Task.Id;
        Assert.True(await stream.MoveNextAsync()); // Working, sequence 2
        Assert.Equal(2, stream.Current.Sequence);

        var received = new List<SequencedEvent>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = Task.Run(async () =>
        {
            await foreach (var e in host.Handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, Call()))
            {
                received.Add(e);
                first.TrySetResult();
            }
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, Call());
        await collector.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([2L, 3L], received.Select(e => e.Sequence));
        Assert.IsType<TaskEvent>(received[0].Event);
        Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(received[1].Event).StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Dropped_event_is_not_in_the_event_log()
    {
        var store = new FlakyTaskStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore>(store));
        store.CancelThenConflictOnNextUpdate(); // conflict, then the reloaded task is terminal: the event is dropped
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        var stored = await StoredAsync(host, task.Id);
        Assert.Equal([1L], stored.Select(e => e.Sequence)); // only the initial snapshot
        Assert.IsType<TaskEvent>(stored[0].Event);
    }
}
