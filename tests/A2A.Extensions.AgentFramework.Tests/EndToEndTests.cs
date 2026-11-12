using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary>
/// Both halves together over the ASP.NET test host, on JSON-RPC and HTTP+JSON: an <see cref="AIAgent"/> hosted with
/// <c>AddAIAgent</c>, called with <c>AsAIAgent</c>. (Plain unary and streaming answers are in
/// <see cref="RemoteAgentTests.A_hosted_agent_answers_a_remote_agent_over_unary_and_streaming"/>.)
/// </summary>
public class EndToEndTests
{
    private static Task<AgentHost> StartStoredAsync(AIAgent agent, FakeSessionStore store, bool approvals = false) =>
        AgentHost.StartAsync(a => a.AddAIAgent(o =>
        {
            o.SessionMode = AgentSessionMode.Stored;
            if (approvals) o.ToolApprovals = ToolApprovalMode.Bound;
        }), s =>
        {
            s.AddSingleton(agent);
            s.AddSingleton<AgentSessionStore>(store);
        });

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task One_remote_session_drives_one_stored_session_over_two_turns(string binding)
    {
        var hosted = FakeAIAgent.Streaming(c => [new AgentResponseUpdate(ChatRole.Assistant, "got " + c.Messages[^1].Text)]);
        var store = new FakeSessionStore();
        await using var host = await StartStoredAsync(hosted, store);
        var remote = host.Client(binding).AsAIAgent();
        var session = Assert.IsType<A2ARemoteAgentSession>(await remote.CreateSessionAsync());

        var first = await remote.RunAsync("q1", session);
        var contextId = session.ContextId;
        var second = await remote.RunAsync("q2", session);

        Assert.Equal(("got q1", "got q2"), (first.Text, second.Text));
        Assert.Equal(contextId, session.ContextId);
        Assert.Equal(["q2"], hosted.Calls[1].Messages.Select(m => m.Text)); // the server sends only the new message
        Assert.Same(hosted.Calls[0].Session, hosted.Calls[1].Session);      // into the one stored session
        Assert.Equal(contextId, Assert.Single(store.Keys).SessionId);
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task A_bound_approval_reaches_the_remote_caller_and_a_follow_up_with_the_hash_completes_the_task(string binding)
    {
        var hosted = FakeAIAgent.Streaming(c =>
            c.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().FirstOrDefault() is { } answer
                ? [new AgentResponseUpdate(ChatRole.Assistant, answer.Approved ? "deleted" : "kept")]
                : [new AgentResponseUpdate(ChatRole.Assistant, [new ToolApprovalRequestContent("r1",
                    new FunctionCallContent("call-1", "delete_file", new Dictionary<string, object?> { ["path"] = "/tmp/x" }))])]);
        await using var host = await StartStoredAsync(hosted, new FakeSessionStore(), approvals: true);
        var client = host.Client(binding);
        var remote = client.AsAIAgent();
        var session = Assert.IsType<A2ARemoteAgentSession>(await remote.CreateSessionAsync());

        var question = await remote.RunAsync("clean up", session);

        Assert.Equal("""delete_file({"path":"/tmp/x"})""", question.Text);
        Assert.Equal("input-required", question.AdditionalProperties![A2AAgentKeys.TaskState]);
        Assert.NotNull(session.TaskId);

        // Phase 2 will answer approvals through the remote agent; today the caller echoes the hash itself.
        var waiting = await client.GetTaskAsync(session.TaskId!);
        var hash = waiting.Status.Message!.Metadata![A2AProtocol.ApprovalHashKey].GetString();
        var follow = Message.FromUser("yes");
        follow.TaskId = session.TaskId;
        follow.ContextId = session.ContextId;
        follow.Metadata = new() { [A2AProtocol.ApprovalHashKey] = JsonSerializer.SerializeToElement(hash) };
        var done = Assert.IsType<TaskResponse>(await client.SendMessageAsync(new SendMessageRequest { Message = follow })).Task;

        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Contains("deleted", string.Concat(done.Artifacts!.SelectMany(a => a.Parts).OfType<TextPart>().Select(p => p.Text)));
    }

    [Theory]
    [MemberData(nameof(Bindings.All), MemberType = typeof(Bindings))]
    public async Task Cancelling_the_remote_task_cancels_the_hosted_run(string binding)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosted = new FakeAIAgent((_, ct) => Blocking(ct));
        await using var host = await AgentHost.StartAsync(hosted);
        var client = host.Client(binding);
        var remote = client.AsAIAgent();

        var running = await remote.RunAsync("slow", options: new AgentRunOptions { AllowBackgroundResponses = true });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var taskId = (string)running.AdditionalProperties![A2AAgentKeys.TaskId]!;
        var canceled = await client.CancelTaskAsync(taskId);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        // Polling the canceled task through the remote agent reports it as such.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.RunAsync([], options: new AgentRunOptions { ContinuationToken = running.ContinuationToken }));
        Assert.Contains("canceled", error.Message);

        async IAsyncEnumerable<AgentResponseUpdate> Blocking([EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observed.TrySetResult(); throw; }
            yield break;
        }
    }
}
