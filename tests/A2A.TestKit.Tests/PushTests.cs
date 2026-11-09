using System.Net;
using A2A.Client;

namespace A2A.TestKit.Tests;

public sealed class PushTests
{
    private static async Task<(A2ATestHost Host, A2AClient Client)> HostWithWebhookAsync(Func<A2A.Server.AgentRequestContext, A2A.Server.TaskUpdater, CancellationToken, Task> agent,
        string? token = null, Action<A2ATestHostOptions>? configure = null)
    {
        var host = await A2ATestHost.StartAsync(agent, configure);
        return (host, host.CreateClient());
    }

    private static SendMessageRequest WithWebhook(string text, string? token = null)
    {
        var request = Ask.Text(text, returnImmediately: true);
        request.Configuration!.TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = TestPushReceiver.DefaultUrl, Token = token };
        return request;
    }

    [Fact]
    public async Task A_registered_webhook_gets_the_events_with_parsed_headers_and_body()
    {
        var (host, client) = await HostWithWebhookAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.CompleteAsync("all done", ct);
        });
        await using var _ = host;

        var task = A2AAssert.IsTask(await client.SendMessageAsync(WithWebhook("go", "secret-token")));
        var completed = await host.Push.WaitForAsync(d => d.Event is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });

        Assert.Equal(new Uri(TestPushReceiver.DefaultUrl), completed.Url);
        Assert.Equal(HttpMethod.Post, completed.Method);
        Assert.Equal("secret-token", completed.Token);
        Assert.False(string.IsNullOrEmpty(completed.DeliveryId));
        Assert.NotNull(completed.Sequence);
        Assert.Equal(completed.DeliveryId, completed.Headers["x-a2a-delivery-id"]); // names are case-insensitive
        Assert.Contains("json", completed.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);
        var status = Assert.IsType<StatusUpdateEvent>(completed.Event);
        Assert.Equal(task.Id, status.StatusUpdate.TaskId);
        Assert.Equal("all done", status.StatusUpdate.Status.Message!.GetText());
        Assert.Contains("\"taskId\"", completed.Body, StringComparison.Ordinal);

        await host.WaitIdleAsync();
        var sequences = host.Push.Deliveries.Select(d => d.Sequence!.Value).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Equal(host.Push.Deliveries.Count, host.Push.Events.Count);
    }

    [Fact]
    public async Task WaitForAsync_finds_deliveries_that_arrived_before_and_after_the_call()
    {
        var (host, client) = await HostWithWebhookAsync(async (context, updater, ct) => await updater.CompleteAsync("done", ct));
        await using var _ = host;
        await client.SendMessageAsync(WithWebhook("go"));
        await host.WaitIdleAsync();

        var past = await host.Push.WaitForAsync();
        Assert.NotNull(past.Event);

        host.Push.Clear();
        Assert.Empty(host.Push.Deliveries);
        var future = host.Push.WaitForAsync();
        Assert.False(future.IsCompleted);
        await client.SendMessageAsync(WithWebhook("again"));
        Assert.NotNull((await future).Event);
    }

    [Fact]
    public async Task A_failing_webhook_is_retried_with_the_same_delivery_id()
    {
        var (host, client) = await HostWithWebhookAsync(async (context, updater, ct) => await updater.CompleteAsync("done", ct));
        await using var _ = host;
        var calls = 0;
        host.Push.Respond = _ => Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        await client.SendMessageAsync(WithWebhook("go"));
        await host.Push.WaitForCountAsync(2);
        await host.WaitIdleAsync();

        // The task sends more than one event, so only the delivery that got the 500 is seen twice.
        var retried = Assert.Single(host.Push.Deliveries.GroupBy(d => d.DeliveryId), g => g.Count() > 1);
        Assert.Equal(2, retried.Count());
        Assert.Single(retried.Select(d => d.Sequence).Distinct());
    }

    [Fact]
    public async Task A_retry_that_waits_on_a_fake_clock_needs_Advance()
    {
        var spy = new SpyClock(new TestClock());
        var (host, client) = await HostWithWebhookAsync(async (context, updater, ct) => await updater.CompleteAsync("done", ct),
            configure: o =>
            {
                o.TimeProvider = spy;
                o.ConfigureServer = s => { s.PushDelivery.InitialBackoff = TimeSpan.FromMinutes(5); s.PushDelivery.MaxBackoff = TimeSpan.FromMinutes(5); };
            });
        await using var _ = host;
        var calls = 0;
        host.Push.Respond = _ => Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;

        await client.SendMessageAsync(WithWebhook("go"));
        await host.Push.WaitForAsync(); // the first attempt got a 503
        // Without advancing, the retry does not come.
        var early = await Assert.ThrowsAsync<A2AAssertionException>(() => host.Push.WaitForCountAsync(2, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("1 arrived", early.Message, StringComparison.Ordinal);
        var idle = await Assert.ThrowsAsync<A2AAssertionException>(() => host.WaitIdleAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains("advance the clock", idle.Message, StringComparison.Ordinal);

        // The delivery loop wakes on its poll interval; advance until the retry is due and sent.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (host.Push.Deliveries.Count < 2)
        {
            spy.Inner.Advance(TimeSpan.FromMinutes(6));
            await Task.Delay(5, cts.Token);
        }
        await host.Push.WaitForCountAsync(2);
    }

    [Fact]
    public async Task WaitForCountAsync_times_out_with_a_message_that_lists_what_arrived()
    {
        var receiver = new TestPushReceiver();
        var empty = await Assert.ThrowsAsync<A2AAssertionException>(() => receiver.WaitForCountAsync(2, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("Expected 2 push deliveries", empty.Message, StringComparison.Ordinal);
        Assert.Contains("0 arrived", empty.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing arrived", empty.Message, StringComparison.Ordinal);

        using var invoker = receiver.CreateInvoker();
        await Send(invoker, StreamResponseJson(TaskState.Working), 1);
        var one = await Assert.ThrowsAsync<A2AAssertionException>(() => receiver.WaitForCountAsync(2, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("1 arrived", one.Message, StringComparison.Ordinal);
        Assert.Contains("#1 Status(WORKING)", one.Message, StringComparison.Ordinal);

        var none = await Assert.ThrowsAsync<A2AAssertionException>(() => receiver.WaitForAsync(d => d.Sequence == 99, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("No push delivery matched", none.Message, StringComparison.Ordinal);
        Assert.Contains("#1 Status(WORKING)", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_an_event_does_not_break_the_receiver()
    {
        var receiver = new TestPushReceiver();
        using var invoker = receiver.CreateInvoker();
        var junk = await Send(invoker, "this is not json", 1);
        Assert.Equal(HttpStatusCode.OK, junk.StatusCode);
        await Send(invoker, StreamResponseJson(TaskState.Completed), 2);

        var bad = Assert.Single(receiver.Deliveries, d => d.Body == "this is not json");
        var ex = Assert.Throws<A2AAssertionException>(() => bad.Event);
        Assert.Contains("this is not json", ex.Message, StringComparison.Ordinal);
        Assert.Same(ex, Assert.Throws<A2AAssertionException>(() => bad.Event)); // parsed once
        Assert.Throws<A2AAssertionException>(() => receiver.Events);
        Assert.Contains("(not a StreamResponse)", bad.ToString(), StringComparison.Ordinal);

        var good = await receiver.WaitForAsync(d => d.Sequence == 2);
        Assert.IsType<StatusUpdateEvent>(good.Event);
        Assert.Equal(2, receiver.Deliveries.Count);
    }

    [Fact]
    public async Task Respond_sets_the_status_and_headers_join_multiple_values()
    {
        var receiver = new TestPushReceiver { Respond = d => d.Sequence == 1 ? HttpStatusCode.Gone : HttpStatusCode.Accepted };
        using var invoker = receiver.CreateInvoker();
        var first = await Send(invoker, StreamResponseJson(TaskState.Working), 1, extra: ("X-Multi", ["a", "b"]));
        var second = await Send(invoker, StreamResponseJson(TaskState.Working), 2);
        Assert.Equal(HttpStatusCode.Gone, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("a, b", receiver.Deliveries[0].Headers["x-multi"]);
        Assert.Null(receiver.Deliveries[1].Token);
        Assert.Null(receiver.Deliveries[1].DeliveryId);

        receiver.Clear();
        Assert.Empty(receiver.Deliveries);
    }

    [Fact]
    public async Task Disposing_the_invoker_leaves_the_receiver_usable()
    {
        var receiver = new TestPushReceiver();
        receiver.CreateInvoker().Dispose();
        using var invoker = receiver.CreateInvoker();
        await Send(invoker, StreamResponseJson(TaskState.Working), 1);
        Assert.Single(receiver.Deliveries);
    }

    private static string StreamResponseJson(TaskState state) =>
        A2A.Json.A2AJson.Serialize(StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = state } }));

    private static async Task<HttpResponseMessage> Send(HttpMessageInvoker invoker, string body, long sequence, (string, string[])? extra = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TestPushReceiver.DefaultUrl) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(A2AProtocol.Headers.NotificationSequence, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (extra is { } e) request.Headers.Add(e.Item1, e.Item2);
        return await invoker.SendAsync(request, CancellationToken.None);
    }
}
