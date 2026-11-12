using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary>Background responses of <see cref="A2ARemoteAIAgent"/>: continuation tokens to poll and to resume.</summary>
public class BackgroundTests
{
    private static readonly AgentRunOptions Background = new() { AllowBackgroundResponses = true };

    private static Message AgentMsg(string text) => new() { MessageId = Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] };

    private static Artifact Art(string id, params string[] texts) => new() { ArtifactId = id, Parts = [.. texts.Select(t => (Part)Part.FromText(t))] };

    private static AgentTask Working(string id = "t1") => FakeTransport.TaskIn(TaskState.Working, id);

    private static AgentTask Done(string text, string id = "t1") => FakeTransport.TaskIn(TaskState.Completed, id, artifacts: [Art("a-" + id, text)]);

    private static AgentRunOptions Resume(ResponseContinuationToken token) => new() { ContinuationToken = token };

    private static async IAsyncEnumerable<StreamResponse> Events(params StreamResponse[] events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    private static StreamResponse Chunk(string text, bool append, bool last = false, string artifactId = "a1") =>
        new ArtifactUpdateEvent(new TaskArtifactUpdateEvent { TaskId = "t1", ContextId = "c1", Artifact = Art(artifactId, text), Append = append, LastChunk = last });

    private static StreamResponse Status(TaskState state, Message? message = null) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent { TaskId = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = state, Message = message } });

    // ---- 1. poll ----

    [Fact]
    public async Task A_running_task_gives_a_token_and_polling_with_it_gives_the_result_once_complete()
    {
        var reads = new Queue<AgentTask>([Working(), FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("a-t1", "the result")])]);
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(Working())),
            Get = (_, _) => Task.FromResult(reads.Dequeue()),
        };
        var agent = transport.AsClient().AsAIAgent();
        var session = Assert.IsType<A2ARemoteAgentSession>(await agent.CreateSessionAsync());

        var started = await agent.RunAsync("long job", session, Background);

        Assert.True(transport.Sent[0].Configuration!.ReturnImmediately);
        Assert.Empty(started.Messages);
        Assert.Equal("working", started.AdditionalProperties![A2AAgentKeys.TaskState]);
        Assert.NotNull(started.ContinuationToken);
        Assert.Equal("c1", session.ContextId);
        Assert.Null(session.TaskId); // running, not waiting for input

        var still = await agent.RunAsync([], session, Resume(started.ContinuationToken!));
        Assert.Empty(still.Messages);
        Assert.NotNull(still.ContinuationToken);

        var done = await agent.RunAsync([], session, Resume(still.ContinuationToken!));
        Assert.Equal("the result", done.Text);
        Assert.Null(done.ContinuationToken);
        Assert.Single(transport.Sent); // polling never sends
    }

    [Fact]
    public async Task A_task_that_is_already_done_answers_at_once_without_a_token()
    {
        var transport = new FakeTransport { Send = (_, _) => Task.FromResult(FakeTransport.Respond(Done("quick"))) };

        var response = await transport.AsClient().AsAIAgent().RunAsync("go", options: Background);

        Assert.Equal("quick", response.Text);
        Assert.Null(response.ContinuationToken);
    }

    [Fact]
    public async Task An_interrupted_task_answers_with_its_question_and_no_token()
    {
        var transport = new FakeTransport { Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, message: AgentMsg("Which city?")))) };
        var agent = transport.AsClient().AsAIAgent();
        var session = Assert.IsType<A2ARemoteAgentSession>(await agent.CreateSessionAsync());

        var response = await agent.RunAsync("weather", session, Background);

        Assert.Equal("Which city?", response.Text);
        Assert.Null(response.ContinuationToken);
        Assert.Equal("t1", session.TaskId);
    }

    [Fact]
    public async Task Polling_a_task_that_failed_throws_without_the_remote_text()
    {
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(Working())),
            Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Failed, message: AgentMsg("SECRET"))),
        };
        var agent = transport.AsClient().AsAIAgent();
        var started = await agent.RunAsync("go", options: Background);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync([], options: Resume(started.ContinuationToken!)));
        Assert.DoesNotContain("SECRET", error.Message);
    }

    [Fact]
    public async Task Continuing_a_task_in_the_background_shows_only_this_turns_content()
    {
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, message: AgentMsg("Which city?")))),
        };
        var agent = transport.AsClient().AsAIAgent();
        var session = Assert.IsType<A2ARemoteAgentSession>(await agent.CreateSessionAsync());
        await agent.RunAsync("weather", session);
        // The task already has an artifact from an earlier turn; this turn adds one.
        transport.Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.InputRequired, artifacts: [Art("old", "old text")], message: AgentMsg("Which city?")));
        transport.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("old", "old text"), Art("new", "Sunny")])));

        var response = await agent.RunAsync("Paris", session, Background);

        Assert.Equal("t1", transport.Sent[1].Message!.TaskId);
        Assert.Equal("Sunny", response.Text);
    }

    // ---- 2. tokens ----

    [Fact]
    public async Task The_token_names_the_task_and_round_trips()
    {
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(Working("task-42"))),
            Get = (r, _) => Task.FromResult(Done("ok", r.Id)),
        };
        var agent = transport.AsClient().AsAIAgent();

        var started = await agent.RunAsync("go", options: Background);
        using var json = JsonDocument.Parse(started.ContinuationToken!.ToBytes());
        Assert.Equal("task-42", json.RootElement.GetProperty("taskId").GetString());

        var done = await agent.RunAsync([], options: Resume(ResponseContinuationToken.FromBytes(started.ContinuationToken.ToBytes())));
        Assert.Equal("ok", done.Text);
        Assert.Contains("get:task-42", transport.Reads);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"taskId":"t1","extra":1}""")]
    [InlineData("""{"taskId":5}""")]
    [InlineData("""{"artifacts":{"a":1}}""")]
    [InlineData("""{"taskId":"t1","artifacts":{"a":-1}}""")]
    [InlineData("""["t1"]""")]
    public async Task A_foreign_token_is_refused(string bytes)
    {
        var agent = new FakeTransport().AsClient().AsAIAgent();
        var token = ResponseContinuationToken.FromBytes(Encoding.UTF8.GetBytes(bytes));

        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync([], options: Resume(token)));
    }

    [Fact]
    public async Task Messages_with_a_token_are_refused()
    {
        var agent = new FakeTransport().AsClient().AsAIAgent();
        var token = ResponseContinuationToken.FromBytes("""{"taskId":"t1"}"""u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync("more", options: Resume(token)));
    }

    // ---- 3. streaming ----

    [Fact]
    public async Task Every_streamed_update_but_the_last_carries_a_token()
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(Chunk("Hello ", false), Chunk("world", true, last: true), Status(TaskState.Completed)) };
        var agent = transport.AsClient().AsAIAgent();

        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync("hi", options: Background)) updates.Add(u);

        Assert.Equal("Hello world", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates.Take(updates.Count - 1), u => Assert.NotNull(u.ContinuationToken));
        Assert.Null(updates[^1].ContinuationToken);
    }

    [Fact]
    public async Task Streaming_in_the_background_shows_the_same_text_as_without()
    {
        StreamResponse[] script =
        [
            Chunk("Hel", false), Chunk("lo", true, last: true),
            Status(TaskState.Completed, AgentMsg("Hello")), // repeats the artifact: adds nothing
        ];
        var plain = new FakeTransport { Stream = (_, _) => Events(script) }.AsClient().AsAIAgent();
        var background = new FakeTransport { Stream = (_, _) => Events(script) }.AsClient().AsAIAgent();

        var a = new StringBuilder();
        await foreach (var u in plain.RunStreamingAsync("hi")) a.Append(u.Text);
        var b = new StringBuilder();
        await foreach (var u in background.RunStreamingAsync("hi", options: Background)) b.Append(u.Text);

        Assert.Equal("Hello", a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }

    [Fact]
    public async Task Resuming_with_a_token_continues_after_the_last_part_the_caller_had()
    {
        // The caller had "Hello " (one part of a1). The resumed stream starts with a snapshot that holds two parts.
        var transport = new FakeTransport
        {
            Stream = (_, _) => Events(Chunk("Hello ", false)),
            Subscribe = (_, _) => Events(
                new TaskEvent(FakeTransport.TaskIn(TaskState.Working, artifacts: [Art("a1", "Hello ", "wor")])),
                Chunk("ld", true, last: true),
                Status(TaskState.Completed)),
        };
        var agent = transport.AsClient().AsAIAgent();
        ResponseContinuationToken? token = null;
        await foreach (var u in agent.RunStreamingAsync("hi", options: Background))
        {
            token = u.ContinuationToken;
            break; // the connection dropped
        }

        var rest = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync([], options: Resume(token!))) rest.Add(u);

        Assert.Equal("world", string.Concat(rest.Select(u => u.Text)));
        Assert.Null(rest[^1].ContinuationToken);
        Assert.Contains("subscribe:t1", transport.Reads);
    }

    [Fact]
    public async Task A_resumed_stream_does_not_repeat_the_reply_as_a_status_message()
    {
        // The stream dropped right after the last chunk. The completed status message repeats the artifact text (as the
        // SDK's own executors write it with context memory): the resumed stream must not show it again.
        var transport = new FakeTransport
        {
            Stream = (_, _) => Events(Chunk("the answer", false, last: true)),
            Subscribe = (_, _) => Events(new TaskEvent(FakeTransport.TaskIn(TaskState.Completed,
                message: AgentMsg("the answer"), artifacts: [Art("a1", "the answer")]))),
        };
        var agent = transport.AsClient().AsAIAgent();
        ResponseContinuationToken? token = null;
        await foreach (var u in agent.RunStreamingAsync("hi", options: Background))
        {
            token = u.ContinuationToken;
            break; // the connection dropped
        }

        var rest = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync([], options: Resume(token!))) rest.Add(u);

        Assert.Equal("", string.Concat(rest.Select(u => u.Text)));
    }

    [Fact]
    public async Task Polling_after_a_dropped_stream_does_not_repeat_the_reply_as_a_status_message()
    {
        var transport = new FakeTransport
        {
            Stream = (_, _) => Events(Chunk("the answer", false, last: true)),
            Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Completed,
                message: AgentMsg("the answer"), artifacts: [Art("a1", "the answer")])),
        };
        var agent = transport.AsClient().AsAIAgent();
        ResponseContinuationToken? token = null;
        await foreach (var u in agent.RunStreamingAsync("hi", options: Background))
        {
            token = u.ContinuationToken;
            break;
        }

        var done = await agent.RunAsync([], options: Resume(token!));

        Assert.Equal("", done.Text);
    }

    [Fact]
    public async Task A_resumed_stream_ends_at_the_terminal_state_over_the_real_server()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosted = new FakeAIAgent((_, ct) => Gated(gate, ct));
        await using var host = await AgentHost.StartAsync(hosted, o => o.StreamChunkSize = 1);
        var agent = host.Client().AsAIAgent();

        var first = new StringBuilder();
        ResponseContinuationToken? token = null;
        await foreach (var u in agent.RunStreamingAsync("go", options: Background))
        {
            first.Append(u.Text);
            token = u.ContinuationToken;
            if (first.Length > 0) break; // stop after the first text, before the agent is let go
        }

        gate.SetResult();
        var rest = new StringBuilder();
        AgentResponseUpdate? last = null;
        await foreach (var u in agent.RunStreamingAsync([], options: Resume(token!)))
        {
            rest.Append(u.Text);
            last = u;
        }

        Assert.Equal("a", first.ToString());
        Assert.Equal("abc", first.ToString() + rest);
        Assert.Equal("completed", last!.AdditionalProperties![A2AAgentKeys.TaskState]);
        Assert.Null(last.ContinuationToken);

        static async IAsyncEnumerable<AgentResponseUpdate> Gated(TaskCompletionSource gate, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "a");
            yield return new AgentResponseUpdate(ChatRole.Assistant, "b"); // emits "a"; "b" is held back
            await gate.Task.WaitAsync(ct);
            yield return new AgentResponseUpdate(ChatRole.Assistant, "c");
        }
    }

    [Fact]
    public async Task Polling_works_over_the_real_server()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosted = new FakeAIAgent((_, ct) => Slow(gate, ct));
        await using var host = await AgentHost.StartAsync(hosted);
        var agent = host.Client().AsAIAgent();

        var started = await agent.RunAsync("go", options: Background);
        Assert.NotNull(started.ContinuationToken);

        gate.SetResult();
        var response = started;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (response.ContinuationToken is { } token && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            response = await agent.RunAsync([], options: Resume(token));
        }

        Assert.Equal("finished", response.Text);
        Assert.Null(response.ContinuationToken);

        static async IAsyncEnumerable<AgentResponseUpdate> Slow(TaskCompletionSource gate, [EnumeratorCancellation] CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            yield return new AgentResponseUpdate(ChatRole.Assistant, "finished");
        }
    }

    // ---- 4. without background responses ----

    [Fact]
    public async Task Without_background_responses_a_slow_task_is_waited_for_and_no_token_appears()
    {
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(Working())),
            Get = (_, _) => Task.FromResult(Done("waited")),
        };

        var response = await transport.AsClient().AsAIAgent().RunAsync("go");

        Assert.Equal("waited", response.Text);
        Assert.Null(response.ContinuationToken);
        Assert.False(transport.Sent[0].Configuration?.ReturnImmediately ?? false);
    }

    [Fact]
    public async Task Without_background_responses_streamed_updates_carry_no_token()
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(Chunk("Hello", false, last: true), Status(TaskState.Completed)) };

        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in transport.AsClient().AsAIAgent().RunStreamingAsync("hi")) updates.Add(u);

        Assert.All(updates, u => Assert.Null(u.ContinuationToken));
    }
}
