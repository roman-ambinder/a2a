using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.TestKit.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task Starts_with_an_executor_type()
    {
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>();
        var response = await host.CreateClient().SendMessageAsync(Ask.Text("hi"));
        var task = A2AAssert.HasState(response, TaskState.Completed);
        Assert.Equal("echo: hi", task.Status.Message!.GetText());
    }

    private sealed class CountingExecutor : IAgentExecutor
    {
        public int Runs;
        public int Cancels;

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            return updater.RequireInputAsync("more?", cancellationToken).AsTask();
        }

        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Cancels);
            return updater.CancelAsync("stopped by the instance", cancellationToken).AsTask();
        }
    }

    [Fact]
    public async Task Starts_with_an_executor_instance_and_keeps_its_cancel()
    {
        var executor = new CountingExecutor();
        await using var host = await A2ATestHost.StartAsync(executor);
        var client = host.CreateClient();
        var task = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("start")), TaskState.InputRequired);
        var canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = task.Id });
        A2AAssert.HasState(canceled, TaskState.Canceled);
        Assert.Equal(1, executor.Runs);
        Assert.Equal(1, executor.Cancels);
        Assert.Equal("stopped by the instance", canceled.Status.Message!.GetText());
    }

    [Fact]
    public async Task Starts_with_a_delegate()
    {
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
            await updater.ReplyAsync("delegate: " + context.UserText, ct));
        var message = A2AAssert.IsMessage(await host.CreateClient().SendMessageAsync(Ask.Text("x")));
        Assert.Equal("delegate: x", message.GetText());
    }

    [Fact]
    public async Task Starts_with_a_builder()
    {
        await using var host = await A2ATestHost.StartWithBuilderAsync(b => b.AddAgentExecutor((c, u, ct) => u.ReplyAsync("built", ct).AsTask()));
        Assert.Equal("built", A2AAssert.IsMessage(await host.CreateClient().SendMessageAsync(Ask.Text("x"))).GetText());
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Both_bindings_work_for_send_and_stream(string binding)
    {
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        });
        var client = host.CreateClient(binding);
        A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("a")), TaskState.Completed);
        var events = await Ask.Stream(client, "b").CollectAsync();
        A2AAssert.StateOrder(events, TaskState.Submitted, TaskState.Working, TaskState.Completed);
    }

    [Fact]
    public async Task A_binding_without_support_is_refused()
    {
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => host.CreateClient(ProtocolBindings.Grpc));
        Assert.Equal("binding", ex.ParamName);
    }

    [Fact]
    public async Task Callers_are_anonymous_or_the_given_user()
    {
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>();
        Assert.Equal(ServerCallContext.AnonymousPrincipal, A2AAssert.IsMessage(await host.CreateClient().SendMessageAsync(Ask.Text("who"))).GetText());
        Assert.Equal("alice", A2AAssert.IsMessage(await host.CreateClient(user: "alice").SendMessageAsync(Ask.Text("who"))).GetText());
        Assert.Equal("bob", A2AAssert.IsMessage(await host.CreateClient(ProtocolBindings.HttpJson, "bob").SendMessageAsync(Ask.Text("who"))).GetText());
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task RequireAuthentication_refuses_anonymous_callers_but_not_users_or_the_card(string binding)
    {
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>(o => o.RequireAuthentication = true);
        await A2AAssert.ThrowsA2AAsync(() => host.CreateClient(binding).SendMessageAsync(Ask.Text("who")), A2AErrorCode.Unauthenticated);
        Assert.Equal("carol", A2AAssert.IsMessage(await host.CreateClient(binding, "carol").SendMessageAsync(Ask.Text("who"))).GetText());
        var viaCard = await A2AClientFactory.CreateAsync(host.Url, host.CreateHttpClient());
        Assert.Equal(host.Card.Name, viaCard.AgentCard!.Name);
    }

    [Fact]
    public async Task ConfigureCard_changes_the_default_card()
    {
        var defaults = A2ATestHost.DefaultCard();
        Assert.True(defaults.Capabilities.Streaming);
        Assert.True(defaults.Capabilities.PushNotifications);
        Assert.Equal([ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson], defaults.SupportedInterfaces.Select(i => i.ProtocolBinding));
        Assert.All(defaults.SupportedInterfaces, i => Assert.Equal(A2ATestHost.DefaultBaseUrl, i.Url));

        await using var host = await A2ATestHost.StartAsync<EchoExecutor>(o => o.ConfigureCard = c => c.Name = "Renamed");
        Assert.Equal("Renamed", host.Card.Name);
        var client = await A2AClientFactory.CreateAsync(host.Url, host.CreateHttpClient());
        Assert.Equal("Renamed", client.AgentCard!.Name);
    }

    [Fact]
    public async Task Server_http_and_service_options_run_after_the_defaults()
    {
        var seenBackoff = TimeSpan.Zero;
        var servicesCalled = false;
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>(o =>
        {
            o.ConfigureServer = s =>
            {
                seenBackoff = s.PushDelivery.InitialBackoff;
                s.MaxTurnsPerTask = 3;
            };
            o.ConfigureHttp = h => h.AgentCardMaxAge = TimeSpan.FromSeconds(7);
            o.ConfigureServices = s => { servicesCalled = true; s.AddSingleton("marker"); };
        });
        Assert.Equal(TimeSpan.FromMilliseconds(10), seenBackoff);
        Assert.True(servicesCalled);
        Assert.Equal("marker", host.Services.GetRequiredService<string>());
        Assert.Equal(3, host.Services.GetRequiredService<A2AServerOptions>().MaxTurnsPerTask);
        Assert.NotNull(host.Handler);
        Assert.Equal(new Uri(A2ATestHost.DefaultBaseUrl), host.Url);
    }

    [Fact]
    public async Task Clock_is_null_unless_set_and_is_the_registered_time_provider()
    {
        await using var plain = await A2ATestHost.StartAsync<EchoExecutor>();
        Assert.Null(plain.Clock);

        var clock = new TestClock();
        await using var timed = await A2ATestHost.StartAsync<EchoExecutor>(o => o.TimeProvider = clock);
        Assert.Same(clock, timed.Clock);
        Assert.Same(clock, timed.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task WaitIdleAsync_waits_for_running_agents_and_times_out_with_a_clear_message()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("done", ct);
        });
        var client = host.CreateClient();
        var task = A2AAssert.IsTask(await client.SendMessageAsync(Ask.Text("go", returnImmediately: true)));

        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() => host.WaitIdleAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains("fake clock", ex.Message, StringComparison.Ordinal);

        gate.SetResult();
        await host.WaitIdleAsync();
        await A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Completed);
    }

    [Fact]
    public async Task WaitIdleAsync_also_waits_for_push_deliveries()
    {
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) => await updater.CompleteAsync("done", ct));
        var request = Ask.Text("go", returnImmediately: true);
        request.Configuration!.TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = TestPushReceiver.DefaultUrl };
        await host.CreateClient().SendMessageAsync(request);
        await host.WaitIdleAsync();
        Assert.NotEmpty(host.Push.Deliveries);
    }

    [Fact]
    public async Task WaitIdleAsync_reports_the_pending_push_count_when_a_delivery_cannot_finish()
    {
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) => await updater.CompleteAsync("done", ct));
        host.Push.Respond = _ => System.Net.HttpStatusCode.InternalServerError; // retried for ever
        var request = Ask.Text("go", returnImmediately: true);
        request.Configuration!.TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = TestPushReceiver.DefaultUrl };
        await host.CreateClient().SendMessageAsync(request);
        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() => host.WaitIdleAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Contains("push deliveries were still pending", ex.Message, StringComparison.Ordinal);
        Assert.Matches(@"^\d+ push deliveries", ex.Message);
    }

    [Fact]
    public async Task Dispose_can_be_called_twice()
    {
        var host = await A2ATestHost.StartAsync<EchoExecutor>();
        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task MaxTaskDuration_fires_through_a_test_clock()
    {
        var spy = new SpyClock(new TestClock());
        var limit = TimeSpan.FromMinutes(3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, o =>
        {
            o.TimeProvider = spy;
            o.ConfigureServer = s => s.MaxTaskDuration = limit;
        });
        var client = host.CreateClient();
        var task = A2AAssert.IsTask(await client.SendMessageAsync(Ask.Text("go", returnImmediately: true)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await spy.WhenTimerAsync(limit).WaitAsync(TimeSpan.FromSeconds(30));

        spy.Inner.Advance(limit + TimeSpan.FromSeconds(1));

        var failed = await A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Failed);
        Assert.Contains("time limit", failed.Status.Message!.GetText(), StringComparison.Ordinal);
    }
}
