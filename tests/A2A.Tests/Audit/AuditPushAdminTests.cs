using System.Net;
using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Push;

namespace A2A.Tests.Audit;

public class AuditPushAdminTests
{
    [Fact]
    public async Task Redrive_and_listing_are_audited_with_the_actor()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.InternalServerError);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.MaxAttempts = 1;
        await rig.SaveConfigAsync(new TaskPushNotificationConfig { Url = "https://hooks.example/cb" });
        await rig.CommitAsync("c1");
        using var service = rig.NewService();
        await rig.DrainAsync(service);
        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10)).Delivery;
        var sink = new RecordingAuditSink();
        var admin = new A2APushAdmin(rig.Outbox, new A2AAuditor(sink, new A2AServerOptions()));

        Assert.Single(await admin.ListDeadLettersAsync("", 10, "ops-carol"));
        Assert.True(await admin.RedriveAsync(dead, "ops-carol"));
        Assert.False(await admin.RedriveAsync(dead, "ops-carol")); // no longer a dead letter
        await admin.ListDeadLettersAsync("", 10); // the old overload

        var e = sink.Events;
        Assert.Equal(4, e.Count);
        Assert.Equal((A2AAuditAction.AdminDeadLettersListed, A2AAuditOutcome.Succeeded, "ops-carol"), (e[0].Action, e[0].Outcome, e[0].Actor));
        Assert.Equal((A2AAuditAction.AdminDeadLetterRedriven, A2AAuditOutcome.Succeeded), (e[1].Action, e[1].Outcome));
        Assert.Equal(dead.DeliveryId, e[1].Resource);
        Assert.Equal(dead.Task.TaskId, e[1].TaskId);
        Assert.Equal(dead.Task.Tenant, e[1].Tenant);
        Assert.Equal((A2AAuditOutcome.Failed, "NotFound"), (e[2].Outcome, e[2].Reason));
        Assert.Equal(A2AAuditEvent.UnknownOperator, e[3].Actor);
    }

    private sealed class TickingSink(FakeTimeProvider clock) : RecordingAuditSink
    {
        public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (auditEvent.Outcome == A2AAuditOutcome.Attempted) clock.Advance(TimeSpan.FromSeconds(5)); // the action takes time
            return base.WriteAsync(auditEvent, cancellationToken);
        }
    }

    [Fact]
    public async Task Final_row_carries_the_time_of_the_outcome()
    {
        var rig = new PushRig(new ScriptedHooks(HttpStatusCode.OK));
        var clock = new FakeTimeProvider();
        var sink = new TickingSink(clock);
        var options = new A2AServerOptions();
        options.Audit.FailClosed = true;
        var admin = new A2APushAdmin(rig.Outbox, new A2AAuditor(sink, options, null, clock));

        await admin.ListDeadLettersAsync("", 10, "ops");

        Assert.Equal([A2AAuditOutcome.Attempted, A2AAuditOutcome.Succeeded], sink.Events.Select(e => e.Outcome));
        Assert.Equal(sink.Events[0].Id, sink.Events[1].Id);
        Assert.Equal(TimeSpan.FromSeconds(5), sink.Events[1].Time - sink.Events[0].Time);
    }
}
