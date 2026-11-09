using System.Net;
using System.Security.Claims;
using System.Text.Json;
using A2A.Client;
using A2A.Elicitations;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Skills;
using A2A.Tests.Elicitations;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Audit;

/// <summary>Each audited action leaves one event with the right fields. Fail open: only the final row is written.</summary>
public class AuditActionTests
{
    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
        ProtocolVersion = A2AProtocol.Version,
        Binding = ProtocolBindings.JsonRpc,
    };

    private static Message ApprovalFollowUp(AgentTask task, string? hash)
    {
        var m = Message.FromUser("go ahead", taskId: task.Id);
        if (hash is not null)
        {
            using var doc = JsonDocument.Parse($"\"{hash}\"");
            m.Metadata = new() { [A2AProtocol.ApprovalHashKey] = doc.RootElement.Clone() };
        }
        return m;
    }

    private static Task<TestAgentHost> StartApprovalAsync(RecordingAuditSink sink, ApprovalAgent agent) =>
        AuditTestSupport.StartAsync(sink, services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));

    private static void AssertFinal(A2AAuditEvent e, A2AAuditAction action, string taskId, A2AAuditOutcome outcome = A2AAuditOutcome.Succeeded)
    {
        Assert.Equal(action, e.Action);
        Assert.Equal(outcome, e.Outcome);
        Assert.Equal("alice", e.Actor);
        Assert.Equal("", e.Tenant);
        Assert.Equal(taskId, e.TaskId);
        Assert.Equal(32, e.Id.Length);
        Assert.True(Guid.TryParseExact(e.Id, "N", out _));
    }

    [Fact]
    public async Task New_task_is_TaskCreated()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var before = DateTimeOffset.UtcNow;

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();

        var e = Assert.Single(sink.Events);
        AssertFinal(e, A2AAuditAction.TaskCreated, task.Id);
        Assert.Equal(ProtocolBindings.JsonRpc, e.Binding);
        Assert.Null(e.Resource);
        Assert.Null(e.Reason);
        Assert.Null(e.Operation);
        Assert.InRange(e.Time, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(TimeSpan.Zero, e.Time.Offset);
    }

    [Fact]
    public async Task Follow_up_is_FollowUpSent()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();

        await client.SendMessageAsync(Message.FromUser("more", taskId: task.Id));

        Assert.Equal([A2AAuditAction.TaskCreated, A2AAuditAction.FollowUpSent], sink.Events.Select(e => e.Action));
        var follow = sink.Events[1];
        AssertFinal(follow, A2AAuditAction.FollowUpSent, task.Id);
        Assert.Null(follow.Reason);
    }

    [Fact]
    public async Task Matching_hash_is_ApprovalGranted_with_the_status_message_as_resource()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartApprovalAsync(sink, new ApprovalAgent());
        var client = host.Client(ProtocolBindings.JsonRpc);
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        await client.SendMessageAsync(ApprovalFollowUp(asked, ApprovalAgent.Pending.Hash));

        var e = Assert.Single(sink.Of(A2AAuditAction.ApprovalGranted));
        AssertFinal(e, A2AAuditAction.ApprovalGranted, asked.Id);
        Assert.Equal(asked.Status.Message!.MessageId, e.Resource);
        Assert.Empty(sink.Of(A2AAuditAction.FollowUpSent));
    }

    [Fact]
    public async Task Follow_up_without_the_hash_is_ApprovalDeclined()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartApprovalAsync(sink, new ApprovalAgent());
        var client = host.Client(ProtocolBindings.JsonRpc);
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        await client.SendMessageAsync(ApprovalFollowUp(asked, null));

        var e = Assert.Single(sink.Of(A2AAuditAction.ApprovalDeclined));
        AssertFinal(e, A2AAuditAction.ApprovalDeclined, asked.Id);
        Assert.Null(e.Resource);
    }

    [Fact]
    public async Task Wrong_hash_is_ApprovalGranted_Failed_InvalidParams()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartApprovalAsync(sink, new ApprovalAgent());
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });
        var asked = (await client.SendMessageAsync("approve")).RequireTask();

        await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(ApprovalFollowUp(asked, new PendingApproval("op-1", "something else").Hash)));

        var e = Assert.Single(sink.Of(A2AAuditAction.ApprovalGranted));
        AssertFinal(e, A2AAuditAction.ApprovalGranted, asked.Id, A2AAuditOutcome.Failed);
        Assert.Equal(nameof(A2AErrorCode.InvalidParams), e.Reason);
        Assert.Null(e.Resource);
    }

    [Theory]
    [InlineData(true, "Accept")]
    [InlineData(false, "Decline")]
    public async Task Answered_form_is_ElicitationAnswered_with_the_action_as_reason(bool accept, string reason)
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink,
            o => o.AgentCard.Capabilities.Extensions = [Elicitation.CreateExtension()],
            s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(new ElicitationServerTests.Agent())));
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { ValidateCapabilities = false });
        var asked = (await client.SendMessageAsync(Message.FromUser("book"), options: new A2ARequestOptions().UseElicitation())).RequireTask();
        var form = asked.GetElicitationForm()!;
        var answer = accept
            ? ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" }, { "nights", 2 } })
            : ElicitationAnswer.Decline(form);

        await client.AnswerElicitationAsync(asked, answer, options: new A2ARequestOptions().UseElicitation());

        var e = Assert.Single(sink.Of(A2AAuditAction.ElicitationAnswered));
        AssertFinal(e, A2AAuditAction.ElicitationAnswered, asked.Id);
        Assert.Equal(reason, e.Reason);
    }

    [Fact]
    public async Task Cancel_is_TaskCanceled()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        await client.CancelTaskAsync(task.Id);

        var e = Assert.Single(sink.Of(A2AAuditAction.TaskCanceled));
        AssertFinal(e, A2AAuditAction.TaskCanceled, task.Id);
        Assert.Null(e.Reason);
    }

    [Fact]
    public async Task Cancel_that_is_only_requested_has_that_reason()
    {
        var sink = new RecordingAuditSink();
        var agent = new StubbornAgent();
        await using var host = await AuditTestSupport.StartAsync(sink, o => o.CancelGracePeriod = TimeSpan.FromMilliseconds(200),
            s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("keep"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        await client.CancelTaskAsync(task.Id);

        var e = Assert.Single(sink.Of(A2AAuditAction.TaskCanceled));
        Assert.Equal(A2AAuditOutcome.Succeeded, e.Outcome);
        Assert.Equal("CancelRequested", e.Reason);
        agent.Release.SetResult();
        await host.WaitIdleAsync();
    }

    [Fact]
    public async Task Cancel_of_a_finished_task_is_Failed_with_the_error_code()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id));

        var e = Assert.Single(sink.Of(A2AAuditAction.TaskCanceled));
        AssertFinal(e, A2AAuditAction.TaskCanceled, task.Id, A2AAuditOutcome.Failed);
        Assert.Equal(nameof(A2AErrorCode.TaskNotCancelable), e.Reason);
    }

    private static readonly byte[] Payload = [1, 2, 3, 4, 5];

    private static Task<TestAgentHost> StartWithArtifactsAsync(RecordingAuditSink sink, ArtifactObjectStore store) =>
        TestAgentHost.StartAsync(o => o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl),
            services: s =>
            {
                s.Replace(ServiceDescriptor.Singleton<A2AAuditSink>(sink));
                s.AddSingleton(store);
            },
            configurePipeline: app => app.MapA2AArtifacts("/a2a").RequireAuthorization());

    [Fact]
    public async Task Artifact_download_and_miss_are_audited()
    {
        var sink = new RecordingAuditSink();
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartWithArtifactsAsync(sink, store);
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).RequireTask();
        var objectId = ArtifactObjectKey.NewObjectId();
        await store.PutAsync(ArtifactObjectKey.Create("", task.Id, objectId), new MemoryStream(Payload), new ArtifactObjectInfo("application/pdf", "r.pdf"), long.MaxValue);
        var missing = ArtifactObjectKey.NewObjectId();

        using var ok = await host.Http("alice").GetAsync($"/a2a/artifacts/_/{task.Id}/{objectId}");
        using var miss = await host.Http("alice").GetAsync($"/a2a/artifacts/_/{task.Id}/{missing}");
        using var bad = await host.Http("alice").GetAsync($"/a2a/artifacts/_/{task.Id}/not-an-object-id");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        var downloads = sink.Of(A2AAuditAction.ArtifactDownloaded);
        Assert.Equal(3, downloads.Count);
        AssertFinal(downloads[0], A2AAuditAction.ArtifactDownloaded, task.Id);
        Assert.Equal(objectId, downloads[0].Resource);
        AssertFinal(downloads[1], A2AAuditAction.ArtifactDownloaded, task.Id, A2AAuditOutcome.Failed);
        Assert.Equal(missing, downloads[1].Resource);
        Assert.Equal(nameof(A2AErrorCode.TaskNotFound), downloads[1].Reason);
        Assert.Equal(A2AAuditOutcome.Failed, downloads[2].Outcome);
        Assert.Null(downloads[2].Resource); // an id that is not a valid object id is never copied into the event
    }

    private static TaskPushNotificationConfig Hook(string taskId, string? id = null) =>
        new() { TaskId = taskId, Id = id, Url = "https://hooks.example/cb" };

    [Fact]
    public async Task Push_config_create_and_delete_are_audited()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();

        var created = await client.CreateTaskPushNotificationConfigAsync(Hook(task.Id));
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });

        var create = Assert.Single(sink.Of(A2AAuditAction.PushConfigCreated));
        AssertFinal(create, A2AAuditAction.PushConfigCreated, task.Id);
        Assert.Equal(created.Id, create.Resource);
        var delete = Assert.Single(sink.Of(A2AAuditAction.PushConfigDeleted));
        AssertFinal(delete, A2AAuditAction.PushConfigDeleted, task.Id);
        Assert.Equal(created.Id, delete.Resource);
    }

    [Fact]
    public async Task Inline_push_config_is_a_second_event()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc);

        var task = (await client.SendMessageAsync(Message.FromUser("input"),
            new SendMessageConfiguration { TaskPushNotificationConfig = Hook("", "cfg-1") })).RequireTask();

        Assert.Equal(2, sink.Events.Count);
        AssertFinal(Assert.Single(sink.Of(A2AAuditAction.TaskCreated)), A2AAuditAction.TaskCreated, task.Id);
        var push = Assert.Single(sink.Of(A2AAuditAction.PushConfigCreated));
        AssertFinal(push, A2AAuditAction.PushConfigCreated, task.Id);
        Assert.Equal("cfg-1", push.Resource);
        Assert.NotEqual(sink.Of(A2AAuditAction.TaskCreated)[0].Id, push.Id);
    }

    [Fact]
    public async Task Admin_task_delete_names_the_actor_and_a_missing_task_is_Failed()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();
        var admin = host.App.Services.GetRequiredService<A2ATaskAdmin>();

        Assert.True(await admin.DeleteTaskAsync(new TaskKey("", "alice", task.Id), "ops-bob"));
        Assert.False(await admin.DeleteTaskAsync(new TaskKey("", "alice", task.Id), "ops-bob"));
        Assert.False(await admin.DeleteTaskAsync(new TaskKey("acme", "alice", "t-9")));

        var deletes = sink.Of(A2AAuditAction.AdminTaskDeleted);
        Assert.Equal(3, deletes.Count);
        Assert.Equal(A2AAuditOutcome.Succeeded, deletes[0].Outcome);
        Assert.Equal("ops-bob", deletes[0].Actor);
        Assert.Equal(task.Id, deletes[0].TaskId);
        Assert.Equal("", deletes[0].Tenant);
        Assert.Null(deletes[0].Binding);
        Assert.Equal(A2AAuditOutcome.Failed, deletes[1].Outcome);
        Assert.Equal("NotFound", deletes[1].Reason);
        Assert.Equal(A2AAuditEvent.UnknownOperator, deletes[2].Actor);
        Assert.Equal("acme", deletes[2].Tenant);
        Assert.Equal("t-9", deletes[2].TaskId);
    }

    // ---- denials ----

    private sealed class DenyGetTask : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.GetTask && operation.Kind != A2AOperation.ListTasks);
    }

    private sealed class DenySkills : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeSkillAsync(ServerCallContext call, string skillId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    [Fact]
    public async Task Denied_skill_is_audited()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, o => o.SkillAuthorization.Mode = SkillAuthorizationMode.WhenNamed,
            s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenySkills>()));
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0, ValidateCapabilities = false });
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("work")] }.SetRequestedSkillId("scenario");

        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(message));

        var denied = Assert.Single(sink.Of(A2AAuditAction.AccessDenied));
        Assert.Equal(A2AAuditOutcome.Denied, denied.Outcome);
        Assert.Equal("PermissionDenied", denied.Reason);
        Assert.Equal(A2AOperation.SendMessage.ToString(), denied.Operation);
    }

    [Fact]
    public async Task Refused_context_is_audited()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);
        var options = new A2AClientOptions { MaxRetries = 0 };
        var task = (await host.Client(ProtocolBindings.JsonRpc, "alice", options).SendMessageAsync("input")).RequireTask();
        var bob = host.Client(ProtocolBindings.JsonRpc, "bob", options);

        await Assert.ThrowsAsync<A2AException>(() => bob.SendMessageAsync(new Message
        {
            MessageId = "m2", Role = Role.User, Parts = [Part.FromText("join")], ContextId = task.ContextId,
        }));

        await sink.WaitUntilAsync(events => events.Any(e => e.Action == A2AAuditAction.AccessDenied));
        var denied = Assert.Single(sink.Of(A2AAuditAction.AccessDenied));
        Assert.Equal("bob", denied.Actor);
        Assert.Equal(A2AAuditOutcome.Denied, denied.Outcome);
        Assert.Equal(task.ContextId, denied.Resource);
    }

    [Fact]
    public async Task Denial_is_audited_with_its_true_reason_even_when_the_wire_says_TaskNotFound()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyGetTask>()));
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { MaxRetries = 0 });
        var task = (await client.SendMessageAsync("input")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(task.Id));
        var list = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(A2AErrorCode.PermissionDenied, list.Code);
        var denied = sink.Of(A2AAuditAction.AccessDenied);
        Assert.Equal(2, denied.Count);
        Assert.All(denied, e =>
        {
            Assert.Equal(A2AAuditOutcome.Denied, e.Outcome);
            Assert.Equal("PermissionDenied", e.Reason);
            Assert.Equal("alice", e.Actor);
            Assert.Equal(ProtocolBindings.JsonRpc, e.Binding);
        });
        Assert.Equal("GetTask", denied[0].Operation);
        Assert.Equal(task.Id, denied[0].TaskId);
        Assert.Equal("ListTasks", denied[1].Operation);
        Assert.Null(denied[1].TaskId);
    }

    [Fact]
    public async Task Unauthenticated_call_is_audited_from_ValidateCall_without_waiting_for_the_sink()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);

        var ex = Assert.Throws<A2AException>(() => host.Handler.ValidateCall(ServerCallContext.Anonymous()));

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        await sink.WaitUntilAsync(events => events.Count > 0);
        var e = Assert.Single(sink.Events);
        Assert.Equal(A2AAuditAction.AccessDenied, e.Action);
        Assert.Equal(A2AAuditOutcome.Denied, e.Outcome);
        Assert.Equal("Unauthenticated", e.Reason);
        Assert.Equal(ServerCallContext.AnonymousPrincipal, e.Actor);
        Assert.Null(e.Operation);
    }

    [Fact]
    public async Task Direct_anonymous_call_is_audited_by_the_authorization_step()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink);

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
            await host.Handler.GetTaskAsync(new GetTaskRequest { Id = "t1" }, ServerCallContext.Anonymous()));

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        var e = Assert.Single(sink.Events);
        Assert.Equal("Unauthenticated", e.Reason);
        Assert.Equal("GetTask", e.Operation);
        Assert.Equal("t1", e.TaskId);
    }

    private sealed class Switchable : A2AAuthorizationHandler
    {
        public volatile bool Allow = true;

        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor op, object request, CancellationToken ct) =>
            ValueTask.FromResult(Allow);
    }

    [Fact]
    public async Task Stream_that_loses_access_is_audited()
    {
        var sink = new RecordingAuditSink();
        var auth = new Switchable();
        await using var host = await AuditTestSupport.StartAsync(sink, o => o.Limits.StreamReauthInterval = TimeSpan.FromMilliseconds(100),
            s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(auth)));
        await using var stream = host.Handler.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("slow") }, AliceCall()).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var taskId = Assert.IsType<TaskEvent>(stream.Current.Event).Task.Id;

        auth.Allow = false;
        // "slow" never finishes, so the stream ends only because the re-check refused it.
        while (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30))) { }

        var denied = Assert.Single(sink.Of(A2AAuditAction.AccessDenied));
        Assert.Equal(A2AAuditOutcome.Denied, denied.Outcome);
        Assert.Equal("PermissionDenied", denied.Reason);
        Assert.Equal("SendStreamingMessage", denied.Operation);
        Assert.Equal(taskId, denied.TaskId ?? taskId); // a send names no task of its own until it is created
        auth.Allow = true;
        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, AliceCall());
    }
}
