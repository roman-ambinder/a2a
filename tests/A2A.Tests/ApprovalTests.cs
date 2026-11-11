using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>Asks for an approval on "approve", for input on "plain"; records what each follow-up says about the approval.</summary>
public sealed class ApprovalAgent : IAgentExecutor
{
    public static readonly PendingApproval Pending = new("op-1", "delete the production database");

    private static Message AuthMessage(TaskUpdater updater)
    {
        var m = updater.NewAgentMessage("authorize and approve");
        using var doc = JsonDocument.Parse($"\"{ApprovalAgent.Pending.Hash}\"");
        m.Metadata = new() { [A2AProtocol.ApprovalHashKey] = doc.RootElement.Clone() };
        return m;
    }

    public List<ApprovalState> Seen { get; } = [];
    public int Executions;

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        if (context.Task is null)
        {
            if (context.UserText == "approve") await updater.RequireApprovalAsync(Pending, "Delete the database?", ct);
            else if (context.UserText == "auth-approve")
                await updater.UpdateStatusAsync(TaskState.AuthRequired, AuthMessage(updater), null, ct);
            else await updater.RequireInputAsync("need more", ct);
            return;
        }
        lock (Seen) Seen.Add(context.Approval);
        if (context.UserText == "approved-but-ask-again" && context.Approval == ApprovalState.Approved)
            await updater.RequireApprovalAsync(Pending, "Really delete the database?", ct); // a new approval of the same operation
        else if (context.UserText == "approved-but-stay" && context.Approval == ApprovalState.Approved)
            return; // ends without changing the task's state
        else if (context.Approval == ApprovalState.NotApproved) await updater.RequireApprovalAsync(Pending, "Delete the database?", ct);
        else await updater.CompleteAsync($"done {context.Approval}", ct);
    }
}

public class ApprovalTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static Message FollowUp(AgentTask task, string? hash, string? id = null, string text = "go ahead")
    {
        var m = Message.FromUser(text, taskId: task.Id);
        if (id is not null) m.MessageId = id;
        if (hash is not null)
        {
            using var doc = JsonDocument.Parse($"\"{hash}\"");
            m.Metadata = new() { [A2AProtocol.ApprovalHashKey] = doc.RootElement.Clone() };
        }
        return m;
    }

    private static Task<TestAgentHost> StartAsync(ApprovalAgent agent, Action<A2AServerOptions>? configure = null,
        Action<IServiceCollection>? services = null) =>
        TestAgentHost.StartAsync(configure, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
            services?.Invoke(s);
        });

    /// <summary>Allows everything but SubscribeToTask.</summary>
    private sealed class DenySubscribe : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor op, object request, CancellationToken ct) =>
            ValueTask.FromResult(op.Kind != A2AOperation.SubscribeToTask);
    }

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
    [InlineData("op\n1")]
    [InlineData("\n")]
    public void Operation_id_with_a_line_feed_is_rejected(string operationId)
    {
        // The hash joins the two fields with "\n": ("a\nb", "c") and ("a", "b\nc") would share one hash.
        Assert.Throws<ArgumentException>(() => new PendingApproval(operationId, "description"));
        Assert.Throws<ArgumentException>(() => new PendingApproval("op", "description") with { OperationId = operationId });
        Assert.Equal(new PendingApproval("op", "a\nb").Hash, new PendingApproval("op", "a\nb").Hash); // the description may hold one
    }

    /// <summary>
    /// The pending status message has an empty message id (unreachable through the API: ids are stamped; a null id does not
    /// even round-trip through the store, the JSON property is required). The approval could never be marked used, so it
    /// fails closed: a hash is InvalidParams, no hash is NotApproved.
    /// </summary>
    [Fact]
    public async Task Approval_whose_status_message_has_no_id_fails_closed()
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        // The stored task is corrupted below, so its history has a message without an id: accept it to test the server.
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0, ValidateResponses = false });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        await host.WaitIdleAsync();
        var store = host.App.Services.GetRequiredService<A2A.Server.Storage.TaskStore>();
        var key = new A2A.Server.Storage.TaskKey("", "alice", asked.Id);
        var record = (await store.GetAsync(key))!;
        record.Task.Status.Message!.MessageId = "";
        await store.UpdateAsync(record, record.Version);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Empty(agent.Seen);

        var again = (await client.SendMessageAsync(FollowUp(asked, null))).RequireTask();
        Assert.Equal([ApprovalState.NotApproved], agent.Seen);
        Assert.Equal(TaskState.InputRequired, again.Status.State);
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

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_follow_up_on_a_stream_resubscribes_with_one_stream_per_task(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent, o => o.Limits.MaxConcurrentStreamsPerTask = 1);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("plain")).RequireTask();
        var messageId = Message.NewId();
        await client.SendMessageAsync(FollowUp(asked, null, messageId));

        // Nothing else streams the task: the resume of the duplicate gets the one slot.
        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(FollowUp(asked, null, messageId), options: new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() }))
            events.Add(e);

        Assert.NotEmpty(events);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Executions);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_follow_up_on_a_stream_is_authorized_as_subscribe(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent, services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenySubscribe>()));
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("plain")).RequireTask();
        var messageId = Message.NewId();

        // The first streaming send is allowed.
        var first = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(FollowUp(asked, null, messageId)))
            first.Add(e);
        Assert.NotEmpty(first);
        await host.WaitIdleAsync();

        // The duplicate resumes the task, a SubscribeToTask the caller may not do.
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(FollowUp(asked, null, messageId), options: new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() })) { }
        });
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(2, agent.Executions);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Concurrent_approvals_approve_once(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return (Task: (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash, text: "approved-but-stay"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask(), Error: (A2AException?)null); }
            catch (A2AException ex) { return (Task: null!, Error: ex); }
        }));

        Assert.Single(results, r => r.Error is null);
        var rejected = Assert.Single(results, r => r.Error is not null).Error!;
        Assert.Equal(A2AErrorCode.InvalidParams, rejected.Code);
        await host.WaitIdleAsync();
        Assert.Equal([ApprovalState.Approved], agent.Seen);
        Assert.Equal(2, (await client.GetTaskAsync(asked.Id)).History!.Count(m => m.Role == Role.User)); // the rejected message was not appended
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Replayed_approval_is_rejected_after_use(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        var first = (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash, text: "approved-but-stay"))).RequireTask();
        await host.WaitIdleAsync();
        Assert.Equal(TaskState.InputRequired, (await client.GetTaskAsync(asked.Id)).Status.State); // the state did not change

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash, text: "approved-but-stay")));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("Approval does not match the pending request.", ex.Message);
        await host.WaitIdleAsync();
        Assert.Equal([ApprovalState.Approved], agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task New_approval_of_the_same_operation_approves_again(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        var second = (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash, text: "approved-but-ask-again"))).RequireTask();
        Assert.Equal(TaskState.InputRequired, second.Status.State);
        Assert.NotEqual(asked.Status.Message!.MessageId, second.Status.Message!.MessageId);

        var done = (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash))).RequireTask();

        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal([ApprovalState.Approved, ApprovalState.Approved], agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Auth_required_approval_binds_too(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("auth-approve")).RequireTask();
        Assert.Equal(TaskState.AuthRequired, asked.Status.State);

        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(FollowUp(asked, new PendingApproval("op-1", "other").Hash)));
        var done = (await client.SendMessageAsync(FollowUp(asked, ApprovalAgent.Pending.Hash))).RequireTask();

        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal([ApprovalState.Approved], agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Non_string_hash_is_invalid_params(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        var m = Message.FromUser("go ahead", taskId: asked.Id);
        using var doc = JsonDocument.Parse("42");
        m.Metadata = new() { [A2AProtocol.ApprovalHashKey] = doc.RootElement.Clone() };

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(m));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Empty(agent.Seen);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_while_input_required_starts_nothing(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("approve")).RequireTask();
        var messageId = Message.NewId();
        await client.SendMessageAsync(FollowUp(asked, null, messageId)); // not approved: the agent asks again, still INPUT_REQUIRED

        var retry = (await client.SendMessageAsync(FollowUp(asked, null, messageId), options: new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() })).RequireTask();

        Assert.Equal(TaskState.InputRequired, retry.Status.State);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Executions);
        Assert.Single((await client.GetTaskAsync(asked.Id)).History!, m => m.MessageId == messageId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_with_inline_push_config_saves_nothing(string binding)
    {
        var agent = new ApprovalAgent();
        await using var host = await StartAsync(agent);
        var client = host.Client(binding);
        var asked = (await client.SendMessageAsync("plain")).RequireTask();
        var messageId = Message.NewId();
        await client.SendMessageAsync(FollowUp(asked, null, messageId));
        var before = (await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = asked.Id })).Configs.Count;

        var configuration = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://localhost/hook", Id = "dup-config" },
        };
        await client.SendMessageAsync(FollowUp(asked, null, messageId), configuration, new A2ARequestOptions { IdempotencyKey = Guid.NewGuid().ToString() });

        Assert.Equal(before, (await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = asked.Id })).Configs.Count);
    }
}
