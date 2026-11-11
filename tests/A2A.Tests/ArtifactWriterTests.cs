using A2A.Client;

namespace A2A.Tests;

public class ArtifactWriterTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<List<TaskArtifactUpdateEvent>> ArtifactEvents(string text)
    {
        var events = new List<TaskArtifactUpdateEvent>();
        await foreach (var evt in _host.Client(ProtocolBindings.JsonRpc).SendStreamingMessageAsync(Message.FromUser(text)))
            if (evt is ArtifactUpdateEvent { ArtifactUpdate: var a }) events.Add(a);
        return events;
    }

    [Fact]
    public async Task First_write_creates_later_writes_append_and_completion_seals_before_the_task_ends()
    {
        var events = await ArtifactEvents("writer");
        Assert.Equal(3, events.Count);
        Assert.False(events[0].Append);
        Assert.Equal("story", events[0].Artifact.Name);
        Assert.Equal("a short story", events[0].Artifact.Description);
        Assert.Equal("once ", events[0].Artifact.GetText());
        Assert.True(events[1].Append);
        Assert.Equal("upon", events[1].Artifact.GetText());
        Assert.False(events[1].LastChunk);
        Assert.True(events[2].Append);
        Assert.True(events[2].LastChunk);
        Assert.Empty(events[2].Artifact.Parts);
        Assert.All(events, e => Assert.Equal("w1", e.Artifact.ArtifactId));

        var task = (await _host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("writer")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        var artifact = Assert.Single(task.Artifacts!);
        Assert.Equal("once upon", artifact.GetText());
        Assert.Equal("story", artifact.Name);
    }

    [Fact]
    public async Task Explicit_completion_seals_once_and_an_unused_writer_sends_nothing()
    {
        var events = await ArtifactEvents("writer-explicit");
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("w2", e.Artifact.ArtifactId));
        Assert.True(events[1].LastChunk);
    }
}
