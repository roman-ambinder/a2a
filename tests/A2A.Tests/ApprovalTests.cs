using System.Text.Json;
using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>Asks for an approval on "approve", for input on "plain"; records what each follow-up says about the approval.</summary>
public sealed class ApprovalAgent : IAgentExecutor
{
    public static readonly PendingApproval Pending = new("op-1", "delete the production database");

    public List<ApprovalState> Seen { get; } = [];
    public int Executions;

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        if (context.Task is null)
        {
            if (context.UserText == "approve") await updater.RequireApprovalAsync(Pending, "Delete the database?", ct);
            else await updater.RequireInputAsync("need more", ct);
            return;
        }
        lock (Seen) Seen.Add(context.Approval);
        if (context.Approval == ApprovalState.NotApproved) await updater.RequireApprovalAsync(Pending, "Delete the database?", ct);
        else await updater.CompleteAsync($"done {context.Approval}", ct);
    }
}

public class ApprovalTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static Message FollowUp(AgentTask task, string? hash, string? id = null)
    {
        var m = Message.FromUser("go ahead", taskId: task.Id);
        if (id is not null) m.MessageId = id;
        if (hash is not null)
        {
            using var doc = JsonDocument.Parse($"\"{hash}\"");
            m.Metadata = new() { [A2AProtocol.ApprovalHashKey] = doc.RootElement.Clone() };
        }
        return m;
    }

    private static Task<TestAgentHost> StartAsync(ApprovalAgent agent) =>
        TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));

    [Fact]
    public void Hash_is_lowercase_hex_sha256_of_operation_and_description()
    {
        var hash = new PendingApproval("a", "b").Hash;
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("a\nb"u8)).ToLowerInvariant(), hash);
        Assert.Equal(64, hash.Length);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Matching_hash_approves(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        Assert.Equal(TaskState.InputRequired, asked.Status.State);
        Assert.Equal(ApprovalAgent.Pending.Hash, asked.Status.Message!.Metadata![A2AProtocol.ApprovalHashKey].GetString());

        var done = (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash))).RequireTask();

        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal([ApprovalState.Approved], agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Mismatched_hash_rejected(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(FollowUp(asked, new PendingApproval("op-1", "something else").Hash)));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("Approval does not match the pending request.", ex.Message);
        Assert.Empty(agent.Seen);
        var stored = await client.GetTaskAsync(asked.Id);
        Assert.Equal(TaskState.InputRequired, stored.Status.State);
        Assert.Single(stored.History!, m => m.Role == Role.User); // the refused message was not appended
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Missing_hash_is_not_approved(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        var again = (await client.SendMessageAsync(FollowUp(asked, null))).RequireTask();

        Assert.Equal([ApprovalState.NotApproved], agent.Seen);
        Assert.Equal(TaskState.InputRequired, again.Status.State); // the executor re-asked
        Assert.Equal(ApprovalAgent.Pending.Hash, again.Status.Message!.Metadata![A2AProtocol.ApprovalHashKey].GetString());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Executor_without_pending_sees_none(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("plain")).RequireTask();

        var done = (await client.SendMessageAsync(FollowUp(asked, null))).RequireTask();

        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal([ApprovalState.None], agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_follow_up_is_not_appended_twice(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("plain")).RequireTask();
        var messageId = Message.NewId();
        var first = (await client.SendMessageAsync(FollowUp(asked, null, messageId))).RequireTask();
        Assert.Equal(TaskState.Completed, first.Status.State);

        // A retry the idempotency store does not recognise (a different key), as after an abandoned claim.
        var retry = (await client.SendMessageAsync(FollowUp(asked, null, messageId), options: new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() })).RequireTask();

        Assert.Equal(TaskState.Completed, retry.Status.State);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Executions); // the first message and the follow-up, not the retry
        var stored = await client.GetTaskAsync(asked.Id);
        Assert.Equal(2, stored.History!.Count(m => m.Role == Role.User));
        Assert.Single(stored.History!, m => m.MessageId == messageId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_follow_up_on_a_stream_resubscribes(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("plain")).RequireTask();
        var messageId = Message.NewId();
        await client.SendMessageAsync(FollowUp(asked, null, messageId));

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(FollowUp(asked, null, messageId), options: new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() }))
            events.Add(e);

        Assert.NotEmpty(events);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Executions);
        var stored = await client.GetTaskAsync(asked.Id);
        Assert.Single(stored.History!, m => m.MessageId == messageId);
    }
}
