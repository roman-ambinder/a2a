using A2A.Client;
using A2A.Server.Audit;

namespace A2A.Tests.Audit;

/// <summary>The hooks sit in the request handler, so every binding feeds the same audit trail.</summary>
public class AuditBindingTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task SendMessage_and_cancel_are_audited_with_the_binding(string binding)
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, grpc: true);
        var client = host.Client(binding);

        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        await client.CancelTaskAsync(task.Id);

        Assert.Equal([A2AAuditAction.TaskCreated, A2AAuditAction.TaskCanceled], sink.Events.Select(e => e.Action));
        Assert.All(sink.Events, e =>
        {
            Assert.Equal(A2AAuditOutcome.Succeeded, e.Outcome);
            Assert.Equal(binding, e.Binding);
            Assert.Equal("alice", e.Actor);
            Assert.Equal(task.Id, e.TaskId);
        });
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_SendMessage_is_audited(string binding)
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, grpc: true);
        var client = host.Client(binding);

        string? taskId = null;
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("task")))
            if (evt is TaskEvent { Task: var t }) taskId = t.Id;

        Assert.NotNull(taskId);
        var e = Assert.Single(sink.Events);
        Assert.Equal(A2AAuditAction.TaskCreated, e.Action);
        Assert.Equal(A2AAuditOutcome.Succeeded, e.Outcome);
        Assert.Equal(binding, e.Binding);
        Assert.Equal(taskId, e.TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_over_a_stream_is_audited(string binding)
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink, grpc: true);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();

        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("more", taskId: task.Id))) { }

        Assert.Equal([A2AAuditAction.TaskCreated, A2AAuditAction.FollowUpSent], sink.Events.Select(e => e.Action));
        Assert.Equal(binding, sink.Events[1].Binding);
    }
}
