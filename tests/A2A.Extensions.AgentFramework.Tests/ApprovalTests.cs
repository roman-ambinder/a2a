using System.Text.Json;
using A2A.Client;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary><see cref="ToolApprovalMode.Bound"/>: Agent Framework approval requests through A2A approval binding.</summary>
public class ApprovalTests
{
    private const string Generic = "The agent failed to process the request.";
    private const string Reason = "The caller did not approve this action.";

    private static AgentTask Task_(SendMessageResponse r) => Assert.IsType<TaskResponse>(r).Task;

    private static string Text(IEnumerable<Part> parts) => string.Concat(parts.OfType<TextPart>().Select(p => p.Text));

    private static string? HashOf(AgentTask task) =>
        task.Status.Message?.Metadata is { } m && m.TryGetValue(A2AProtocol.ApprovalHashKey, out var h) ? h.GetString() : null;

    private static Dictionary<string, object?> Args() => new() { ["path"] = "/x", ["n"] = 2 };

    /// <summary>
    /// An agent that asks to approve <c>delete_file</c> with <paramref name="args"/> (by run number, from 0), unless it was
    /// given an answer: then it says what it did.
    /// </summary>
    private static FakeAIAgent Asking(Func<int, IDictionary<string, object?>>? args = null, string callId = "call-1", int requests = 1)
    {
        var runs = 0;
        return FakeAIAgent.Streaming(call =>
        {
            var run = Interlocked.Increment(ref runs) - 1;
            var answer = call.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().FirstOrDefault();
            if (answer is not null && run > 0 && args is null)
                return [new AgentResponseUpdate(ChatRole.Assistant, answer.Approved ? "deleted" : "kept: " + answer.Reason)];
            var updates = new List<AgentResponseUpdate> { new(ChatRole.Assistant, "checking ") };
            for (var i = 0; i < requests; i++)
                updates.Add(new AgentResponseUpdate(ChatRole.Assistant,
                    [new ToolApprovalRequestContent($"req-{run}-{i}", new FunctionCallContent(i == 0 ? callId : $"{callId}-{i}", "delete_file", args?.Invoke(run) ?? Args()))]));
            return updates;
        });
    }

    private static Task<AgentHost> StartAsync(AIAgent agent, FakeSessionStore? store = null) =>
        AgentHost.StartAsync(a => a.AddAIAgent(o =>
        {
            o.SessionMode = AgentSessionMode.Stored;
            o.ToolApprovals = ToolApprovalMode.Bound;
        }), s =>
        {
            s.AddSingleton(agent);
            s.AddSingleton<AgentSessionStore>(store ?? new FakeSessionStore());
        });

    private static Message FollowUp(AgentTask task, string text, string? hash = null)
    {
        var message = Message.FromUser(text);
        message.TaskId = task.Id;
        message.ContextId = task.ContextId;
        if (hash is not null) message.Metadata = new() { [A2AProtocol.ApprovalHashKey] = JsonSerializer.SerializeToElement(hash) };
        return message;
    }

    // ---- 1. asking ----

    [Fact]
    public async Task An_approval_request_puts_the_task_in_input_required_with_the_bound_hash()
    {
        var agent = Asking();
        await using var host = await StartAsync(agent);

        var task = Task_(await host.Client().SendMessageAsync("clean up"));

        const string description = """delete_file({"n":2,"path":"/x"})""";
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal(description, Text(task.Status.Message!.Parts));
        Assert.Equal(new PendingApproval("call-1", description).Hash, HashOf(task));
        Assert.Equal("checking ", Text(Assert.Single(task.Artifacts!).Parts)); // what it said before asking stays
    }

    [Fact]
    public async Task Text_before_the_request_is_not_marked_as_the_last_chunk()
    {
        await using var host = await StartAsync(Asking());
        var events = new List<StreamResponse>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // INPUT_REQUIRED is not terminal: the client would resume the stream, so stop reading there.
        await foreach (var e in host.Client().SendStreamingMessageAsync(Message.FromUser("clean up"), cancellationToken: timeout.Token))
        {
            events.Add(e);
            if (e is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.InputRequired }) break;
        }

        var chunk = Assert.Single(events.OfType<ArtifactUpdateEvent>()).ArtifactUpdate;
        Assert.False(chunk.LastChunk);
        Assert.Equal(TaskState.InputRequired, events.OfType<StatusUpdateEvent>().Last().StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Only_the_first_request_of_a_run_is_asked()
    {
        await using var host = await StartAsync(Asking(requests: 3));

        var task = Task_(await host.Client().SendMessageAsync("clean up"));

        Assert.Equal(new PendingApproval("call-1", """delete_file({"n":2,"path":"/x"})""").Hash, HashOf(task));
    }

    // ---- 2. approved ----

    [Fact]
    public async Task Echoing_the_hash_approves_that_call_and_the_task_completes()
    {
        var agent = Asking();
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));

        var done = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) }));

        var input = Assert.Single(agent.Calls[1].Messages);
        Assert.Equal(ChatRole.User, input.Role);
        var answer = Assert.IsType<ToolApprovalResponseContent>(input.Contents[0]);
        Assert.True(answer.Approved);
        Assert.Equal("call-1", answer.ToolCall.CallId);
        Assert.Equal("yes", Assert.IsType<TextContent>(input.Contents[1]).Text);
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Contains("deleted", Text(done.Artifacts!.SelectMany(a => a.Parts)));
    }

    [Fact]
    public async Task Approval_works_through_a_store_that_serializes_sessions()
    {
        var agent = Asking();
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));

        var done = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) }));

        Assert.NotSame(agent.Calls[0].Session, agent.Calls[1].Session); // read back from JSON
        var answer = Assert.IsType<ToolApprovalResponseContent>(agent.Calls[1].Messages[0].Contents[0]);
        Assert.True(answer.Approved);
        Assert.Equal("delete_file", Assert.IsType<FunctionCallContent>(answer.ToolCall).Name);
        Assert.Equal(TaskState.Completed, done.Status.State);
    }

    // ---- 3. not approved ----

    [Fact]
    public async Task A_follow_up_without_the_hash_declines_with_the_fixed_reason()
    {
        var agent = Asking();
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));

        var done = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "no, keep it") }));

        var input = Assert.Single(agent.Calls[1].Messages);
        var answer = Assert.IsType<ToolApprovalResponseContent>(input.Contents[0]);
        Assert.False(answer.Approved);
        Assert.Equal(Reason, answer.Reason);
        Assert.Equal("no, keep it", Assert.IsType<TextContent>(input.Contents[1]).Text); // the caller's words are not lost
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Contains("kept: " + Reason, Text(done.Artifacts!.SelectMany(a => a.Parts)));
    }

    // ---- 4. a changed call is asked again ----

    [Fact]
    public async Task A_changed_call_after_an_approval_is_asked_again_and_the_old_hash_does_not_approve_it()
    {
        // Every run asks, with other arguments from the second run on: the approval answers the first call only.
        var agent = Asking(args: run => run == 0 ? Args() : new Dictionary<string, object?> { ["path"] = "/", ["n"] = 2 });
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var first = Task_(await client.SendMessageAsync("clean up"));

        var second = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(first, "yes", HashOf(first)) }));

        Assert.True(Assert.IsType<ToolApprovalResponseContent>(agent.Calls[1].Messages[0].Contents[0]).Approved); // the first call
        Assert.Equal(TaskState.InputRequired, second.Status.State);
        Assert.Equal("""delete_file({"n":2,"path":"/"})""", Text(second.Status.Message!.Parts));
        Assert.NotEqual(HashOf(first), HashOf(second));

        var replay = await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(second, "yes", HashOf(first)) }));
        Assert.Equal(A2AErrorCode.InvalidParams, replay.Code);
        Assert.Equal(2, agent.Calls.Count); // refused before the agent ran
    }

    // ---- 5. stale requests are never answered ----

    [Fact]
    public async Task A_new_task_in_the_context_drops_the_pending_request_and_runs_as_a_new_turn()
    {
        var agent = Asking();
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));

        await client.SendMessageAsync("something else", asked.ContextId);

        Assert.DoesNotContain(agent.Calls[1].Messages.SelectMany(m => m.Contents), c => c is ToolApprovalResponseContent);
        Assert.Equal("something else", agent.Calls[1].Messages.Single().Text);
    }

    [Fact]
    public async Task Approving_one_task_never_answers_another_tasks_request_in_the_same_context()
    {
        // Task A asks (request req-0-0); task B in the same context asks too (req-1-0). Approving A answers A's request,
        // never B's.
        var agent = Asking();
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var a = Task_(await client.SendMessageAsync("clean up"));
        var b = Task_(await client.SendMessageAsync("clean up too", a.ContextId));
        Assert.Equal(TaskState.InputRequired, b.Status.State);

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(a, "yes", HashOf(a)) });

        var answers = agent.Calls[2].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().ToList();
        Assert.Equal(["req-0-0"], answers.Select(r => r.RequestId));
    }

    [Fact]
    public async Task Another_task_in_the_context_keeps_this_tasks_pending_approval()
    {
        // Task A asks; task B in the same context asks too. Each task keeps its own pending request: approving A answers
        // A's call, and approving B afterwards answers B's. Before, B's run took A's entry out of the session's one slot.
        var agent = Asking();
        await using var host = await StartAsync(agent);
        var client = host.Client();
        var a = Task_(await client.SendMessageAsync("clean up"));
        var b = Task_(await client.SendMessageAsync("clean up too", a.ContextId));
        Assert.Equal(TaskState.InputRequired, b.Status.State);

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(a, "yes", HashOf(a)) });
        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(b, "yes", HashOf(b)) });

        var answerA = Assert.Single(agent.Calls[2].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>());
        var answerB = Assert.Single(agent.Calls[3].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>());
        Assert.True(answerA.Approved);
        Assert.True(answerB.Approved);
    }

    [Fact]
    public async Task A_task_running_at_the_same_time_in_the_context_keeps_this_tasks_pending_approval()
    {
        // Task B loads the session, then task A asks; B saves its older copy last. A's request is a record of its own, so
        // approving A still answers A's call. Before, it was in the session, B's save dropped it, and A completed with the
        // approved tool never run.
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeAIAgent((call, ct) => Concurrent(call, slowStarted, releaseSlow, ct));
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var first = Task_(await client.SendMessageAsync("hello"));

        var slow = client.SendMessageAsync("slow", first.ContextId);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var a = Task_(await client.SendMessageAsync("clean up", first.ContextId));
        Assert.Equal(TaskState.InputRequired, a.Status.State);
        releaseSlow.TrySetResult();
        Assert.Equal(TaskState.Completed, Task_(await slow).Status.State);

        var after = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(a, "yes", HashOf(a)) }));

        var answer = Assert.Single(agent.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>());
        Assert.True(answer.Approved);
        Assert.Equal("deleted", Text(after.Artifacts!.SelectMany(x => x.Parts)));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> Concurrent(FakeAIAgent.Call call, TaskCompletionSource started,
        TaskCompletionSource release, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (call.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().FirstOrDefault() is { } answer)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, answer.Approved ? "deleted" : "kept");
            yield break;
        }

        var text = string.Concat(call.Messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text));
        if (text.Contains("slow", StringComparison.Ordinal))
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new AgentResponseUpdate(ChatRole.Assistant, "slow done");
        }
        else if (text.Contains("clean", StringComparison.Ordinal))
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant,
                [new ToolApprovalRequestContent("req-1", new FunctionCallContent("call-1", "delete_file", Args()))]);
        }
        else
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "hi");
        }
    }

    [Fact]
    public async Task An_answered_request_is_deleted_and_deleting_the_context_deletes_a_waiting_one()
    {
        var agent = Asking();
        var store = new FakeSessionStore();
        await using var host = await StartAsync(agent, store);
        var client = host.Client();

        var answered = Task_(await client.SendMessageAsync("clean up"));
        Assert.Single(store.ApprovalKeys);
        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(answered, "yes", HashOf(answered)) });
        Assert.Empty(store.ApprovalKeys);

        // A request that is never answered stays until its context is deleted.
        var waiting = Task_(await client.SendMessageAsync("clean up"));
        var record = Assert.Single(store.ApprovalKeys);
        Assert.Equal("k:" + waiting.Id, record.Partitions!["a2a.task"]);
        var owner = Assert.Single(store.Keys, k => k.SessionId == waiting.ContextId).Partitions!["a2a.owner"][2..];
        var hook = host.App.Services.GetServices<A2A.Server.Retention.IA2AContextLifecycle>().Single();
        await hook.OnContextDeletedAsync(new A2A.Server.Storage.ContextRecord("", owner, waiting.ContextId, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Empty(store.ApprovalKeys);
        Assert.DoesNotContain(store.IndexKeys, k => k.SessionId == waiting.ContextId);
    }

    [Fact]
    public async Task A_failed_index_write_does_not_stop_the_approval()
    {
        // The index only serves context deletion: when it cannot be saved, the request is still saved and answered.
        var agent = Asking();
        var store = new FakeSessionStore { FailIndexSaves = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();

        var asked = Task_(await client.SendMessageAsync("clean up"));
        Assert.Equal(TaskState.InputRequired, asked.Status.State);
        Assert.Empty(store.IndexKeys);
        var done = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) }));

        Assert.True(Assert.Single(agent.Calls[1].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>()).Approved);
        Assert.Equal(TaskState.Completed, done.Status.State);
    }

    [Fact]
    public async Task An_out_of_date_session_never_answers_a_newer_request_with_the_old_call()
    {
        // The store serves an older copy of the task's request record: the request for call 1 (hash h1). After the caller
        // approved h1, the agent asks for a changed call (h2). Approving h2 must not answer the stale request for call 1.
        var agent = Asking(args: run => run == 0 ? Args() : new Dictionary<string, object?> { ["path"] = "/", ["n"] = 2 });
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var first = Task_(await client.SendMessageAsync("clean up"));
        var key = Assert.Single(store.ApprovalKeys);
        var older = (await store.GetSessionAsync(agent, key))!;
        var second = Task_(await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(first, "yes", HashOf(first)) }));
        Assert.NotEqual(HashOf(first), HashOf(second));
        await store.SaveSessionAsync(agent, key, older); // as a lagging replica would serve it

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(second, "yes", HashOf(second)) });

        Assert.DoesNotContain(agent.Calls[2].Messages.SelectMany(m => m.Contents), c => c is ToolApprovalResponseContent);
    }

    [Fact]
    public async Task An_unreadable_pending_entry_is_dropped_and_never_answered()
    {
        var agent = Asking();
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));
        // Damage the stored record, as a store with a bad copy would.
        var key = Assert.Single(store.ApprovalKeys);
        var record = (await store.GetSessionAsync(agent, key))!;
        record.StateBag.SetValue("a2a.approvalRequest", "not an approval request", AIJsonUtilities.DefaultOptions);
        await store.SaveSessionAsync(agent, key, record);

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) });

        Assert.DoesNotContain(agent.Calls[1].Messages.SelectMany(m => m.Contents), c => c is ToolApprovalResponseContent);
    }

    /// <summary>
    /// Moves the task's request from its own record into the caller's session under <paramref name="stateKey"/>, as an
    /// earlier version kept it: as one entry (the single slot) or in a list.
    /// </summary>
    private static async Task MoveRequestIntoSessionAsync(FakeSessionStore store, AIAgent agent, string stateKey, bool asList)
    {
        var recordKey = Assert.Single(store.ApprovalKeys);
        var record = (await store.GetSessionAsync(agent, recordKey))!;
        var request = record.StateBag.GetValue<System.Text.Json.Nodes.JsonObject>("a2a.approvalRequest", AIJsonUtilities.DefaultOptions)!;
        await store.DeleteSessionAsync(agent, recordKey);
        var key = Assert.Single(store.Keys);
        var session = (await store.GetSessionAsync(agent, key))!;
        if (asList) session.StateBag.SetValue(stateKey, new List<System.Text.Json.Nodes.JsonObject> { request }, AIJsonUtilities.DefaultOptions);
        else session.StateBag.SetValue(stateKey, request, AIJsonUtilities.DefaultOptions);
        await store.SaveSessionAsync(agent, key, session);
    }

    [Fact]
    public async Task A_pending_request_in_the_old_single_slot_is_still_answered_for_its_task()
    {
        // A session saved by an earlier version holds the request under "a2a.pendingApproval". It is still this task's.
        var agent = Asking();
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));
        await MoveRequestIntoSessionAsync(store, agent, "a2a.pendingApproval", asList: false);

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) });

        Assert.Equal(["req-0-0"], agent.Calls[1].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().Select(r => r.RequestId));
    }

    [Fact]
    public async Task A_pending_request_in_the_previous_versions_session_list_is_still_answered_for_its_task()
    {
        // The previous version kept a list of requests in the session under "a2a.pendingApprovals".
        var agent = Asking();
        var store = new FakeSessionStore { SerializeSessions = true };
        await using var host = await StartAsync(agent, store);
        var client = host.Client();
        var asked = Task_(await client.SendMessageAsync("clean up"));
        await MoveRequestIntoSessionAsync(store, agent, "a2a.pendingApprovals", asList: true);

        await client.SendMessageAsync(new SendMessageRequest { Message = FollowUp(asked, "yes", HashOf(asked)) });

        Assert.Equal(["req-0-0"], agent.Calls[1].Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().Select(r => r.RequestId));
    }

    // ---- 6. canonical description ----

    [Fact]
    public async Task Key_order_and_value_representation_do_not_change_the_description_or_the_hash()
    {
        var shapes = new IDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2 },
            new Dictionary<string, object?> { ["a"] = 2, ["b"] = 1 },
            new Dictionary<string, object?> { ["a"] = JsonSerializer.SerializeToElement(2), ["b"] = JsonSerializer.SerializeToElement(1) },
        };
        var seen = new List<(string Text, string? Hash)>();
        foreach (var shape in shapes)
        {
            await using var host = await StartAsync(Asking(args: _ => shape));
            var task = Task_(await host.Client().SendMessageAsync("go"));
            seen.Add((Text(task.Status.Message!.Parts), HashOf(task)));
        }

        Assert.Equal("""delete_file({"a":2,"b":1})""", seen[0].Text);
        Assert.All(seen, s => Assert.Equal(seen[0], s));
    }

    [Fact]
    public async Task A_call_id_with_a_line_feed_fails_the_task()
    {
        await using var host = await StartAsync(Asking(callId: "call\n1"));

        var task = Task_(await host.Client().SendMessageAsync("go"));

        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(Generic, Text(task.Status.Message!.Parts));
    }

    // ---- 7. registration ----

    [Fact]
    public void Bound_approvals_need_stored_sessions()
    {
        var builder = new ServiceCollection().AddA2AServer();
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddAIAgent(o => o.ToolApprovals = ToolApprovalMode.Bound));
        Assert.Contains("ToolApprovalMode.Bound", error.Message);
        Assert.Contains("AgentSessionMode.Stored", error.Message);
        Assert.Throws<InvalidOperationException>(() => new AIAgentExecutor(FakeAIAgent.Replying("x"),
            new AIAgentExecutorOptions { ToolApprovals = ToolApprovalMode.Bound }, new FakeSessionStore()));
    }

    [Fact]
    public async Task With_approvals_off_a_follow_up_hash_answers_nothing()
    {
        // Off: an approval request with output is ignored, so nothing is stored to answer.
        var agent = Asking();
        await using var host = await AgentHost.StartAsync(a => a.AddAIAgent(o => o.SessionMode = AgentSessionMode.Stored), s =>
        {
            s.AddSingleton<AIAgent>(agent);
            s.AddSingleton<AgentSessionStore>(new FakeSessionStore());
        });

        var task = Task_(await host.Client().SendMessageAsync("go"));

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Null(HashOf(task));
    }
}
