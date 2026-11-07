using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
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
        services.AddKeyedSingleton(HttpPushNotificationSender.InvokerServiceKey, new HttpMessageInvoker(recording));
        using var sp = services.BuildServiceProvider();

        var key = new TaskKey("", "alice", "t1");
        await sp.GetRequiredService<PushConfigStore>().SaveAsync(new PushConfigRecord(key,
            new TaskPushNotificationConfig { Id = "c1", TaskId = "t1", Url = "http://127.0.0.1/hook" }));
        var sender = sp.GetRequiredService<HttpPushNotificationSender>();
        sender.Enqueue(key, new SequencedEvent(1, StreamResponse.Of(Message.FromUser("x"))));
        await sender.WhenIdleAsync();

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
        Assert.NotNull(sp.GetRequiredKeyedService<HttpMessageInvoker>(HttpPushNotificationSender.InvokerServiceKey));
    }

    private class CustomSender : IPushNotificationSender
    {
        public void Enqueue(TaskKey task, SequencedEvent evt) { }
    }

    private sealed class ListeningSender : CustomSender, IPushConfigChangeListener
    {
        public List<TaskKey> Changed { get; } = [];
        public void ConfigChanged(TaskKey key) => Changed.Add(key);
    }

    private static TaskPushNotificationConfig Hook() => new() { Id = "c1", TaskId = "t1", Url = "https://hooks.example/cb" };

    [Fact]
    public async Task Custom_sender_that_listens_is_told_about_config_changes()
    {
        var custom = new ListeningSender();
        var services = new ServiceCollection();
        services.AddSingleton<IPushNotificationSender>(custom);
        services.AddA2AServer(o => o.PushNotifications.AllowPrivateNetworks = true);
        using var sp = services.BuildServiceProvider();

        var key = new TaskKey("", "alice", "t1");
        await sp.GetRequiredService<PushConfigService>().CreateAsync(key, Hook(), default);

        Assert.Same(custom, sp.GetRequiredService<IPushConfigChangeListener>());
        Assert.Equal([key], custom.Changed);
    }

    [Fact]
    public async Task Custom_sender_that_does_not_listen_never_builds_the_http_sender()
    {
        var invokers = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IPushNotificationSender>(new CustomSender());
        services.AddKeyedSingleton(HttpPushNotificationSender.InvokerServiceKey, (_, _) =>
        {
            Interlocked.Increment(ref invokers); // built only when HttpPushNotificationSender is constructed
            return new HttpMessageInvoker(new Throwing());
        });
        services.AddA2AServer(o => o.PushNotifications.AllowPrivateNetworks = true);
        using var sp = services.BuildServiceProvider();

        var listener = sp.GetRequiredService<IPushConfigChangeListener>();
        await sp.GetRequiredService<PushConfigService>().CreateAsync(new TaskKey("", "alice", "t1"), Hook(), default);

        Assert.IsNotType<HttpPushNotificationSender>(listener);
        Assert.Equal(0, invokers);
    }
}
