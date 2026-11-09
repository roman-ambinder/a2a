using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI.Tests;

public class ChatClientFacadeTests
{
    private static Message AgentMsg(string text, string? id = null) =>
        new() { MessageId = id ?? Message.NewId(), Role = Role.Agent, Parts = [Part.FromText(text)] };

    private static Artifact Art(string id, string text) => new() { ArtifactId = id, Parts = [Part.FromText(text)] };

    private static StreamResponse Chunk(string artifactId, string text, bool append = false) =>
        new ArtifactUpdateEvent(new TaskArtifactUpdateEvent { TaskId = "t1", ContextId = "c1", Artifact = Art(artifactId, text), Append = append });

    private static StreamResponse Status(TaskState state, Message? message = null) =>
        new StatusUpdateEvent(new TaskStatusUpdateEvent { TaskId = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = state, Message = message } });

    private static async IAsyncEnumerable<StreamResponse> Events(params StreamResponse[] events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(IChatClient chat, params ChatMessage[] messages)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var u in chat.GetStreamingResponseAsync(messages)) list.Add(u);
        return list;
    }

    private static FakeTransport Unary(AgentTask task) => new() { Send = (_, _) => Task.FromResult(FakeTransport.Respond(task)) };

    // ---- what is sent ----

    [Fact]
    public async Task Only_the_trailing_user_messages_are_sent_merged_into_one_message()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed));
        var chat = transport.AsClient().AsChatClient();

        await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "first question"),
            new ChatMessage(ChatRole.Assistant, "first answer"),
            new ChatMessage(ChatRole.User, "second"),
            new ChatMessage(ChatRole.User, "third"),
        ]);

        var sent = Assert.Single(transport.Sent).Message!;
        Assert.Equal(Role.User, sent.Role);
        Assert.Equal(["second", "third"], sent.Parts.Select(p => ((TextPart)p).Text).ToArray());
    }

    [Fact]
    public async Task System_and_tool_messages_are_never_sent()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed));
        var chat = transport.AsClient().AsChatClient();

        await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "SECRET-SYSTEM-PROMPT"),
            new ChatMessage(ChatRole.User, "hi"),
        ], new ChatOptions { Instructions = "SECRET-INSTRUCTIONS" });

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.Single(transport.Sent), A2A.Json.A2AJson.Options);
        Assert.DoesNotContain("SECRET", json);
        Assert.Equal("hi", ((TextPart)Assert.Single(transport.Sent[0].Message!.Parts)).Text);

        // A system message in the middle of the trailing user run ends that run: only the user text after it is sent.
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "before"), new ChatMessage(ChatRole.System, "SECRET-2"), new ChatMessage(ChatRole.User, "after")]);
        Assert.Equal(["after"], transport.Sent[1].Message!.Parts.Select(p => ((TextPart)p).Text).ToArray());
    }

    [Fact]
    public async Task Messages_not_ending_in_a_user_message_throw_ArgumentException()
    {
        var chat = Unary(FakeTransport.TaskIn(TaskState.Completed)).AsClient().AsChatClient();
        await Assert.ThrowsAsync<ArgumentException>(async () => await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, "a")]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await chat.GetResponseAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await chat.GetResponseAsync([new ChatMessage(ChatRole.System, "s")]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await CollectAsync(chat, new ChatMessage(ChatRole.Assistant, "a")));
    }

    [Fact]
    public async Task ChatOptions_Tools_are_ignored()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed));
        var chat = transport.AsClient().AsChatClient();
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Tools = [AIFunctionFactoryLess.Tool("t")] });
        Assert.Single(transport.Sent);
    }

    // ---- ids round trip ----

    [Fact]
    public async Task ConversationId_and_task_id_go_out_and_come_back()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.InputRequired, "task-9", "ctx-9", AgentMsg("which one?"), [Art("a", "partial")]));
        var chat = transport.AsClient().AsChatClient();
        var options = new ChatOptions
        {
            ConversationId = "ctx-in",
            AdditionalProperties = new AdditionalPropertiesDictionary { [A2AChatKeys.TaskId] = "task-in" },
        };

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        var sent = Assert.Single(transport.Sent).Message!;
        Assert.Equal("ctx-in", sent.ContextId);
        Assert.Equal("task-in", sent.TaskId);
        Assert.Equal("ctx-9", response.ConversationId);
        Assert.Equal("task-9", response.AdditionalProperties![A2AChatKeys.TaskId]);
        Assert.Equal("input-required", response.AdditionalProperties[A2AChatKeys.TaskState]);
        Assert.Contains("partial", response.Text);
        Assert.Contains("which one?", response.Text);
    }

    [Fact]
    public async Task Direct_reply_response_carries_context_and_task_id()
    {
        var reply = AgentMsg("direct");
        reply.ContextId = "ctx-d";
        var transport = new FakeTransport { Send = (_, _) => Task.FromResult<SendMessageResponse>(new MessageResponse(reply)) };
        var response = await transport.AsClient().AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("direct", response.Text);
        Assert.Equal("ctx-d", response.ConversationId);
        Assert.Equal(ChatRole.Assistant, response.Messages.Single().Role);
    }

    [Fact]
    public async Task Completed_response_has_artifacts_as_assistant_messages_and_completed_state()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed, "t", "c", AgentMsg("STATUS-NOT-SHOWN"), [Art("a1", "one"), Art("a2", "two")]));
        var response = await transport.AsClient().AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal(["one", "two"], response.Messages.Select(m => m.Text).ToArray());
        Assert.All(response.Messages, m => Assert.Equal(ChatRole.Assistant, m.Role));
        Assert.Equal("completed", response.AdditionalProperties![A2AChatKeys.TaskState]);
        Assert.DoesNotContain("STATUS-NOT-SHOWN", response.Text);
    }

    [Fact]
    public async Task Working_task_is_waited_for()
    {
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Working, "w"))),
            Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Completed, "w", "c", artifacts: [Art("a", "done")])),
        };
        var response = await transport.AsClient().AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("done", response.Text);
    }

    // ---- failures ----

    [Theory]
    [InlineData(TaskState.Failed, "failed")]
    [InlineData(TaskState.Rejected, "rejected")]
    [InlineData(TaskState.Canceled, "canceled")]
    public async Task Failed_rejected_or_canceled_throw_with_the_state_only(TaskState state, string name)
    {
        var transport = Unary(FakeTransport.TaskIn(state, message: AgentMsg("SECRET-REMOTE-TEXT"), artifacts: [Art("a", "SECRET-ARTIFACT")]));
        var chat = transport.AsClient().AsChatClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains(name, ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Rejected)]
    [InlineData(TaskState.Canceled)]
    public async Task Streaming_failure_throws_with_the_state_only(TaskState state)
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(Status(TaskState.Working), Status(state, AgentMsg("SECRET-REMOTE-TEXT"))) };
        var chat = transport.AsClient().AsChatClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(chat, new ChatMessage(ChatRole.User, "hi")));
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Fact]
    public async Task Failed_task_snapshot_in_a_stream_throws()
    {
        var snapshot = new TaskEvent(FakeTransport.TaskIn(TaskState.Failed, message: AgentMsg("SECRET")));
        var transport = new FakeTransport { Stream = (_, _) => Events(snapshot) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi")));
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    // ---- streaming content rules ----

    [Fact]
    public async Task Artifact_chunks_become_text_updates_and_progress_is_not_surfaced()
    {
        var transport = new FakeTransport
        {
            Stream = (_, _) => Events(
                new TaskEvent(FakeTransport.TaskIn(TaskState.Submitted)),
                Status(TaskState.Working, AgentMsg("PROGRESS-MESSAGE")),
                Chunk("a", "Hel"),
                Chunk("a", "lo", append: true),
                Status(TaskState.Completed)),
        };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));

        var text = string.Concat(updates.Select(u => u.Text));
        Assert.Equal("Hello", text);
        Assert.DoesNotContain("PROGRESS", text);
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));
        Assert.Equal("c1", updates.Last().ConversationId);
        Assert.Equal("t1", updates.Last().AdditionalProperties![A2AChatKeys.TaskId]);
        Assert.Equal(ChatFinishReason.Stop, updates.Last().FinishReason);
        Assert.Equal("completed", updates.Last().AdditionalProperties![A2AChatKeys.TaskState]);
    }

    [Fact]
    public async Task Snapshot_artifacts_are_emitted_only_when_that_artifact_had_no_chunks()
    {
        var snapshot = FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("chunked", "CHUNKED-FULL"), Art("whole", "whole text")]);
        var transport = new FakeTransport { Stream = (_, _) => Events(Chunk("chunked", "chunk"), new TaskEvent(snapshot)) };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));
        Assert.Equal("chunkwhole text", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task Snapshot_artifacts_are_not_emitted_twice()
    {
        var snapshot = FakeTransport.TaskIn(TaskState.Working, artifacts: [Art("whole", "once")]);
        var transport = new FakeTransport { Stream = (_, _) => Events(new TaskEvent(snapshot), new TaskEvent(snapshot), Status(TaskState.Completed)) };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));
        Assert.Equal("once", string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task Direct_reply_in_a_stream_becomes_a_text_update()
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(new MessageEvent(AgentMsg("direct"))) };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));
        Assert.Equal("direct", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task Interrupted_status_message_becomes_a_text_update_with_the_state()
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(Status(TaskState.Working), Status(TaskState.InputRequired, AgentMsg("what size?"))) };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));
        var update = Assert.Single(updates);
        Assert.Equal("what size?", update.Text);
        Assert.Equal("input-required", update.AdditionalProperties![A2AChatKeys.TaskState]);
        Assert.Equal("t1", update.AdditionalProperties[A2AChatKeys.TaskId]);
    }

    [Fact]
    public async Task Streaming_sends_ids_like_the_unary_call()
    {
        var transport = new FakeTransport { Stream = (_, _) => Events(Status(TaskState.Completed)) };
        var chat = transport.AsClient().AsChatClient();
        await foreach (var _ in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ConversationId = "cx", AdditionalProperties = new() { [A2AChatKeys.TaskId] = "tx" } })) { }
        var sent = Assert.Single(transport.Sent).Message!;
        Assert.Equal("cx", sent.ContextId);
        Assert.Equal("tx", sent.TaskId);
    }

    // ---- hooks ----

    [Fact]
    public async Task OnArtifactReceived_sees_artifacts_and_chunks_and_can_abort()
    {
        var seen = new List<string>();
        var transport = new FakeTransport
        {
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("u1", "x")]))),
            Stream = (_, _) => Events(Chunk("s1", "a"), Chunk("s1", "b", append: true), Status(TaskState.Completed)),
        };
        var options = new A2AChatClientOptions { OnArtifactReceived = (a, _) => { seen.Add(a.ArtifactId); return ValueTask.CompletedTask; } };
        var chat = transport.AsClient().AsChatClient(options);
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await CollectAsync(chat, new ChatMessage(ChatRole.User, "hi"));
        Assert.Equal(["u1", "s1", "s1"], seen);

        var aborting = transport.AsClient().AsChatClient(new A2AChatClientOptions { OnArtifactReceived = (_, _) => throw new InvalidOperationException("no") });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await aborting.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await CollectAsync(aborting, new ChatMessage(ChatRole.User, "hi")));
    }

    // ---- KeepConversationOpen echo ----

    [Fact]
    public async Task Interrupted_status_that_only_repeats_the_artifacts_adds_no_text()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.InputRequired, "t", "c", AgentMsg("same reply"), [Art("a", "same reply")]));
        var response = await transport.AsClient().AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("same reply", response.Text);
        Assert.Equal("input-required", response.AdditionalProperties![A2AChatKeys.TaskState]);
    }

    [Fact]
    public async Task Interrupted_status_that_only_repeats_streamed_chunks_adds_no_text_but_carries_the_state()
    {
        var transport = new FakeTransport
        {
            Stream = (_, _) => Events(Chunk("a", "same "), Chunk("a", "reply", append: true), Status(TaskState.InputRequired, AgentMsg("same reply"))),
        };
        var updates = await CollectAsync(transport.AsClient().AsChatClient(), new ChatMessage(ChatRole.User, "hi"));
        Assert.Equal("same reply", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal("input-required", updates.Last().AdditionalProperties![A2AChatKeys.TaskState]);
    }

    [Fact]
    public async Task Continuing_a_task_returns_only_artifacts_the_caller_has_not_seen()
    {
        var old = Art("old", "turn one");
        var transport = new FakeTransport
        {
            Get = (r, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.InputRequired, r.Id, "c", artifacts: [old])),
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, "t", "c", AgentMsg("turn two"), [old, Art("new", "turn two")]))),
        };
        var seen = new List<string>();
        var chat = transport.AsClient().AsChatClient(new A2AChatClientOptions { OnArtifactReceived = (a, _) => { seen.Add(a.ArtifactId); return ValueTask.CompletedTask; } });

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "more")],
            new ChatOptions { ConversationId = "c", AdditionalProperties = new() { [A2AChatKeys.TaskId] = "t" } });

        Assert.Equal("turn two", response.Text);
        Assert.Equal(["new"], seen);
    }

    [Fact]
    public async Task A_new_task_does_not_look_up_a_task()
    {
        var transport = new FakeTransport
        {
            Get = (_, _) => throw new InvalidOperationException("must not be called"),
            Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("a", "x")]))),
        };
        Assert.Equal("x", (await transport.AsClient().AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text);
    }

    [Fact]
    public async Task Streaming_a_continuation_skips_replayed_artifacts_and_the_old_question_but_keeps_ids_and_state()
    {
        var old = Art("old", "turn one");
        var snapshot = FakeTransport.TaskIn(TaskState.InputRequired, "t1", "c1", AgentMsg("turn one", "m-old"), [old]);
        var transport = new FakeTransport
        {
            Get = (r, _) => Task.FromResult(snapshot),
            Stream = (_, _) => Events(new TaskEvent(snapshot), Chunk("new", "turn two"), Status(TaskState.InputRequired, AgentMsg("turn two"))),
        };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in transport.AsClient().AsChatClient().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "more")],
            new ChatOptions { ConversationId = "c1", AdditionalProperties = new() { [A2AChatKeys.TaskId] = "t1" } })) updates.Add(u);

        Assert.Equal("turn two", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("t1", u.AdditionalProperties![A2AChatKeys.TaskId]));
        Assert.Equal("input-required", updates.Last().AdditionalProperties![A2AChatKeys.TaskState]);
    }

    [Fact]
    public async Task Streaming_ends_at_an_interrupted_state_even_if_the_server_keeps_the_stream_open()
    {
        // This SDK's server does not close the stream at input-required: it waits for a follow-up.
        var transport = new FakeTransport { Stream = (_, ct) => OpenAfterInterrupt(ct) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in transport.AsClient().AsChatClient().GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token)) updates.Add(u);
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal("what size?", string.Concat(updates.Select(u => u.Text)));

        static async IAsyncEnumerable<StreamResponse> OpenAfterInterrupt([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Status(TaskState.InputRequired, AgentMsg("what size?"));
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task Message_and_content_properties_are_not_sent_to_the_remote_agent()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed));
        var chat = transport.AsClient().AsChatClient();
        var content = new TextContent("hi") { AdditionalProperties = new() { ["contentSecret"] = "C-SECRET" } };
        var message = new ChatMessage(ChatRole.User, [content]) { AdditionalProperties = new() { ["messageSecret"] = "M-SECRET" } };

        await chat.GetResponseAsync([message]);
        await CollectIgnoringAsync(chat, message);

        foreach (var request in transport.Sent)
        {
            Assert.Null(request.Message!.Metadata);
            Assert.All(request.Message.Parts, p => Assert.Null(p.Metadata));
            Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(request, A2A.Json.A2AJson.Options));
        }
        Assert.Equal(2, transport.Sent.Count);
        // The public conversion still copies metadata.
        Assert.Equal("M-SECRET", message.ToA2AMessage().Metadata!["messageSecret"].GetString());
    }

    private static async Task CollectIgnoringAsync(IChatClient chat, ChatMessage message)
    {
        var transport = ((A2AChatClient)chat);
        try { await foreach (var _ in transport.GetStreamingResponseAsync([message])) { } }
        catch (NotSupportedException) { }
    }

    // ---- service and dispose ----

    [Fact]
    public void GetService_returns_metadata_and_self_and_nothing_else()
    {
        var card = new AgentCard { Name = "Remote Bot", Description = "d", Version = "1" };
        var chat = new FakeTransport().AsClient(card: card).AsChatClient();
        var metadata = Assert.IsType<ChatClientMetadata>(chat.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("a2a", metadata.ProviderName);
        Assert.Equal("Remote Bot", metadata.DefaultModelId);
        Assert.Null(metadata.ProviderUri);
        Assert.Same(chat, chat.GetService(typeof(A2AChatClient)));
        Assert.Same(chat, chat.GetService(typeof(IChatClient)));
        Assert.Null(chat.GetService(typeof(string)));
        Assert.Null(chat.GetService(typeof(ChatClientMetadata), "key"));
    }

    [Fact]
    public async Task Dispose_does_not_dispose_the_A2AClient()
    {
        var transport = Unary(FakeTransport.TaskIn(TaskState.Completed, artifacts: [Art("a", "still works")]));
        var client = transport.AsClient();
        var chat = client.AsChatClient();
        chat.Dispose();
        var again = await client.AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("still works", again.Text);
    }

    [Fact]
    public async Task Cancellation_token_reaches_the_stream()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport { Stream = (_, ct) => Infinite(ct) };
        var chat = transport.AsClient().AsChatClient();
        var task = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token)) { }
        });
        cts.CancelAfter(50);
        await task;

        // Chunks keep coming (the stream does not end at a working status), so only the token can stop it.
        static async IAsyncEnumerable<StreamResponse> Infinite([EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Chunk("a", "x", append: true);
                await Task.Yield();
            }
        }
    }
}
