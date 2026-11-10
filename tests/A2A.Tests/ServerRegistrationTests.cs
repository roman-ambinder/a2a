using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Tests;

public sealed class ServerRegistrationTests
{
    private sealed class Throwing : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("unrelated invoker must not be used for webhooks");
        }
    }

    [Fact]
    public void Plain_options_are_the_same_instance_as_IOptions_value()
    {
        var services = new ServiceCollection();
        services.AddA2AServer(o => o.CancelGracePeriod = TimeSpan.FromSeconds(7));
        using var sp = services.BuildServiceProvider();
        Assert.Same(sp.GetRequiredService<IOptions<A2AServerOptions>>().Value, sp.GetRequiredService<A2AServerOptions>());
        Assert.Equal(TimeSpan.FromSeconds(7), sp.GetRequiredService<A2AServerOptions>().CancelGracePeriod);
    }

    [Fact]
    public async Task Unrelated_HttpMessageInvoker_does_not_replace_the_push_transport()
    {
        var unrelated = new Throwing();
        var recording = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddSingleton(new HttpMessageInvoker(unrelated));
        services.AddA2AServer(o =>
        {
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
        });
        services.AddSingleton(new HttpMessageInvoker(unrelated)); // registered after, too
        services.AddKeyedSingleton(PushDeliveryService.InvokerServiceKey, new HttpMessageInvoker(recording));
        using var sp = services.BuildServiceProvider();

        var key = new TaskKey("", "alice", "t1");
        await sp.GetRequiredService<PushConfigStore>().SaveAsync(new PushConfigRecord(key,
            new TaskPushNotificationConfig { Id = "c1", TaskId = "t1", Url = "http://127.0.0.1/hook" }));
        var now = DateTimeOffset.UtcNow;
        var record = new TaskRecord("", "alice", new AgentTask { Id = "t1", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Submitted } }, 0, now, now);
        await sp.GetRequiredService<TaskStore>().CommitAsync(new TaskCommit(record, 0, StreamResponse.Of(record.Task.DeepCopy()),
            [new PushDelivery(key, "c1", 0, "d1")]));
        Assert.Equal(1, await sp.GetRequiredService<PushDeliveryService>().RunOnceAsync(CancellationToken.None));

        Assert.Single(recording.Calls);
        Assert.Equal(0, unrelated.Calls);
    }

    [Fact]
    public void AddA2AServer_does_not_register_an_unkeyed_HttpMessageInvoker()
    {
        var services = new ServiceCollection();
        services.AddA2AServer();
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<HttpMessageInvoker>());
        Assert.NotNull(sp.GetRequiredKeyedService<HttpMessageInvoker>(PushDeliveryService.InvokerServiceKey));
    }
}
