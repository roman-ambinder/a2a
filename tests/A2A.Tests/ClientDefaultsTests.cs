using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using Microsoft.Extensions.DependencyInjection;

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
        IA2ATransport transport = binding == ProtocolBindings.JsonRpc
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
    public async Task Request_tenant_wins_over_the_options_tenant()
    {
        var handler = new ScriptedHandler(RpcOk(SendResult), RpcOk(SendResult));
        var client = NewClient(handler, new A2AClientOptions { Tenant = "from-options" });

        await client.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("a"), Tenant = "from-request" });
        await client.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("b") });

        var calls = handler.Calls.ToArray();
        Assert.Contains("\"tenant\":\"from-request\"", calls[0].Body);
        Assert.Contains("\"tenant\":\"from-options\"", calls[1].Body);
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

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task Does_not_retry_generic_http_errors(int status)
    {
        foreach (var binding in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
        {
            var handler = new ScriptedHandler(Respond((HttpStatusCode)status, "oops"), RpcOk(TaskJson));
            var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() }, binding);
            var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));
            Assert.Equal(status, ex.HttpStatus);
            Assert.Single(handler.Calls);
        }
    }

    private static string ErrorWithRetryable(string? retryable)
    {
        var meta = retryable is null ? "{}" : "{\"retryable\":\"" + retryable + "\"}";
        var info = $$$"""{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"INTERNAL","domain":"{{{A2AProtocol.ErrorDomain}}}","metadata":{{{meta}}}}""";
        return $$$"""{"jsonrpc":"2.0","id":"x","error":{"code":-32603,"message":"e","data":[{{{info}}}]}}""";
    }

    [Theory]
    [InlineData("true", 2)]
    [InlineData("false", 1)]
    [InlineData(null, 1)]
    public async Task Retries_server_error_only_with_explicit_retryable_true(string? retryable, int expectedRequests)
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.OK, ErrorWithRetryable(retryable)), RpcOk(TaskJson));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });
        try { await client.GetTaskAsync("t1"); } catch (A2AException) { }
        Assert.Equal(expectedRequests, handler.Calls.Count);
    }

    [Fact]
    public async Task Per_call_content_header_is_sent()
    {
        var handler = new ScriptedHandler(RpcOk(TaskJson));
        var client = NewClient(handler);
        var options = new A2ARequestOptions();
        options.Headers["Content-Language"] = "en";

        await client.GetTaskAsync(new GetTaskRequest { Id = "t1" }, options);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("en", call.Request.Content!.Headers.ContentLanguage.Single());
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

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> SendOk(string binding) =>
        binding == ProtocolBindings.JsonRpc ? RpcOk(SendResult) : Respond(HttpStatusCode.OK, SendResult);

    /// <summary>A response whose body breaks after the first bytes: the server got the request and started to answer.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResetMidResponse() =>
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream()) });

    private sealed class BreakingStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent) throw new IOException("Connection reset by peer.");
            _sent = true;
            buffer[offset] = (byte)'{';
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Send_is_not_retried_after_a_mid_response_reset(string binding)
    {
        var handler = new ScriptedHandler(ResetMidResponse(), SendOk(binding));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() }, binding);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendMessageAsync("hi"));

        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Send_is_not_retried_when_the_response_ended_early(string binding)
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), SendOk(binding));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() }, binding);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendMessageAsync("hi"));

        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc, HttpRequestError.ConnectionError)]
    [InlineData(ProtocolBindings.HttpJson, HttpRequestError.ConnectionError)]
    [InlineData(ProtocolBindings.JsonRpc, HttpRequestError.NameResolutionError)]
    [InlineData(ProtocolBindings.HttpJson, HttpRequestError.SecureConnectionError)]
    [InlineData(ProtocolBindings.JsonRpc, HttpRequestError.ProxyTunnelError)]
    public async Task Send_is_retried_when_the_request_never_left(string binding, HttpRequestError error)
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException(error, "not sent"), SendOk(binding));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() }, binding);

        await client.SendMessageAsync("hi");

        var calls = handler.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(Header(calls[0], A2AProtocol.Headers.IdempotencyKey), Header(calls[1], A2AProtocol.Headers.IdempotencyKey));
    }

    [Fact]
    public async Task Read_operations_still_retry_any_transport_failure()
    {
        var handler = new ScriptedHandler(ResetMidResponse(), RpcOk(TaskJson));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        await client.GetTaskAsync("t1");

        Assert.Equal(2, handler.Calls.Count);
    }

    /// <summary>A real socket that sends response headers and part of the body, then closes the connection.</summary>
    [Fact]
    public async Task Send_over_a_real_socket_is_not_retried_after_the_response_started()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                Interlocked.Increment(ref accepted);
                using (socket)
                {
                    var stream = socket.GetStream();
                    var buffer = new byte[8192];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n")) head.Append(Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer)));
                    var partial = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{\"jsonrpc\":";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(partial));
                    await stream.FlushAsync();
                    socket.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0); // RST on close
                }
            }
        });
        var options = new A2AClientOptions { TimeProvider = new FakeTimeProvider() };
        var client = new A2AClient(new JsonRpcTransport(new HttpClient(), new Uri($"http://127.0.0.1:{port}/a2a"), options), options);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendMessageAsync("hi"));

        stop.Cancel();
        listener.Stop();
        await server;
        Assert.Equal(1, accepted);
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A refused connection is a ConnectionError: nothing reached a server, so SendMessage is retried.</summary>
    [Fact]
    public async Task Send_over_a_real_socket_is_retried_when_the_connection_is_refused()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var counter = new CountingHandler(new SocketsHttpHandler());
        var options = new A2AClientOptions { TimeProvider = new FakeTimeProvider() };
        var client = new A2AClient(new JsonRpcTransport(new HttpClient(counter), new Uri($"http://127.0.0.1:{port}/a2a"), options), options);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendMessageAsync("hi"));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.Equal(3, counter.Count);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Max_retries_zero_disables_retry(string binding)
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable), SendOk(binding));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider(), MaxRetries = 0 }, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hi"));

        Assert.Equal(503, ex.HttpStatus);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Per_call_max_retries_overrides_the_client_default()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));
        var client = NewClient(handler, new A2AClientOptions { TimeProvider = new FakeTimeProvider() });

        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { MaxRetries = 0 }));
        Assert.Single(handler.Calls);

        var five = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable));
        var fiveClient = NewClient(five, new A2AClientOptions { TimeProvider = new FakeTimeProvider(), MaxRetries = 0 });
        await Assert.ThrowsAsync<A2AException>(() => fiveClient.GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { MaxRetries = 4 }));
        Assert.Equal(5, five.Calls.Count);
    }

    [Fact]
    public void Negative_max_retries_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new A2AClientOptions { MaxRetries = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new A2ARequestOptions { MaxRetries = -1 });
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
            var content = new StringContent("data: " + $$"""{"jsonrpc":"2.0","id":"x","result":{"task":{{TaskJson.Replace("WORKING", "COMPLETED")}} } }""" + "\n\n", Encoding.UTF8, "text/event-stream");
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

    [Fact]
    public void Registered_client_buffers_at_most_16_MiB_by_default_and_the_limit_is_configurable()
    {
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o => o.AgentUrl = new Uri("https://agent.example/"));
        services.AddA2AClient("small", o =>
        {
            o.AgentUrl = new Uri("https://agent.example/");
            o.MaxResponseContentBufferSize = 4096;
        });
        services.AddA2AClient("invalid", o =>
        {
            o.AgentUrl = new Uri("https://agent.example/");
            o.MaxResponseContentBufferSize = 0;
        });
        using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(16L * 1024 * 1024, A2ANamedClientOptions.DefaultMaxResponseContentBufferSize);
        Assert.Equal(A2ANamedClientOptions.DefaultMaxResponseContentBufferSize, http.CreateClient("A2A:agent").MaxResponseContentBufferSize);
        Assert.Equal(4096, http.CreateClient("A2A:small").MaxResponseContentBufferSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => http.CreateClient("A2A:invalid"));
    }

    [Fact]
    public async Task Registered_client_refuses_a_response_body_over_its_buffer_limit()
    {
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o =>
        {
            o.AgentUrl = new Uri("https://agent.example/");
            o.MaxResponseContentBufferSize = 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"" + new string('x', 4096) + "\"}", Encoding.UTF8, "application/json"),
        })));
        using var provider = services.BuildServiceProvider();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetRequiredService<IA2AClientFactory>().GetClientAsync("agent").AsTask());
        Assert.Contains("buffer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string LocalCardJson = JsonSerializer.Serialize(new AgentCard
    {
        Name = "a", Description = "d", Version = "1",
        SupportedInterfaces = [new AgentInterface { Url = Endpoint, ProtocolBinding = ProtocolBindings.JsonRpc, ProtocolVersion = "1.0" }],
        Capabilities = new(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
    }, A2A.Json.A2AJson.Options);

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    private static ServiceProvider LocalAgent(ScriptedHandler handler, TimeSpan? cardTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddA2AClient("agent", o =>
        {
            o.AgentUrl = new Uri("http://localhost/");
            if (cardTimeout is { } t) o.Resolver.Timeout = t;
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Registered_client_retries_the_card_after_a_card_fetch_timeout()
    {
        var handler = new ScriptedHandler(Hang, Respond(HttpStatusCode.OK, LocalCardJson));
        using var provider = LocalAgent(handler, TimeSpan.FromMilliseconds(50));
        var factory = provider.GetRequiredService<IA2AClientFactory>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.GetClientAsync("agent").AsTask());
        var client = await factory.GetClientAsync("agent");

        Assert.NotNull(client);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Same(client, await factory.GetClientAsync("agent"));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Registered_client_keeps_a_running_creation_when_only_the_caller_cancels()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = Respond(HttpStatusCode.OK, LocalCardJson);
        var handler = new ScriptedHandler(async (r, ct) => { await gate.Task; return await card(r, ct); });
        using var provider = LocalAgent(handler);
        var factory = provider.GetRequiredService<IA2AClientFactory>();

        using var cts = new CancellationTokenSource();
        var first = factory.GetClientAsync("agent", cts.Token).AsTask();
        while (handler.Calls.IsEmpty) await Task.Delay(5);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = factory.GetClientAsync("agent").AsTask();
        gate.SetResult();

        Assert.NotNull(await second);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Card_fetch_timeout_is_a_timeout_not_a_bare_cancellation()
    {
        var handler = new ScriptedHandler(Hang);
        var resolver = new A2ACardResolver(new HttpClient(handler), new A2ACardResolverOptions { Timeout = TimeSpan.FromMilliseconds(50) });

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => resolver.ResolveAsync(new Uri("http://localhost/")));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("timed out", error.Message);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var resolver2 = new A2ACardResolver(new HttpClient(new ScriptedHandler(Hang)), new A2ACardResolverOptions { Timeout = TimeSpan.FromMinutes(1) });
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver2.ResolveAsync(new Uri("http://localhost/"), cts.Token));
        Assert.IsNotType<TimeoutException>(cancelled.InnerException);
    }
}
