using A2A.Client;

namespace A2A.Tests;

/// <summary>A2AConversation and AgentReply: multi-turn without handling ids, over JSON-RPC and HTTP+JSON.</summary>
public class ConversationTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Direct_reply_is_completed_with_its_text(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var reply = await chat.SendAsync("hello");
        Assert.True(reply.IsCompleted);
        Assert.Equal("hi there", reply.Text);
        Assert.Null(reply.State);
        Assert.NotNull(reply.Message);
        Assert.Null(chat.TaskId);
        Assert.Same(reply, chat.LastReply);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Completed_task_returns_artifact_text_and_starts_no_follow_up_task(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var reply = await chat.SendAsync("task");
        Assert.True(reply.IsCompleted);
        Assert.Equal(TaskState.Completed, reply.State);
        Assert.Equal("part1part2", reply.Text);
        Assert.False(string.IsNullOrEmpty(chat.ContextId));
        Assert.Null(chat.TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Answering_input_required_continues_the_same_task(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var first = await chat.SendAsync("input");
        Assert.True(first.NeedsInput);
        Assert.False(first.IsCompleted);
        Assert.Equal("need more", first.StatusText);
        Assert.Equal(first.TaskId, chat.TaskId);
        Assert.Equal(first.ContextId, chat.ContextId);

        var second = await chat.SendAsync("blue");
        Assert.True(second.IsCompleted);
        Assert.Equal("got: blue", second.StatusText);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(first.ContextId, second.ContextId);
        Assert.Null(chat.TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Agent_can_ask_again_before_completing(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var reply = await chat.SendAsync("input");
        reply = await chat.SendAsync("ask-again");
        Assert.True(reply.NeedsInput);
        Assert.Equal("need even more", reply.StatusText);
        reply = await chat.SendAsync("done");
        Assert.True(reply.IsCompleted);
        Assert.Equal("got: done", reply.StatusText);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Next_message_after_completion_starts_a_new_task_in_the_same_context(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var first = await chat.SendAsync("task");
        var second = await chat.SendAsync("task");
        Assert.Equal(first.ContextId, second.ContextId);
        Assert.NotEqual(first.TaskId, second.TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Failed_task_is_returned_and_EnsureCompleted_throws(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var reply = await chat.SendAsync("fail");
        Assert.Equal(TaskState.Failed, reply.State);
        Assert.False(reply.IsCompleted);
        var ex = Assert.Throws<TaskNotCompletedException>(() => reply.EnsureCompleted());
        Assert.Equal(TaskState.Failed, ex.State);
        Assert.Null(chat.TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_turns_keep_the_ids_and_answer_input_required(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        await foreach (var _ in chat.StreamTextAsync("input")) { }
        Assert.NotNull(chat.LastReply);
        Assert.True(chat.LastReply!.NeedsInput);
        Assert.Equal("need more", chat.LastReply.StatusText);
        Assert.False(string.IsNullOrEmpty(chat.TaskId));
        Assert.False(string.IsNullOrEmpty(chat.ContextId));

        var reply = await chat.SendAsync("blue");
        Assert.True(reply.IsCompleted);
        Assert.Equal("got: blue", reply.StatusText);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_yields_chunks_and_records_the_reply_text(string binding)
    {
        var chat = _host.Client(binding).StartConversation();
        var chunks = new List<string>();
        await foreach (var chunk in chat.StreamTextAsync("task")) chunks.Add(chunk);
        Assert.Equal(["part1", "part2"], chunks);
        Assert.True(chat.LastReply!.IsCompleted);
        Assert.Equal("part1part2", chat.LastReply.Text);
        Assert.Null(chat.TaskId);
    }

    [Fact]
    public async Task A_second_turn_while_one_is_running_is_refused()
    {
        var chat = _host.Client(ProtocolBindings.JsonRpc).StartConversation();
        using var cts = new CancellationTokenSource();
        var running = chat.SendAsync("slow", cts.Token); // works until cancelled
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync("hello"));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal("hi there", (await chat.SendAsync("hello")).Text); // the conversation is free again
    }

    [Fact]
    public async Task An_existing_context_is_continued_from_the_first_turn()
    {
        var client = _host.Client(ProtocolBindings.JsonRpc);
        var contextId = (await client.StartConversation().SendAsync("task")).ContextId!; // the server issues context ids
        var chat = client.StartConversation(contextId);
        var reply = await chat.SendAsync("task");
        Assert.Equal(contextId, reply.ContextId);
        Assert.Equal(contextId, chat.ContextId);
    }

    [Fact]
    public void Response_unions_expose_state_and_ids()
    {
        var task = new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working } };
        SendMessageResponse taskResponse = new TaskResponse(task);
        Assert.Equal(TaskState.Working, taskResponse.State);
        Assert.Equal("t", taskResponse.TaskId);
        Assert.Equal("c", taskResponse.ContextId);

        SendMessageResponse messageResponse = new MessageResponse(Message.FromAgent("x", "c2", "t2"));
        Assert.Null(messageResponse.State);
        Assert.Equal("t2", messageResponse.TaskId);
        Assert.Equal("c2", messageResponse.ContextId);

        Assert.Equal("c", StreamResponse.Of(task).ContextId);
        Assert.Equal("c2", StreamResponse.Of(Message.FromAgent("x", "c2")).ContextId);
        Assert.Equal("c3", StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", ContextId = "c3", Status = task.Status }).ContextId);
        Assert.Equal("c4", StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c4", Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("x")] }
        }).ContextId);
    }
}
