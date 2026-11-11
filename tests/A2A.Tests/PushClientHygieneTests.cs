using System.Text;
using A2A.Client;
using A2A.Client.Push;

namespace A2A.Tests;

/// <summary>
///     Spec §13.2 on the client: HTTPS webhook URLs, unique single-purpose tokens, token rotation, and webhook rate
///     limiting.
/// </summary>
public class PushClientHygieneTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly Func<PushDelivery, CancellationToken, Task> Ok = (_, _) => Task.CompletedTask;

    private static byte[] Body(string taskId = "t1") =>
        Encoding.UTF8.GetBytes("{\"statusUpdate\":{\"taskId\":\"" + taskId + "\",\"contextId\":\"c1\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}");

    private static Dictionary<string, string> Headers(string id, string token = "tok") =>
        new() { [A2AProtocol.Headers.NotificationToken] = token, [A2AProtocol.Headers.DeliveryId] = id };

    // ---- HTTPS webhook URLs ----

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task With_RequireHttpsWebhooks_a_plain_http_webhook_is_refused_before_sending(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding, options: new A2AClientOptions { RequireHttpsWebhooks = true });
        var task = (await client.SendMessageAsync("task")).RequireTask();

        var create = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(
            new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/a" }));
        var send = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("task"),
            Configuration = new SendMessageConfiguration
                { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://hooks.test/b" } }
        }));

        Assert.Equal(A2AErrorCode.InvalidParams, create.Code);
        Assert.Contains("config.url", create.Message, StringComparison.Ordinal);
        Assert.Equal(A2AErrorCode.InvalidParams, send.Code);
        Assert.Contains("configuration.taskPushNotificationConfig.url", send.Message, StringComparison.Ordinal);
        Assert.Empty(host.Webhooks.Calls); // nothing was registered
    }

    [Theory]
    [InlineData("https://hooks.test/a")]
    [InlineData("http://localhost:5000/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    public async Task With_RequireHttpsWebhooks_https_and_loopback_are_allowed(string url)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.HttpJson, options: new A2AClientOptions { RequireHttpsWebhooks = true });
        var task = (await client.SendMessageAsync("task")).RequireTask();

        var stored = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = url });

        Assert.Equal(url, stored.Url);
    }

    [Fact]
    public async Task By_default_a_plain_http_webhook_is_still_allowed()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.HttpJson);
        var task = (await client.SendMessageAsync("task")).RequireTask();

        var stored = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://hooks.test/a" });

        Assert.Equal("http://hooks.test/a", stored.Url);
    }

    // ---- unique tokens ----

    [Fact]
    public void Tokens_are_random_base64url_of_32_bytes()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => PushTokens.Create()).ToList();

        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public async Task RegisterPushAsync_gives_a_config_without_credentials_its_own_token()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.HttpJson);
        var first = (await client.SendMessageAsync("task")).RequireTask();
        var second = (await client.SendMessageAsync("task")).RequireTask();

        var a = await client.RegisterPushAsync(first.Id, new TaskPushNotificationConfig { Url = "https://hooks.test/a" });
        var b = await client.RegisterPushAsync(second.Id, new TaskPushNotificationConfig { Url = "https://hooks.test/a" });
        var mine = await client.RegisterPushAsync(first.Id, new TaskPushNotificationConfig { Url = "https://hooks.test/a", Token = "my-token" });
        var withAuth = await client.RegisterPushAsync(first.Id, new TaskPushNotificationConfig
        {
            Url = "https://hooks.test/a", Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "c" }
        });

        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Config.Token!);
        Assert.NotEqual(a.Config.Token, b.Config.Token); // one per config
        Assert.Equal("my-token", mine.Config.Token);
        Assert.Null(withAuth.Config.Token);
    }

    // ---- rotation ----

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task RotatePushTokenAsync_registers_the_new_token_then_removes_the_old_config(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        var old = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
            { TaskId = task.Id, Url = "https://hooks.test/a", Token = "old-token" });

        var rotated = await client.RotatePushTokenAsync(old);
        var chosen = await client.RotatePushTokenAsync(rotated, "chosen-token");

        Assert.NotEqual(old.Id, rotated.Id);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", rotated.Token!);
        Assert.Equal("https://hooks.test/a", rotated.Url);
        Assert.Equal("chosen-token", chosen.Token);
        var left = await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id });
        Assert.Equal(chosen.Id, Assert.Single(left.Configs).Id);
    }

    [Fact]
    public async Task RotatePushTokenAsync_needs_the_ids_of_the_registration()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.HttpJson);

        await Assert.ThrowsAsync<ArgumentException>(() => client.RotatePushTokenAsync(new TaskPushNotificationConfig { TaskId = "t1", Url = "https://hooks.test/a" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RotatePushTokenAsync(new TaskPushNotificationConfig { Id = "c1", Url = "https://hooks.test/a" }));
    }

    // ---- rate limiting ----

    [Fact]
    public async Task Over_the_limit_a_delivery_is_429_with_retry_after_and_the_next_window_accepts_it()
    {
        var clock = new ManualClock();
        var receiver = new PushNotificationReceiver(clock) { MaxDeliveriesPerMinute = 2 };
        var calls = 0;
        Task Count(PushDelivery _, CancellationToken __) { calls++; return Task.CompletedTask; }

        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(Headers("d1"), Body(), Count, "tok")).Outcome);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(Headers("d2"), Body(), Count, "tok")).Outcome);
        var limited = await receiver.HandleAsync(Headers("d3"), Body(), Count, "tok");

        Assert.Equal(PushHandleOutcome.RateLimited, limited.Outcome);
        Assert.Equal(429, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(40), limited.RetryAfter);
        Assert.Equal(2, calls);

        clock.Advance(TimeSpan.FromSeconds(40));
        var retry = await receiver.HandleAsync(Headers("d3"), Body(), Count, "tok");
        Assert.Equal(PushHandleOutcome.Handled, retry.Outcome); // the refused id was not remembered
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Unauthenticated_deliveries_do_not_use_up_the_budget()
    {
        var receiver = new PushNotificationReceiver(new ManualClock()) { MaxDeliveriesPerMinute = 1 };
        for (var i = 0; i < 5; i++)
            Assert.Equal(PushHandleOutcome.Unauthorized, (await receiver.HandleAsync(Headers("x" + i, "wrong"), Body(), Ok, "tok")).Outcome);

        Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(Headers("d1"), Body(), Ok, "tok")).Outcome);
    }

    [Fact]
    public async Task Without_a_limit_every_delivery_is_accepted()
    {
        var receiver = new PushNotificationReceiver(new ManualClock());
        for (var i = 0; i < 100; i++)
            Assert.Equal(PushHandleOutcome.Handled, (await receiver.HandleAsync(Headers("d" + i), Body(), Ok, "tok")).Outcome);
    }

    [Fact]
    public void A_limit_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PushNotificationReceiver { MaxDeliveriesPerMinute = 0 });
    }
}
