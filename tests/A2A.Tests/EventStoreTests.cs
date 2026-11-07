using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Event sequences seen end to end. The store behavior itself is in the contract suite (tests/A2A.Stores.Tests).</summary>
public class EventStoreTests
{
    private static async Task<List<SequencedEvent>> ReadAllAsync(EventStore store, TaskKey key, long after = 0)
    {
        var list = new List<SequencedEvent>();
        await foreach (var e in store.ReadAfterAsync(key, after)) list.Add(e);
        return list;
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
