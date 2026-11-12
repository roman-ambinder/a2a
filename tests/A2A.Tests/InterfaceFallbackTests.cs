using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Tests.Credentials;

namespace A2A.Tests;

/// <summary>
///     Spec §5.2 fallback: with <see cref="A2AClientOptions.InterfaceFallback" /> the client moves to the next interface of
///     the card only when a call provably never reached the agent.
/// </summary>
public sealed class InterfaceFallbackTests
{
    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"}}""";

    private static HttpRequestException Unsent() => new(HttpRequestError.ConnectionError, "connection refused");

    private static AgentTask Done(string id, string contextId) =>
        new() { Id = id, ContextId = contextId, Status = new AgentTaskStatus { State = TaskState.Completed } };

    /// <summary>A transport that answers from a script: each call takes the next failure (null: answer).</summary>
    private class FakeTransport(string name) : IA2AClient, ITransportBinding
    {
        private readonly ConcurrentQueue<Exception?> _failures = new();
        public string Name { get; } = name;
        public string Binding => Name;
        public int Calls;
        public A2AClientOptions? Options { get; set; }

        /// <summary>For streams: the failure after the first event (null: the stream ends after it).</summary>
        public Exception? FailAfterFirstEvent { get; set; }

        public FakeTransport Fail(params Exception?[] failures)
        {
            foreach (var f in failures) _failures.Enqueue(f);
            return this;
        }

        public FakeTransport FailAlways(Func<Exception> failure)
        {
            _always = failure;
            return this;
        }

        private Func<Exception>? _always;

        protected void Step()
        {
            Interlocked.Increment(ref Calls);
            if (_always is not null) throw _always();
            if (_failures.TryDequeue(out var f) && f is not null) throw f;
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(Done(request.Id, Name));
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult<SendMessageResponse>(new TaskResponse(Done("t1", Name)));
        }

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Stream(cancellationToken);

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Stream(cancellationToken);

        private async IAsyncEnumerable<StreamResponse> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Step();
            if (FailAfterFirstEvent is { } after)
            {
                yield return new TaskEvent(new AgentTask { Id = "t1", ContextId = Name, Status = new AgentTaskStatus { State = TaskState.Working } });
                throw after;
            }

            yield return new TaskEvent(Done("t1", Name));
        }

        // The other operations answer with something that names this transport, so a test sees which one answered.
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(new ListTasksResponse { Tasks = [Done("t1", Name)], NextPageToken = "", PageSize = 1, TotalSize = 1 });
        }

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(Done(request.Id, Name));
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(Config(Name));
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(Config(Name));
        }

        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(new ListTaskPushNotificationConfigsResponse { Configs = [Config(Name)] });
        }

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.CompletedTask;
        }

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            var card = Card(Itf(Name));
            card.Description = Name;
            return Task.FromResult(card);
        }

        private static TaskPushNotificationConfig Config(string id) =>
            new() { Id = id, TaskId = "t1", Url = "https://hooks.example.com/" + id };
    }

    /// <summary>A fake that can also call custom operations; the result names it.</summary>
    private sealed class ExtensionTransport(string name) : FakeTransport(name), IA2AExtensionOperationClient
    {
        public Task<System.Text.Json.JsonElement> InvokeExtensionOperationAsync(string method, System.Text.Json.JsonElement parameters,
            A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Step();
            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(Name));
        }
    }

#pragma warning disable A2AEXP005
    /// <summary>A fake with a live exchange: opening it takes the next scripted failure, or throws <see cref="Opened"/>.</summary>
    private sealed class LiveTransport(string name) : FakeTransport(name), ILiveTransport
    {
        public sealed class Opened(string by) : Exception("opened by " + by)
        {
            public string By { get; } = by;
        }

        public Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options, CancellationToken cancellationToken)
        {
            Step();
            throw new Opened(Name);
        }
    }
#pragma warning restore A2AEXP005

    private sealed class FakeFactory(FakeTransport transport) : IA2ATransportFactory
    {
        public int Created;
        public int CreateAttempts;

        /// <summary>How many more times <see cref="Create"/> throws before it works.</summary>
        public int FailCreates;

        public string ProtocolBinding => transport.Name;
        public bool CanCreate(AgentInterface agentInterface) => true;

        public IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options)
        {
            Interlocked.Increment(ref CreateAttempts);
            if (FailCreates > 0)
            {
                FailCreates--;
                throw new InvalidOperationException("the transport could not be created");
            }

            Interlocked.Increment(ref Created);
            transport.Options = options;
            return transport;
        }
    }

    private static AgentCard Card(params AgentInterface[] interfaces) => new()
    {
        Name = "agent", Description = "d", Version = "1",
        SupportedInterfaces = interfaces.ToList(),
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
    };

    private static AgentInterface Itf(string binding, string? tenant = null, string version = "1.0") =>
        new() { Url = $"http://localhost/{binding}", ProtocolBinding = binding, ProtocolVersion = version, Tenant = tenant };

    private static (A2AClient Client, FakeFactory[] Factories) Make(bool fallback, FakeTransport[] transports,
        AgentInterface[]? interfaces = null, Action<A2AClientOptions>? configure = null)
    {
        var factories = transports.Select(t => new FakeFactory(t)).ToArray();
        var options = new A2AClientOptions
        {
            InterfaceFallback = fallback,
            MaxRetries = 0,
            EndpointPolicy = AgentEndpointPolicies.AllowAll,
            SupportedBindings = transports.Select(t => t.Name).ToList(),
            TransportFactories = factories.Cast<IA2ATransportFactory>().ToList(),
        };
        configure?.Invoke(options);
        var card = Card(interfaces ?? transports.Select(t => Itf(t.Name)).ToArray());
        return (A2AClientFactory.Create(card, new HttpClient(), options), factories);
    }

    // ---------------------------------------------------------------- unary

    [Fact]
    public async Task Falls_back_to_the_next_interface_when_the_first_cannot_be_reached()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, factories) = Make(true, [a, b]);

        var task = await client.GetTaskAsync("t1");

        Assert.Equal("b", task.ContextId);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, factories[1].Created);
    }

    [Fact]
    public async Task The_interface_that_worked_is_kept_for_later_calls()
    {
        var a = new FakeTransport("a").Fail(Unsent()); // down once, then up again
        var b = new FakeTransport("b");
        var (client, factories) = Make(true, [a, b]);

        await client.GetTaskAsync("t1");
        var second = await client.SendMessageAsync("hi");

        Assert.Equal("b", ((TaskResponse)second).Task.ContextId);
        Assert.Equal(1, a.Calls);
        Assert.Equal(2, b.Calls);
        Assert.Equal(1, factories[1].Created); // created once, on first use
    }

    [Fact]
    public async Task Fallback_transports_are_created_lazily()
    {
        var a = new FakeTransport("a");
        var b = new FakeTransport("b");
        var (client, factories) = Make(true, [a, b]);

        await client.GetTaskAsync("t1");

        Assert.Equal(1, factories[0].Created);
        Assert.Equal(0, factories[1].Created);
    }

    [Fact]
    public async Task Off_by_default()
    {
        Assert.False(new A2AClientOptions().InterfaceFallback);
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, factories) = Make(false, [a, b]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTaskAsync("t1"));

        Assert.Equal(0, b.Calls);
        Assert.Equal(0, factories[1].Created);
    }

    public static TheoryData<string> Reached => ["a2a", "mid-request", "timeout", "blocked", "canceled-wrapping"];

    private static Exception ReachedError(string kind) => kind switch
    {
        "a2a" => new A2AException(A2AErrorCode.TaskNotFound, "no such task") { HttpStatus = 404 },
        "mid-request" => new HttpRequestException(HttpRequestError.ResponseEnded, "the response ended early"),
        "timeout" => new TaskCanceledException("The request timed out.", new TimeoutException()),
        "blocked" => new HttpRequestException(HttpRequestError.ConnectionError, "blocked",
            new A2A.Security.Http.BlockedAddressException("private address")),
        _ => new OperationCanceledException("canceled", Unsent()),
    };

    [Theory]
    [MemberData(nameof(Reached))]
    public async Task Does_not_fall_back_when_the_request_may_have_reached_the_agent(string kind)
    {
        var a = new FakeTransport("a").FailAlways(() => ReachedError(kind));
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetTaskAsync("t1"));

        Assert.Equal(ReachedError(kind).GetType(), ex.GetType());
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task A_wrapped_connection_failure_falls_back_as_the_grpc_transport_reports_it()
    {
        // The gRPC transport throws an A2AException over the RpcException, whose inner exception is the HttpRequestException.
        var a = new FakeTransport("a").FailAlways(() =>
            new A2AException(A2AErrorCode.Internal, "Error starting gRPC call.", new IOException("rpc", Unsent())) { HttpStatus = 503 });
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);

        Assert.Equal("b", (await client.GetTaskAsync("t1")).ContextId);
    }

    [Fact]
    public async Task When_every_interface_fails_the_first_error_is_thrown()
    {
        var first = Unsent();
        var a = new FakeTransport("a").FailAlways(() => first);
        var b = new FakeTransport("b").FailAlways(Unsent);
        var c = new FakeTransport("c").FailAlways(Unsent);
        var (client, _) = Make(true, [a, b, c]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTaskAsync("t1"));

        Assert.Same(first, ex);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, c.Calls);
    }

    [Fact]
    public async Task An_A2A_error_of_a_fallback_interface_is_its_answer()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b").FailAlways(() => new A2AException(A2AErrorCode.TaskNotFound, "no such task"));
        var c = new FakeTransport("c");
        var (client, _) = Make(true, [a, b, c]);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(0, c.Calls);
    }

    [Fact]
    public async Task Returns_to_the_first_interface_when_the_fallback_goes_down()
    {
        var a = new FakeTransport("a").Fail(Unsent());
        var b = new FakeTransport("b").Fail(null, Unsent());
        var (client, _) = Make(true, [a, b]);

        Assert.Equal("b", (await client.GetTaskAsync("t1")).ContextId);
        Assert.Equal("a", (await client.GetTaskAsync("t1")).ContextId);
    }

    [Fact]
    public async Task Skips_interfaces_with_another_tenant()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var c = new FakeTransport("c");
        var (client, _) = Make(true, [a, b, c], [Itf("a", "t-1"), Itf("b", "t-2"), Itf("c", "t-1", "1.1")]);

        Assert.Equal("c", (await client.GetTaskAsync("t1")).ContextId);
        Assert.Equal(0, b.Calls);
        // Each transport has its own options, set up for its interface.
        Assert.Equal("t-1", c.Options!.Tenant);
        Assert.Equal("1.1", c.Options.ProtocolVersion);
        Assert.Equal("1.0", a.Options!.ProtocolVersion);
        Assert.NotSame(a.Options, c.Options);
    }

    [Fact]
    public async Task A_null_and_an_empty_tenant_are_the_same_tenant()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b], [Itf("a"), Itf("b", "")]);

        Assert.Equal("b", (await client.GetTaskAsync("t1")).ContextId);
    }

    [Fact]
    public void A_single_candidate_uses_the_transport_itself()
    {
        var a = new FakeTransport("a");
        var b = new FakeTransport("b");
        var (client, factories) = Make(true, [a, b], [Itf("a", "t-1"), Itf("b", "t-2")]);

        Assert.Equal(1, factories[0].Created);
        Assert.Equal("t-1", a.Options!.Tenant);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Each_fallback_is_counted_with_its_bindings()
    {
        var measured = new ConcurrentQueue<(string? From, string? To)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == "A2A" && i.Name == "a2a.client.interface.fallback") l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? from = null, to = null;
            foreach (var t in tags)
            {
                if (t.Key == "from") from = t.Value as string;
                if (t.Key == "to") to = t.Value as string;
            }

            if (from is "fa" or "fb") measured.Enqueue((from, to));
        });
        listener.Start();
        var a = new FakeTransport("fa").FailAlways(Unsent);
        var b = new FakeTransport("fb").FailAlways(Unsent);
        var c = new FakeTransport("fc");
        var (client, _) = Make(true, [a, b, c]);

        await client.GetTaskAsync("t1");

        Assert.Equal([("fa", "fb"), ("fb", "fc")], measured.ToArray());
    }

    [Fact]
    public void Every_candidate_passes_the_endpoint_policy()
    {
        var a = new FakeTransport("a");
        var b = new FakeTransport("b");
        var options = new A2AClientOptions
        {
            InterfaceFallback = true,
            EndpointPolicy = check => check.Endpoint.AbsolutePath != "/b",
            SupportedBindings = ["a", "b"],
            TransportFactories = [new FakeFactory(a), new FakeFactory(b)],
        };

        var ex = Assert.Throws<A2AException>(() => A2AClientFactory.Create(Card(Itf("a"), Itf("b")), new HttpClient(), options));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    // ---------------------------------------------------------------- streams

    [Fact]
    public async Task A_stream_falls_back_when_it_fails_before_the_first_event()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") }))
            events.Add(e);

        Assert.Equal("b", Assert.IsType<TaskEvent>(Assert.Single(events)).Task.ContextId);
        Assert.Equal(1, a.Calls);
    }

    [Fact]
    public async Task A_stream_does_not_fall_back_after_an_event()
    {
        var failure = Unsent();
        var a = new FakeTransport("a") { FailAfterFirstEvent = failure };
        var b = new FakeTransport("b");
        var fallback = new FallbackTransport([(Itf("a"), () => a), (Itf("b"), () => b)], a);

        var events = new List<StreamResponse>();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var e in fallback.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" })) events.Add(e);
        });

        Assert.Same(failure, ex);
        Assert.Single(events);
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task A_stream_does_not_fall_back_on_an_A2A_error()
    {
        var a = new FakeTransport("a").FailAlways(() => new A2AException(A2AErrorCode.TaskNotFound, "no such task"));
        var b = new FakeTransport("b");
        var fallback = new FallbackTransport([(Itf("a"), () => a), (Itf("b"), () => b)], a);

        await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in fallback.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" })) { }
        });

        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task A_stream_that_fell_back_is_kept_for_later_calls()
    {
        var a = new FakeTransport("a").Fail(Unsent());
        var b = new FakeTransport("b");
        var fallback = new FallbackTransport([(Itf("a"), () => a), (Itf("b"), () => b)], a);

        await foreach (var _ in fallback.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = "t1" })) { }
        await fallback.GetTaskAsync(new GetTaskRequest { Id = "t1" });

        Assert.Equal("b", fallback.CurrentInterface.ProtocolBinding);
        Assert.Equal(1, a.Calls);
        Assert.Equal(2, b.Calls);
    }

    // ---------------------------------------------------------------- built-in HTTP transports

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AgentCard HttpCard(params (string Binding, string Url)[] interfaces) =>
        Card(interfaces.Select(i => new AgentInterface { Url = i.Url, ProtocolBinding = i.Binding, ProtocolVersion = "1.0" }).ToArray());

    private static A2AClientOptions HttpOptions() =>
        new() { InterfaceFallback = true, MaxRetries = 1, EndpointPolicy = AgentEndpointPolicies.AllowAll };

    [Fact]
    public async Task Over_http_a_refused_connection_falls_back_after_the_transport_retries()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/rpc")
            ? throw Unsent()
            : Json(HttpStatusCode.OK, TaskJson));
        var card = HttpCard((ProtocolBindings.JsonRpc, "http://localhost/rpc"), (ProtocolBindings.HttpJson, "http://localhost/rest"));
        var options = HttpOptions();
        options.TimeProvider = new ImmediateTime();

        var client = A2AClientFactory.Create(card, new HttpClient(handler), options);
        var task = await client.GetTaskAsync("t1");

        Assert.Equal("t1", task.Id);
        var paths = handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToArray();
        Assert.Equal(["/rpc", "/rpc", "/rest/tasks/t1"], paths); // MaxRetries = 1 on the first interface, then the fallback
    }

    [Fact]
    public async Task Over_http_a_404_does_not_fall_back()
    {
        // On HTTP+JSON a 404 is TaskNotFound: the agent answered.
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/rest")
            ? Json(HttpStatusCode.NotFound, "")
            : Json(HttpStatusCode.OK, $$"""{"jsonrpc":"2.0","id":"x","result":{{TaskJson}}}"""));
        var card = HttpCard((ProtocolBindings.HttpJson, "http://localhost/rest"), (ProtocolBindings.JsonRpc, "http://localhost/rpc"));

        var client = A2AClientFactory.Create(card, new HttpClient(handler), HttpOptions());

        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("t1"));
        Assert.All(handler.Requests, r => Assert.StartsWith("/rest", r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task With_credentials_an_interface_on_another_origin_is_not_a_fallback()
    {
        var cardJson = System.Text.Json.JsonSerializer.Serialize(new AgentCard
        {
            Name = "agent", Description = "d", Version = "1",
            SupportedInterfaces =
            [
                new AgentInterface { Url = "http://localhost/rpc", ProtocolBinding = ProtocolBindings.JsonRpc, ProtocolVersion = "1.0" },
                new AgentInterface { Url = "http://other.localhost/rpc", ProtocolBinding = ProtocolBindings.JsonRpc, ProtocolVersion = "1.0" },
                new AgentInterface { Url = "http://localhost/rest", ProtocolBinding = ProtocolBindings.HttpJson, ProtocolVersion = "1.0" },
            ],
            Capabilities = new AgentCapabilities(), DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"], Skills = [],
            SecuritySchemes = new() { ["key"] = SecurityScheme.ApiKey("X-Key") },
            SecurityRequirements = [Cards.Req(("key", []))],
        }, A2A.Json.A2AJson.Options);
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/.well-known/agent-card.json" => Json(HttpStatusCode.OK, cardJson),
            "/rpc" when r.RequestUri.Host == "localhost" => throw Unsent(),
            _ => Json(HttpStatusCode.OK, TaskJson),
        });
        var provider = new RecordingProvider("key");

        var options = HttpOptions();
        options.MaxRetries = 0;
        var client = await A2AClientFactory.CreateAsync(new Uri("http://localhost/"), [provider], handler, options);
        var task = await client.GetTaskAsync("t1");

        Assert.Equal("t1", task.Id);
        var calls = handler.Requests.Where(r => !r.RequestUri!.AbsolutePath.StartsWith("/.well-known")).ToArray();
        Assert.DoesNotContain(calls, r => r.RequestUri!.Host == "other.localhost");
        Assert.Equal("/rest/tasks/t1", calls[^1].RequestUri!.AbsolutePath);
        Assert.True(calls[^1].Headers.Contains("X-Cred"));
    }

    [Fact]
    public async Task Over_a_credentialed_http_client_an_interface_on_another_origin_is_skipped()
    {
        // As AddA2AClient builds it: A2AClientFactory.Create over an HttpClient that carries the credentials.
        var card = HttpCard(
            (ProtocolBindings.JsonRpc, "https://agent.example/rpc"),
            (ProtocolBindings.JsonRpc, "https://other.example/rpc"),
            (ProtocolBindings.HttpJson, "https://agent.example/rest"));
        card.SecuritySchemes = new() { ["key"] = SecurityScheme.ApiKey("X-Key") };
        card.SecurityRequirements = [Cards.Req(("key", []))];
        var resolved = Cards.Resolved(card);
        var inner = new RoutingHandler(r => r.RequestUri!.AbsolutePath == "/rpc" ? throw Unsent() : Json(HttpStatusCode.OK, TaskJson));
        var binding = A2A.Client.Credentials.A2ACredentialBinding.Create(resolved, card.SupportedInterfaces[0], [new RecordingProvider("key")]);
        var http = new HttpClient(new A2A.Client.Credentials.A2ACredentialHandler(binding, inner));
        var options = HttpOptions();
        options.MaxRetries = 0;

        var task = await A2AClientFactory.Create(card, http, options).GetTaskAsync("t1");

        Assert.Equal("t1", task.Id);
        Assert.DoesNotContain(inner.Requests, r => r.RequestUri!.Host == "other.example");
        Assert.Equal("/rest/tasks/t1", inner.Requests.Last().RequestUri!.AbsolutePath);
        Assert.True(inner.Requests.Last().Headers.Contains("X-Cred"));
    }

    [Fact]
    public async Task When_the_only_fallback_refuses_credentials_the_first_error_is_thrown()
    {
        var card = HttpCard((ProtocolBindings.JsonRpc, "https://agent.example/rpc"), (ProtocolBindings.JsonRpc, "https://other.example/rpc"));
        card.SecuritySchemes = new() { ["key"] = SecurityScheme.ApiKey("X-Key") };
        card.SecurityRequirements = [Cards.Req(("key", []))];
        var first = Unsent();
        var inner = new RoutingHandler(_ => throw first);
        var binding = A2A.Client.Credentials.A2ACredentialBinding.Create(Cards.Resolved(card), card.SupportedInterfaces[0], [new RecordingProvider("key")]);
        var options = HttpOptions();
        options.MaxRetries = 0;
        var client = A2AClientFactory.Create(card, new HttpClient(new A2A.Client.Credentials.A2ACredentialHandler(binding, inner)), options);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTaskAsync("t1"));

        Assert.Same(first, ex);
        Assert.Single(inner.Requests);
    }

    // ---------------------------------------------------------------- every operation

    /// <summary>A client whose card declares every capability, so the client's own checks never stop a call.</summary>
    private static (A2AClient Client, FakeFactory[] Factories) MakeAll(FakeTransport[] transports) =>
        Make(true, transports, configure: o => o.ValidateCapabilities = false);

    public static TheoryData<string> Operations =>
    [
        "list-tasks", "cancel-task", "create-push-config", "get-push-config", "list-push-configs",
        "delete-push-config", "extended-card", "custom-operation",
    ];

    /// <summary>Calls <paramref name="operation"/> and returns which transport answered (null when the answer names none).</summary>
    private static async Task<string?> CallAsync(A2AClient client, string operation)
    {
        switch (operation)
        {
            case "list-tasks":
                return (await client.ListTasksAsync(new ListTasksRequest())).Tasks.Single().ContextId;
            case "cancel-task":
                return (await client.CancelTaskAsync("t1")).ContextId;
            case "create-push-config":
                return (await client.CreateTaskPushNotificationConfigAsync(
                    new TaskPushNotificationConfig { TaskId = "t1", Url = "https://hooks.example.com/" })).Id;
            case "get-push-config":
                return (await client.GetTaskPushNotificationConfigAsync(
                    new GetTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" })).Id;
            case "list-push-configs":
                return (await client.ListTaskPushNotificationConfigsAsync(
                    new ListTaskPushNotificationConfigsRequest { TaskId = "t1" })).Configs.Single().Id;
            case "delete-push-config":
                await client.DeleteTaskPushNotificationConfigAsync(
                    new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = "c1" });
                return null;
            case "extended-card":
                return (await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest())).Description;
            case "custom-operation":
                return (await client.InvokeExtensionOperationAsync("acme/ping",
                    System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string>()))).GetString();
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_unary_operation_falls_back(string operation)
    {
        // Custom operations need a transport that can call them; the others take the plain fake.
        FakeTransport a = operation == "custom-operation" ? new ExtensionTransport("a") : new FakeTransport("a");
        FakeTransport b = operation == "custom-operation" ? new ExtensionTransport("b") : new FakeTransport("b");
        a.FailAlways(Unsent);
        var (client, _) = MakeAll([a, b]);

        var answeredBy = await CallAsync(client, operation);

        if (answeredBy is not null) Assert.Equal("b", answeredBy);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public async Task A_custom_operation_on_a_transport_without_them_is_unsupported_and_does_not_fall_back()
    {
        var a = new FakeTransport("a"); // no custom operations
        var b = new ExtensionTransport("b");
        var (client, _) = MakeAll([a, b]);

        var ex = await Assert.ThrowsAsync<A2AException>(() => CallAsync(client, "custom-operation"));

        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Equal(0, b.Calls);
    }

    // ---------------------------------------------------------------- streams

    [Fact]
    public async Task A_subscription_falls_back_when_it_fails_before_the_first_event()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);

        var events = new List<StreamResponse>();
        await foreach (var e in client.SubscribeToTaskAsync("t1")) events.Add(e);

        Assert.Equal("b", Assert.IsType<TaskEvent>(Assert.Single(events)).Task.ContextId);
        Assert.Equal(1, a.Calls);
    }

    [Fact]
    public async Task Observing_a_task_falls_back_when_the_subscription_fails_before_the_first_event()
    {
        // ObserveTaskAsync subscribes through the resumable frame method, with no cursor on the first try.
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);

        var events = new List<StreamResponse>();
        await foreach (var e in client.ObserveTaskAsync("t1")) events.Add(e);

        Assert.Equal("b", Assert.IsType<TaskEvent>(Assert.Single(events)).Task.ContextId);
        Assert.Equal(1, a.Calls);
    }

    [Fact]
    public async Task When_every_interface_fails_before_the_first_event_the_first_error_is_thrown()
    {
        var first = Unsent();
        var a = new FakeTransport("a").Fail(first);
        var b = new FakeTransport("b").FailAlways(Unsent);
        var (client, _) = Make(true, [a, b]);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("hi"))) { }
        });

        Assert.Same(first, ex);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public async Task The_plain_streaming_method_of_the_fallback_transport_falls_back_too()
    {
        // A2AClient streams through the frame methods; a caller of the transport itself uses this one.
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var transport = new FallbackTransport([(Itf("a"), () => a), (Itf("b"), () => b)], a);

        var events = new List<StreamResponse>();
        await foreach (var e in transport.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") }))
            events.Add(e);

        Assert.Equal("b", Assert.IsType<TaskEvent>(Assert.Single(events)).Task.ContextId);
        Assert.Equal("b", transport.CurrentInterface.ProtocolBinding);
    }

    // ---------------------------------------------------------------- transport creation

    [Fact]
    public async Task An_interface_whose_transport_cannot_be_created_is_skipped_and_created_again_later()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var c = new FakeTransport("c").Fail(null, Unsent()); // answers the first call, is down for the second
        var (client, factories) = Make(true, [a, b, c]);
        factories[1].FailCreates = 1;

        var first = await client.GetTaskAsync("t1");
        Assert.Equal("c", first.ContextId); // b could not be created: skipped
        Assert.Equal(0, b.Calls);

        var second = await client.GetTaskAsync("t1");
        Assert.Equal("b", second.ContextId); // c is down now; b's creation is tried again and works
        Assert.Equal((2, 1), (factories[1].CreateAttempts, factories[1].Created));
    }

    // ---------------------------------------------------------------- telemetry and live exchange

    [Fact]
    public async Task The_telemetry_binding_follows_the_fallback()
    {
        var a = new FakeTransport("a").FailAlways(Unsent);
        var b = new FakeTransport("b");
        var (client, _) = Make(true, [a, b]);
        Assert.Equal("a", client.TelemetryBinding);

        await client.GetTaskAsync("t1");

        Assert.Equal("b", client.TelemetryBinding);
    }

#pragma warning disable A2AEXP005
    [Fact]
    public async Task A_live_exchange_opens_on_the_current_interface_and_does_not_fall_back()
    {
        // Opening may not send anything yet, so even an unsent failure is the caller's to see.
        var a = new LiveTransport("a").Fail(Unsent());
        var b = new LiveTransport("b");
        var (client, _) = Make(true, [a, b]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.OpenLiveAsync(Message.FromUser("hi")));
        var opened = await Assert.ThrowsAsync<LiveTransport.Opened>(() => client.OpenLiveAsync(Message.FromUser("hi")));

        Assert.Equal("a", opened.By);
        Assert.Equal(0, b.Calls);
    }

    [Fact]
    public async Task A_live_exchange_on_a_transport_without_one_is_not_supported()
    {
        var (client, _) = Make(true, [new FakeTransport("a"), new LiveTransport("b")]);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenLiveAsync(Message.FromUser("hi")));

        Assert.Contains("no live exchange", ex.Message);
    }
#pragma warning restore A2AEXP005

    private sealed class ImmediateTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan) ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
