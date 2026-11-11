using A2A.Client;

namespace A2A.Tests;

/// <summary>AskAsync, StreamTextAsync and the GetText/State helpers on the protocol model.</summary>
public class ClientTextHelperTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task AskAsync_returns_direct_reply_text(string binding) =>
        Assert.Equal("hi there", await _host.Client(binding).AskAsync("hello"));

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task AskAsync_returns_artifact_text_of_completed_task(string binding) =>
        Assert.Equal("part1part2", await _host.Client(binding).AskAsync("task"));

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task AskAsync_throws_when_input_is_required(string binding)
    {
        var ex = await Assert.ThrowsAsync<TaskNotCompletedException>(() => _host.Client(binding).AskAsync("input"));
        Assert.Equal(TaskState.InputRequired, ex.State);
        Assert.Contains("need more", ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(ex.Task.Id));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task AskAsync_throws_when_task_fails(string binding)
    {
        var ex = await Assert.ThrowsAsync<TaskNotCompletedException>(() => _host.Client(binding).AskAsync("fail"));
        Assert.Equal(TaskState.Failed, ex.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task StreamTextAsync_yields_artifact_chunks(string binding)
    {
        var chunks = new List<string>();
        await foreach (var chunk in _host.Client(binding).StreamTextAsync("task")) chunks.Add(chunk);
        Assert.Equal(["part1", "part2"], chunks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task StreamTextAsync_yields_direct_reply(string binding)
    {
        var chunks = new List<string>();
        await foreach (var chunk in _host.Client(binding).StreamTextAsync("hello")) chunks.Add(chunk);
        Assert.Equal(["hi there"], chunks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task StreamTextAsync_throws_after_stream_when_input_is_required(string binding)
    {
        var ex = await Assert.ThrowsAsync<TaskNotCompletedException>(async () =>
        {
            await foreach (var _ in _host.Client(binding).StreamTextAsync("input")) { }
        });
        Assert.Equal(TaskState.InputRequired, ex.State);
        Assert.False(string.IsNullOrEmpty(ex.Task.Id));
        Assert.False(string.IsNullOrEmpty(ex.Task.ContextId));
    }

    [Fact]
    public void Stream_event_State_covers_task_and_status_events()
    {
        var status = new AgentTaskStatus { State = TaskState.Completed };
        Assert.Equal(TaskState.Completed, StreamResponse.Of(new AgentTask { Id = "t", Status = status }).State);
        Assert.Equal(TaskState.Completed, StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", Status = status }).State);
        Assert.Null(StreamResponse.Of(Message.FromAgent("x")).State);
    }

    [Fact]
    public void GetText_reads_text_parts_only()
    {
        var artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("a"), Part.FromData(default), Part.FromText("b")] };
        Assert.Equal("ab", artifact.GetText());
        var task = new AgentTask
        {
            Id = "t", Status = new AgentTaskStatus { State = TaskState.Completed, Message = Message.FromAgent("status") },
            Artifacts = [artifact, new Artifact { ArtifactId = "c", Parts = [Part.FromText("c")] }]
        };
        Assert.Equal("abc", task.GetText());
        Assert.Equal("abc", new TaskResponse(task).GetText());
        Assert.Equal("hi", new MessageResponse(Message.FromAgent("hi")).GetText());
        Assert.Equal("", new AgentTask { Id = "t", Status = new AgentTaskStatus() }.GetText());
        Assert.Equal("", StreamResponse.Of(task).GetText()); // a snapshot repeats earlier text
        Assert.Equal("ab", StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", Artifact = artifact }).GetText());
    }
}
