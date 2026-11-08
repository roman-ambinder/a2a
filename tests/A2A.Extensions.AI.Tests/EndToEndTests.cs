using System.Text.Json;
using A2A.Client;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI.Tests;

/// <summary>The whole chain over the ASP.NET test host: facade or tool, real client transport, real server, chat client executor, fake model.</summary>
public class EndToEndTests
{
    private static string AllText(AgentTask task) => string.Concat(task.Artifacts!.SelectMany(a => a.Parts).OfType<TextPart>().Select(p => p.Text));

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Hosted_chat_client_answers_over_unary_and_streaming(string binding)
    {
        var chat = FakeChatClient.Replying("Hello ", "from ", "the model");
        await using var host = await AgentHost.StartAsync(chat, o => o.StreamChunkSize = 8);
        var client = host.Client(binding);

        var response = await client.SendMessageAsync("hi");
        var task = Assert.IsType<TaskResponse>(response).Task;
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal("Hello from the model", AllText(task));

        var streamed = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("again"))) streamed.Add(e);
        var chunks = streamed.OfType<ArtifactUpdateEvent>().Select(e => e.ArtifactUpdate).ToList();
        Assert.True(chunks.Count >= 2);
        Assert.True(chunks[^1].LastChunk);
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.False(c.LastChunk));
        Assert.Equal("Hello from the model", string.Concat(chunks.SelectMany(c => c.Artifact.Parts).OfType<TextPart>().Select(p => p.Text)));
        Assert.Equal(TaskState.Completed, streamed.OfType<StatusUpdateEvent>().Last().StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Facade_talks_to_a_hosted_chat_client(string binding)
    {
        var chat = FakeChatClient.Replying("Hello ", "world");
        await using var host = await AgentHost.StartAsync(chat);
        var facade = host.Client(binding).AsChatClient();

        var response = await facade.GetResponseAsync([new ChatMessage(ChatRole.System, "ignored"), new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("Hello world", response.Text);
        Assert.False(string.IsNullOrEmpty(response.ConversationId));
        Assert.Equal("completed", response.AdditionalProperties![A2AChatKeys.TaskState]);
        Assert.DoesNotContain(chat.Calls[0].Messages, m => m.Text == "ignored");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in facade.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "again")])) updates.Add(u);
        Assert.Equal("Hello world", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal("completed", updates.Last().AdditionalProperties![A2AChatKeys.TaskState]);
        Assert.False(string.IsNullOrEmpty(updates.Last().ConversationId));
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Multi_turn_through_the_facade_with_KeepConversationOpen(string binding)
    {
        var n = 0;
        var chat = FakeChatClient.Streaming(_ => [new ChatResponseUpdate(ChatRole.Assistant, $"answer{++n}")]);
        await using var host = await AgentHost.StartAsync(chat, o => o.KeepConversationOpen = true);
        var facade = host.Client(binding).AsChatClient();

        var first = await facade.GetResponseAsync([new ChatMessage(ChatRole.User, "q1")]);
        Assert.Equal("answer1", first.Text);
        Assert.Equal("input-required", first.AdditionalProperties![A2AChatKeys.TaskState]);

        var second = await facade.GetResponseAsync([new ChatMessage(ChatRole.User, "q2")], new ChatOptions
        {
            ConversationId = first.ConversationId,
            AdditionalProperties = new AdditionalPropertiesDictionary { [A2AChatKeys.TaskId] = first.AdditionalProperties[A2AChatKeys.TaskId] },
        });

        Assert.Equal("answer2", second.Text);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(first.AdditionalProperties[A2AChatKeys.TaskId], second.AdditionalProperties![A2AChatKeys.TaskId]);
        Assert.Equal(
            [(ChatRole.User, "q1"), (ChatRole.Assistant, "answer1"), (ChatRole.User, "q2")],
            chat.Calls[1].Messages.Select(m => (m.Role, m.Text)).ToArray());
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Streaming_turn_two_with_KeepConversationOpen_delivers_only_turn_two(string binding)
    {
        var n = 0;
        var chat = FakeChatClient.Streaming(_ => [new ChatResponseUpdate(ChatRole.Assistant, $"answer{++n}")]);
        await using var host = await AgentHost.StartAsync(chat, o => o.KeepConversationOpen = true);
        var facade = host.Client(binding).AsChatClient();

        var first = new List<ChatResponseUpdate>();
        await foreach (var u in facade.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q1")])) first.Add(u);
        Assert.Equal("answer1", string.Concat(first.Select(u => u.Text)));
        var taskId = (string)first.Last(u => u.AdditionalProperties?.ContainsKey(A2AChatKeys.TaskId) == true).AdditionalProperties![A2AChatKeys.TaskId]!;
        var contextId = first.Last(u => u.ConversationId is not null).ConversationId;

        var second = new List<ChatResponseUpdate>();
        await foreach (var u in facade.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q2")],
            new ChatOptions { ConversationId = contextId, AdditionalProperties = new() { [A2AChatKeys.TaskId] = taskId } })) second.Add(u);

        Assert.Equal("answer2", string.Concat(second.Select(u => u.Text)));
        Assert.Equal(taskId, second.Last().AdditionalProperties![A2AChatKeys.TaskId]);
        Assert.Equal("input-required", second.Last().AdditionalProperties![A2AChatKeys.TaskState]);
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Tool_calls_a_hosted_chat_client(string binding)
    {
        var chat = FakeChatClient.Replying("tool ", "answer");
        await using var host = await AgentHost.StartAsync(chat);
        var fn = host.Client(binding).AsAIFunction("ask_agent", "Asks the agent.");

        var result = Assert.IsType<JsonElement>(await fn.InvokeAsync(new AIFunctionArguments { ["message"] = "question" }));

        Assert.Equal("completed", result.GetProperty("state").GetString());
        Assert.Equal("tool answer", result.GetProperty("remoteContent").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("taskId").GetString()));
        Assert.False(string.IsNullOrEmpty(result.GetProperty("contextId").GetString()));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(A2AAgentFunctionOptions.DefaultUntrustedContentNotice, result.GetProperty("notice").GetString());
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Tool_reports_a_failed_remote_task_as_a_result_without_remote_detail(string binding)
    {
        var chat = new FakeChatClient((_, _) => throw new InvalidOperationException("SECRET-DETAIL"));
        await using var host = await AgentHost.StartAsync(chat);
        var fn = host.Client(binding).AsAIFunction("ask_agent", "Asks the agent.");

        var result = Assert.IsType<JsonElement>(await fn.InvokeAsync(new AIFunctionArguments { ["message"] = "q" }));
        Assert.Equal("failed", result.GetProperty("state").GetString());
        Assert.DoesNotContain("SECRET", result.GetRawText());
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Facade_throws_for_a_failed_remote_task_without_remote_detail(string binding)
    {
        var chat = new FakeChatClient((_, _) => throw new InvalidOperationException("SECRET-DETAIL"));
        await using var host = await AgentHost.StartAsync(chat);
        var facade = host.Client(binding).AsChatClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await facade.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]));
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.Contains("failed", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Hooks_run_against_a_real_server_and_can_abort(string binding)
    {
        var chat = FakeChatClient.Replying("hello");
        await using var host = await AgentHost.StartAsync(chat);

        var artifacts = new List<string>();
        var fn = host.Client(binding).AsAIFunction("ask", "Asks.", new A2AAgentFunctionOptions { OnArtifactReceived = (a, _) => { artifacts.Add(((TextPart)a.Parts[0]).Text); return ValueTask.CompletedTask; } });
        await fn.InvokeAsync(new AIFunctionArguments { ["message"] = "q" });
        Assert.Equal(["hello"], artifacts);

        var blocked = host.Client(binding).AsAIFunction("ask", "Asks.", new A2AAgentFunctionOptions { OnArtifactReceived = (_, _) => throw new InvalidOperationException("artifact blocked") });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await blocked.InvokeAsync(new AIFunctionArguments { ["message"] = "q" }));
        Assert.Equal("artifact blocked", ex.Message);

        // OnMessageReceived only sees messages: an open conversation answers with a status message.
        await using var open = await AgentHost.StartAsync(FakeChatClient.Replying("hello"), o => o.KeepConversationOpen = true);
        var withMessageHook = open.Client(binding, new A2AClientOptions { OnMessageReceived = (_, _) => throw new InvalidOperationException("message blocked") });
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await withMessageHook.AsChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "q")]));
        Assert.Equal("message blocked", ex2.Message);
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await withMessageHook.AsAIFunction("ask", "Asks.").InvokeAsync(new AIFunctionArguments { ["message"] = "q" }));
        Assert.Equal("message blocked", ex3.Message);
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Url_part_sent_through_a_client_is_rejected_by_default(string binding)
    {
        var chat = FakeChatClient.Replying("never");
        await using var host = await AgentHost.StartAsync(chat);
        var client = host.Client(binding);
        var message = new Message { MessageId = Message.NewId(), Role = Role.User, Parts = [Part.FromUrl(new Uri("https://example.com/x"))] };

        var task = Assert.IsType<TaskResponse>(await client.SendMessageAsync(new SendMessageRequest { Message = message })).Task;
        Assert.Equal(TaskState.Rejected, task.Status.State);
        Assert.Empty(chat.Calls);

        var fn = client.AsAIFunction("ask", "Asks.");
        var result = Assert.IsType<JsonElement>(await fn.InvokeAsync(new AIFunctionArguments { ["message"] = "text only" }));
        Assert.Equal("completed", result.GetProperty("state").GetString());
    }
}
