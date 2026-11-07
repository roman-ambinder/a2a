using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace A2A.Tests;

public class ClientDefaultsTests
{
    private const string Endpoint = "http://localhost/a2a";
    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";

    private sealed record Call(HttpRequestMessage Request, string Body);

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script) : HttpMessageHandler
    {
        public ConcurrentQueue<Call> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Enqueue(new Call(request, body));
            var step = script[Math.Min(Calls.Count, script.Length) - 1];
            return await step(request, cancellationToken);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body = "", string? retryAfter = null) =>
        (_, _) =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(r);
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RpcOk(string resultJson) =>
        Respond(HttpStatusCode.OK, $$"""{"jsonrpc":"2.0","id":"x","result":{{resultJson}}}""");

    private static string SendResult => $$"""{"task":{{TaskJson}}}""";

    /// <summary>Records every delay the client asks for and completes it at once.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer();
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Enqueue(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return timer;
        }

        private sealed class FakeTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static A2AClient NewClient(ScriptedHandler handler, A2AClientOptions? options = null, string binding = ProtocolBindings.JsonRpc)
    {
        options ??= new A2AClientOptions();
        var http = new HttpClient(handler);
        IA2AClient transport = binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri(Endpoint), options)
            : new HttpJsonTransport(http, new Uri(Endpoint), options);
        return new A2AClient(transport, options);
    }

    private static string Header(Call call, string name) => call.Request.Headers.GetValues(name).Single();

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Send_sets_message_id_and_idempotency_header(string binding)
    {
        var body = binding == ProtocolBindings.JsonRpc ? $$"""{"jsonrpc":"2.0","id":"x","result":{{SendResult}}}""" : SendResult;
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK, body));
        var client = NewClient(handler, binding: binding);
        var message = new Message { MessageId = "", Role = Role.User, Parts = [Part.FromText("hi")] };

        await client.SendMessageAsync(message);

        var call = Assert.Single(handler.Calls);
        var key = Header(call, A2AProtocol.Headers.IdempotencyKey);
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, message.MessageId);
        Assert.Contains($"\"messageId\":\"{key}\"", call.Body);
    }

    [Fact]
    public async Task Explicit_idempotency_key_wins()
    {
        var handler = new ScriptedHandler(RpcOk(SendResult));
        var client = NewClient(handler);

        await client.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") }, new A2ARequestOptions { IdempotencyKey = "my-key" });

        Assert.Equal("my-key", Header(Assert.Single(handler.Calls), A2AProtocol.Headers.IdempotencyKey));
    }

    [Fact]
    public async Task Retries_send_on_503_with_same_key()
    {
        var time = new FakeTimeProvider();
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable), RpcOk(SendResult));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = time });

        await client.SendMessageAsync("hi");

        var calls = handler.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(Header(calls[0], A2AProtocol.Headers.IdempotencyKey), Header(calls[1], A2AProtocol.Headers.IdempotencyKey));
        Assert.Equal(JsonDocument.Parse(calls[0].Body).RootElement.GetProperty("params").GetRawText(),
            JsonDocument.Parse(calls[1].Body).RootElement.GetProperty("params").GetRawText());
        var delay = Assert.Single(time.Delays);
        Assert.InRange(delay.TotalMilliseconds, 160, 240);
    }

    [Fact]
    public async Task Gives_up_after_three_attempts()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));

        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task Does_not_retry_create_push_config_on_503()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable), RpcOk("{}"));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        await Assert.ThrowsAsync<A2AException>(() =>
            client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = "t1", Url = "https://hook.example/x" }));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Does_not_retry_non_retryable_error()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK,
            """{"jsonrpc":"2.0","id":"x","error":{"code":-32001,"message":"nope"}}"""));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Retries_on_transport_failure()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("boom"), RpcOk(TaskJson));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        var task = await client.GetTaskAsync("t1");

        Assert.Equal("t1", task.Id);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Honours_retry_after(string binding)
    {
        var time = new FakeTimeProvider();
        var ok = binding == ProtocolBindings.JsonRpc ? RpcOk(TaskJson) : Respond(HttpStatusCode.OK, TaskJson);
        var handler = new ScriptedHandler(Respond((HttpStatusCode)429, retryAfter: "2"), ok);
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = time }, binding);

        await client.GetTaskAsync("t1");

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task Per_call_headers_are_sent_after_sdk_headers_and_configure_request_runs_last()
    {
        var handler = new ScriptedHandler(RpcOk(TaskJson));
        var client = NewClient(handler, new A2AClientOptions { Extensions = { "urn:a" } });
        var options = new A2ARequestOptions
        {
            Extensions = ["urn:b"],
            ConfigureRequest = (req, _) =>
            {
                req.Headers.TryAddWithoutValidation("X-Configured", req.Headers.Contains("X-Custom") ? "after" : "before");
                return ValueTask.CompletedTask;
            },
        };
        options.Headers["X-Custom"] = "v1";

        await client.GetTaskAsync(new GetTaskRequest { Id = "t1" }, options);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("v1", Header(call, "X-Custom"));
        Assert.Equal("after", Header(call, "X-Configured"));
        Assert.Equal(A2AProtocol.Version, Header(call, A2AProtocol.Headers.Version));
        Assert.Equal("urn:a,urn:b", Header(call, A2AProtocol.Headers.Extensions));
    }

    [Fact]
    public async Task Unary_timeout_applies()
    {
        var handler = new ScriptedHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var client = NewClient(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { Timeout = TimeSpan.FromMilliseconds(50) }));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried_or_reported_as_timeout()
    {
        var handler = new ScriptedHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var client = NewClient(handler);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTaskAsync(new GetTaskRequest { Id = "t1" }, null, cts.Token));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Stream_has_no_timeout_and_is_not_retried()
    {
        var sse = (HttpRequestMessage _, CancellationToken ct) =>
        {
            var content = new StringContent("data: " + $$"""{"jsonrpc":"2.0","id":"x","result":{"task":{{TaskJson}} } }""" + "\n\n", Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        var handler = new ScriptedHandler(async (r, ct) => { await Task.Delay(200, ct); return await sse(r, ct); });
        var client = NewClient(handler);

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") },
                           new A2ARequestOptions { Timeout = TimeSpan.FromMilliseconds(50) }))
            events.Add(e);

        Assert.Single(events);
        Assert.Single(handler.Calls);

        var failing = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));
        var failingClient = NewClient(failing, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });
        await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in failingClient.SendStreamingMessageAsync(Message.FromUser("hi"))) { }
        });
        Assert.Single(failing.Calls);
    }

    [Fact]
    public void Default_handler_has_no_cookies_and_no_redirects()
    {
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => o.AgentUrl = new Uri("https://agent.example/"));
        using var provider = services.BuildServiceProvider();
        var name = "A2A:agent";

        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = factory.CreateHandler(name);
        while (handler is DelegatingHandler d) handler = d.InnerHandler!;

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.Equal(Timeout.InfiniteTimeSpan, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).Timeout);
    }
}
