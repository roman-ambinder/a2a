using System.Security.Claims;
using A2A.Client;
using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Audit;

/// <summary>A sink that fails or hangs: fail open goes on and logs; fail closed refuses before anything changes.</summary>
public class AuditFailureTests
{
    private static ServerCallContext AliceCall(string? idempotencyKey = null) => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
        ProtocolVersion = A2AProtocol.Version,
        IdempotencyKey = idempotencyKey,
    };

    private static A2AClientOptions NoRetry => new() { MaxRetries = 0 };

    private static IEnumerable<(string Category, LogLevel Level, EventId Id, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> ErrorLogs(CapturingLoggerProvider logs) =>
        logs.Entries.Where(l => l.Level == LogLevel.Error && l.Category == typeof(A2AAuditor).FullName);

    // ---- fail open ----

    [Fact]
    public async Task Throwing_sink_does_not_fail_the_request_and_is_logged()
    {
        var sink = new RecordingAuditSink { FailWhen = _ => true };
        var logs = new CapturingLoggerProvider();
        await using var host = await AuditTestSupport.StartAsync(sink, logs: logs);

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();

        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal(1, sink.Attempts);
        var log = Assert.Single(ErrorLogs(logs));
        Assert.Equal(271, log.Id.Id);
        Assert.DoesNotContain("the audit store is down", log.Message, StringComparison.Ordinal); // the exception is attached, never part of the text
    }

    [Fact]
    public async Task Hanging_sink_that_ignores_its_token_cannot_hang_the_request()
    {
        var hang = new HangingAuditSink();
        var clock = new FakeTimeProvider();
        var logs = new CapturingLoggerProvider();
        await using var host = await AuditTestSupport.StartAsync(hang, logs: logs, services: s =>
            s.Replace(ServiceDescriptor.Singleton(sp => new A2AAuditor(hang, sp.GetRequiredService<A2AServerOptions>(),
                sp.GetRequiredService<ILogger<A2AAuditor>>(), clock))));

        var request = host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input");
        await hang.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The wait's timer may not exist yet when the sink is entered: advance until the request is through.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!request.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the request is still waiting for the sink");
            clock.Advance(new A2AServerOptions().Audit.WriteTimeout + TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Equal(TaskState.InputRequired, (await request).RequireTask().Status.State);
        Assert.Single(ErrorLogs(logs));
        hang.Release.SetResult();
    }

    [Fact]
    public async Task Sink_gets_a_token_that_is_cancelled_when_the_wait_ends()
    {
        var clock = new FakeTimeProvider();
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new TokenSink(seen);
        var auditor = new A2AAuditor(sink, new A2AServerOptions(), null, clock);
        var e = auditor.For(A2AAuditAction.TaskCreated, AliceCall(), "t1");

        var write = auditor.RecordAsync(e).AsTask();
        var token = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(token.IsCancellationRequested);
        while (!write.IsCompleted) clock.Advance(TimeSpan.FromSeconds(3));
        await write; // never throws

        Assert.True(token.IsCancellationRequested);
    }

    private sealed class TokenSink(TaskCompletionSource<CancellationToken> seen) : A2AAuditSink
    {
        public override async ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            seen.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
        }
    }

    // ---- fail closed ----

    private static Task<TestAgentHost> StartClosedAsync(RecordingAuditSink sink, Action<IServiceCollection>? services = null, CapturingLoggerProvider? logs = null) =>
        AuditTestSupport.StartAsync(sink, o => o.Audit.FailClosed = true, services, logs);

    [Fact]
    public async Task Failing_sink_refuses_a_new_task_and_nothing_is_stored()
    {
        var sink = new RecordingAuditSink { FailWhen = _ => true };
        var logs = new CapturingLoggerProvider();
        await using var host = await StartClosedAsync(sink, logs: logs);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("input"));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Equal("The request could not be audited.", ex.Message);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Equal(272, Assert.Single(ErrorLogs(logs)).Id.Id);
    }

    [Fact]
    public async Task Failing_sink_refuses_a_follow_up_and_it_is_not_in_the_history()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        sink.FailWhen = _ => true;

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Message.FromUser("more", taskId: task.Id)));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        var stored = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, stored.Status.State);
        Assert.Single(stored.History!, m => m.Role == Role.User);
    }

    [Fact]
    public async Task Failing_sink_refuses_a_cancel_and_the_task_keeps_running()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        sink.FailWhen = _ => true;

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Equal(TaskState.Working, (await client.GetTaskAsync(task.Id)).Status.State);
        sink.FailWhen = null;
        Assert.Equal(TaskState.Canceled, (await client.CancelTaskAsync(task.Id)).Status.State);
    }

    [Fact]
    public async Task Failing_sink_refuses_push_config_create_and_delete()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var kept = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/a" });
        sink.FailWhen = _ => true;

        var create = await Assert.ThrowsAsync<A2AException>(() =>
            client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/b" }));
        var delete = await Assert.ThrowsAsync<A2AException>(() =>
            client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = kept.Id! }));

        Assert.Equal(A2AErrorCode.Internal, create.Code);
        Assert.Equal(A2AErrorCode.Internal, delete.Code);
        var configs = (await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs;
        Assert.Equal(kept.Id, Assert.Single(configs).Id); // the second was never created, the first never deleted
    }

    [Fact]
    public async Task Failing_inline_push_audit_refuses_the_send_and_the_message_attempt_gets_a_final_row()
    {
        var sink = new RecordingAuditSink { FailWhen = e => e.Action == A2AAuditAction.PushConfigCreated };
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Message.FromUser("input"),
            new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "https://hooks.example/cb" } }));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Collection(sink.Events,
            e => Assert.Equal((A2AAuditAction.TaskCreated, A2AAuditOutcome.Attempted), (e.Action, e.Outcome)),
            e =>
            {
                Assert.Equal((A2AAuditAction.TaskCreated, A2AAuditOutcome.Failed), (e.Action, e.Outcome));
                Assert.Equal(nameof(A2AErrorCode.Internal), e.Reason);
            });
        Assert.Equal(sink.Events[0].Id, sink.Events[1].Id);
    }

    [Fact]
    public async Task Failing_sink_refuses_an_admin_delete_and_the_task_stays()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        sink.FailWhen = _ => true;

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
            await host.App.Services.GetRequiredService<A2ATaskAdmin>().DeleteTaskAsync(new TaskKey("", "alice", task.Id), "ops"));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Equal(task.Id, (await client.GetTaskAsync(task.Id)).Id);
    }

    [Fact]
    public async Task Failing_sink_refuses_a_redrive_and_the_dead_letter_stays()
    {
        var hooks = new ScriptedHooks(System.Net.HttpStatusCode.InternalServerError);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 1;
        await rig.SaveConfigAsync(new TaskPushNotificationConfig { Url = "https://hooks.example/cb" });
        await rig.CommitAsync("c1");
        using var service = rig.NewService();
        await rig.DrainAsync(service);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        var sink = new RecordingAuditSink { FailWhen = _ => true };
        var options = new A2AServerOptions();
        options.Audit.FailClosed = true;
        var admin = new A2APushAdmin(rig.Outbox, new A2AAuditor(sink, options));

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await admin.RedriveAsync(dead.Delivery, "ops"));
        var list = await Assert.ThrowsAsync<A2AException>(async () => await admin.ListDeadLettersAsync("", 10, "ops"));

        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Equal(A2AErrorCode.Internal, list.Code);
        Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public async Task Sink_that_fails_only_on_final_rows_does_not_fail_the_request()
    {
        var sink = new RecordingAuditSink { FailWhen = e => e.Outcome != A2AAuditOutcome.Attempted };
        var logs = new CapturingLoggerProvider();
        await using var host = await StartClosedAsync(sink, logs: logs);

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();

        Assert.Equal(TaskState.InputRequired, task.Status.State);
        var attempt = Assert.Single(sink.Events);
        Assert.Equal(A2AAuditOutcome.Attempted, attempt.Outcome);
        Assert.Equal(271, Assert.Single(ErrorLogs(logs)).Id.Id); // the final row's failure is only logged
    }

    private sealed class DenyGetTask : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.GetTask);
    }

    [Fact]
    public async Task Failing_sink_during_a_denial_keeps_the_original_refusal()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink, s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyGetTask>()));
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        sink.FailWhen = _ => true;

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code); // not Internal
    }

    [Fact]
    public async Task Attempted_and_final_rows_share_the_id()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartClosedAsync(sink);
        var client = host.Client(ProtocolBindings.JsonRpc, options: NoRetry);

        var task = (await client.SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();
        await Assert.ThrowsAsync<A2AException>(() => client.CancelTaskAsync(task.Id)); // finished: TaskNotCancelable

        Assert.Equal(
            [(A2AAuditAction.TaskCreated, A2AAuditOutcome.Attempted), (A2AAuditAction.TaskCreated, A2AAuditOutcome.Succeeded),
             (A2AAuditAction.TaskCanceled, A2AAuditOutcome.Attempted), (A2AAuditAction.TaskCanceled, A2AAuditOutcome.Failed)],
            sink.Events.Select(e => (e.Action, e.Outcome)));
        Assert.Equal(sink.Events[0].Id, sink.Events[1].Id);
        Assert.Equal(sink.Events[2].Id, sink.Events[3].Id);
        Assert.NotEqual(sink.Events[0].Id, sink.Events[2].Id);
    }

    // ---- duplicates, replays and disconnects ----

    [Fact]
    public async Task Duplicate_follow_up_has_the_reason_duplicate_and_an_idempotent_replay_adds_no_event()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, o => o.Audit.FailClosed = true);
        var created = await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("input") }, AliceCall());
        var taskId = created.RequireTask().Id;
        var followUp = Message.FromUser("more", taskId: taskId);
        followUp.MessageId = "m-follow";

        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = followUp }, AliceCall("k1"));
        var afterFirst = sink.Events.Count;
        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = followUp }, AliceCall("k1")); // the same key: a replay
        Assert.Equal(afterFirst, sink.Events.Count);
        await host.Handler.SendMessageAsync(new SendMessageRequest { Message = followUp }, AliceCall("k2")); // another key, same message id

        var rows = sink.Events.Skip(afterFirst).ToList();
        Assert.Equal([A2AAuditOutcome.Attempted, A2AAuditOutcome.Succeeded], rows.Select(r => r.Outcome));
        Assert.Equal(rows[0].Id, rows[1].Id);
        Assert.Equal(A2AAuditAction.FollowUpSent, rows[1].Action);
        Assert.Equal("duplicate", rows[1].Reason);
        Assert.Single(sink.Of(A2AAuditAction.FollowUpSent), e => e.Outcome == A2AAuditOutcome.Succeeded && e.Reason is null);
    }

    private sealed class CancelOnAttemptSink(CancellationTokenSource client) : RecordingAuditSink
    {
        public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (auditEvent.Outcome == A2AAuditOutcome.Attempted) client.Cancel(); // the client goes away right after the Attempted row
            return base.WriteAsync(auditEvent, cancellationToken);
        }
    }

    [Fact]
    public async Task Client_that_disconnects_after_the_attempt_still_gets_its_final_row()
    {
        using var client = new CancellationTokenSource();
        var sink = new CancelOnAttemptSink(client);
        await using var host = await AuditTestSupport.StartAsync(sink, o => o.Audit.FailClosed = true);

        try
        {
            await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("input") }, AliceCall(), client.Token);
        }
        catch (OperationCanceledException)
        {
            // either way the record is complete
        }

        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(A2AAuditOutcome.Attempted, sink.Events[0].Outcome);
        Assert.NotEqual(A2AAuditOutcome.Attempted, sink.Events[1].Outcome);
        Assert.Equal(sink.Events[0].Id, sink.Events[1].Id);
    }
}
