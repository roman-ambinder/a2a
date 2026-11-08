using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Event sequences seen end to end. The store behavior itself is in the contract suite (tests/A2A.Stores.Tests).</summary>
public class EventStoreTests
{
    /// <summary>An encoded event; its sequence (0) is assigned by the store.</summary>
    private static SequencedEvent Evt(string taskId) => EventEncoder.Encode(0, StreamResponse.Of(new AgentTask
    {
        Id = taskId, ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
    }));

    private static async Task<List<SequencedEvent>> ReadAllAsync(EventStore store, TaskKey key, long after = 0)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in store.ReadAfterAsync(key, after)) list.Add(e);
        return list;
    }

    /// <summary>In-memory specific: the store keeps the instance and bytes it is given (other stores return copies).</summary>
    [Fact]
    public async Task Append_keeps_event_and_bytes_with_assigned_sequence()
    {
        var store = new InMemoryEventStore(new StreamingOptions());
        var key = new TaskKey("", "alice", "t");
        await store.AppendAsync(key, Evt("t"));
        var appended = Evt("t") with { Sequence = 99 }; // the input sequence is ignored

        Assert.Equal(2, await store.AppendAsync(key, appended));

        var stored = (await ReadAllAsync(store, key, after: 1)).Single();
        Assert.Equal(2, stored.Sequence);
        Assert.Same(appended.Event, stored.Event);
        Assert.True(appended.Json.Equals(stored.Json), "the store keeps the bytes it is given, not a new encoding");
    }

    [Theory]
    [InlineData("JSONRPC")]
    [InlineData("HTTP+JSON")]
    public async Task Push_sequence_header_matches_event_sequence(string binding)
    {
        var route = binding.Replace("+", "-");
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var config = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = $"http://hooks.test/{route}", Token = "t" },
        };
        var task = (await client.SendMessageAsync(Message.FromUser("task"), config)).RequireTask();
        await host.WaitIdleAsync();

        var headerSeqs = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/{route}")
            .Select(c => long.Parse(c.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single())).ToList();
        var store = host.App.Services.GetRequiredService<EventStore>();
        var stored = await ReadAllAsync(store, new TaskKey("", "alice", task.Id));

        Assert.Equal(5, headerSeqs.Count);
        Assert.Equal(stored.Select(e => e.Sequence), headerSeqs);
        Assert.Equal([1L, 2, 3, 4, 5], headerSeqs);
    }
}
