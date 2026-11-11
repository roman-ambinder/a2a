using System.Diagnostics;
using System.Reflection;
using A2A.Client;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server;
using A2A.Server.Idempotency;

namespace A2A.Tests;

/// <summary>
/// The W3C trace context in the message metadata (S34): <see cref="A2AClientOptions.PropagateTraceInMetadata"/> writes it,
/// a server uses it when the request carries no <c>traceparent</c> header, and the idempotency fingerprint ignores it.
/// </summary>
public class TraceMetadataTests
{
    /// <summary>A transport that records the requests it is asked to send and answers with a message.</summary>
    public class RecordingTransport : DispatchProxy
    {
        public List<SendMessageRequest> Sent { get; } = [];

        /// <summary>The traceparent header value the transport would send: the activity current while it sends.</summary>
        public List<string?> CurrentSpans { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var request = (SendMessageRequest)args![0]!;
            Sent.Add(A2AJson.Clone(request));
            CurrentSpans.Add(Activity.Current?.Id);
            var reply = new MessageResponse(Message.FromAgent("ok"));
            return method!.Name switch
            {
                nameof(IA2AClient.SendMessageAsync) => Task.FromResult<SendMessageResponse>(reply),
                nameof(IA2AClient.SendStreamingMessageAsync) => One(StreamResponse.Of(reply.Message)),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private static async IAsyncEnumerable<StreamResponse> One(StreamResponse e)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static (A2AClient Client, RecordingTransport Transport) Recording(bool propagate)
    {
        var transport = DispatchProxy.Create<IA2AClient, RecordingTransport>();
        return (new A2AClient(transport, new A2AClientOptions { PropagateTraceInMetadata = propagate, ValidateCapabilities = false }),
            (RecordingTransport)(object)transport);
    }

    private static System.Security.Claims.ClaimsPrincipal Alice() =>
        new(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "alice")], "test"));

    private static string? Meta(Message m, string key) =>
        m.Metadata is { } md && md.TryGetValue(key, out var v) ? v.GetString() : null;

    [Fact]
    public async Task With_the_option_the_send_carries_the_span_of_the_call_in_the_message_metadata()
    {
        using var trace = new TraceCollector();
        var (client, transport) = Recording(propagate: true);

        await client.SendMessageAsync("hi");

        var sent = Assert.Single(transport.Sent).Message!;
        Assert.Equal(transport.CurrentSpans[0], Meta(sent, A2ATelemetry.TraceParentHeader));
        Assert.True(A2ATelemetry.TryExtractTraceContext(sent, out var carried));
        Assert.Equal(trace.TraceId, carried.TraceId);
        // It is the client's SendMessage span, not the test's root.
        var span = await trace.WaitForAsync(a => a.Source.Name == A2ATelemetry.ClientSourceName && a.OperationName == A2AProtocol.Methods.SendMessage);
        Assert.Equal(span.SpanId, carried.SpanId);
    }

    [Fact]
    public async Task With_the_option_a_streaming_send_carries_it_too()
    {
        using var trace = new TraceCollector();
        var (client, transport) = Recording(propagate: true);

        await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("hi"))) { }

        var sent = Assert.Single(transport.Sent).Message!;
        Assert.True(A2ATelemetry.TryExtractTraceContext(sent, out var carried));
        Assert.Equal(trace.TraceId, carried.TraceId);
    }

    [Fact]
    public async Task Without_the_option_the_metadata_is_unchanged()
    {
        using var trace = new TraceCollector();
        var (client, transport) = Recording(propagate: false);

        await client.SendMessageAsync("hi");

        Assert.Null(Assert.Single(transport.Sent).Message!.Metadata);
    }

    [Fact]
    public async Task A_traceparent_the_caller_set_is_kept()
    {
        using var trace = new TraceCollector();
        var (client, transport) = Recording(propagate: true);
        var own = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
        var message = Message.FromUser("hi");
        message.Metadata = new() { [A2ATelemetry.TraceParentHeader] = A2AJson.SerializeToElement(own) };

        await client.SendMessageAsync(message);

        Assert.Equal(own, Meta(Assert.Single(transport.Sent).Message!, A2ATelemetry.TraceParentHeader));
    }

    [Fact]
    public void Without_a_current_activity_nothing_is_written()
    {
        var message = Message.FromUser("hi");
        Assert.False(A2ATelemetry.InjectTraceContext(message, null));
        Assert.Null(message.Metadata);
    }

    [Fact]
    public void A_traceparent_that_is_not_a_valid_string_is_ignored()
    {
        var message = Message.FromUser("hi");
        message.Metadata = new() { [A2ATelemetry.TraceParentHeader] = A2AJson.SerializeToElement(42) };
        Assert.False(A2ATelemetry.TryExtractTraceContext(message, out _));
        message.Metadata[A2ATelemetry.TraceParentHeader] = A2AJson.SerializeToElement("not-a-traceparent");
        Assert.False(A2ATelemetry.TryExtractTraceContext(message, out _));
        Assert.False(A2ATelemetry.TryExtractTraceContext((Message?)null, out _));
    }

    [Fact]
    public async Task The_server_joins_the_trace_in_the_metadata_when_the_request_has_no_traceparent()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var remote = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var message = Message.FromUser("task");
        message.Metadata = [];
        A2ATelemetry.InjectTraceContext(remote, (name, value) => message.Metadata[name] = A2AJson.SerializeToElement(value));
        var call = new ServerCallContext { Principal = "alice", Binding = "TEST", User = Alice() }; // no TraceParent: no header came

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await host.Handler.SendMessageAsync(new SendMessageRequest { Message = message }, call);
        }
        finally
        {
            Activity.Current = previous;
        }

        var server = await trace.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName && a.OperationName == A2AProtocol.Methods.SendMessage, remote.TraceId);
        Assert.Equal(ActivityKind.Server, server.Kind);
    }

    [Fact]
    public async Task The_traceparent_header_wins_over_the_metadata()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var header = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        var inMetadata = ActivityTraceId.CreateRandom();
        var message = Message.FromUser("task");
        message.Metadata = new()
        {
            [A2ATelemetry.TraceParentHeader] = A2AJson.SerializeToElement($"00-{inMetadata.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01"),
        };
        var call = new ServerCallContext { Principal = "alice", Binding = "TEST", TraceParent = header, User = Alice() };

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await host.Handler.SendMessageAsync(new SendMessageRequest { Message = message }, call);
        }
        finally
        {
            Activity.Current = previous;
        }

        var server = await trace.WaitForAsync(a => a.Source.Name == A2ATelemetry.ServerSourceName && a.OperationName == A2AProtocol.Methods.SendMessage, header.TraceId);
        Assert.Equal(header.SpanId, server.ParentSpanId);
        Assert.Empty(trace.Spans(inMetadata));
    }

    [Fact]
    public async Task Over_the_wire_the_agent_sees_the_trace_in_the_stored_message()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var trace = new TraceCollector();
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { PropagateTraceInMetadata = true });

        var task = (await client.SendMessageAsync("task")).RequireTask();

        var stored = await client.GetTaskAsync(task.Id, historyLength: 10);
        var user = stored.History!.First(m => m.Role == Role.User);
        Assert.True(A2ATelemetry.TryExtractTraceContext(user, out var carried));
        Assert.Equal(trace.TraceId, carried.TraceId);
    }

    [Fact]
    public void The_idempotency_fingerprint_ignores_the_trace_context_and_keeps_other_metadata()
    {
        static SendMessageRequest Request(string json) => A2AJson.Deserialize<SendMessageRequest>(
            $$$"""{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]{{{json}}}}}""")!;

        var plain = Request("");
        var traced = Request(""","metadata":{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","tracestate":"k=v"}""");
        var otherAttempt = Request(""","metadata":{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-01"}""");
        var withOther = Request(""","metadata":{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","k":1}""");
        var onlyOther = Request(""","metadata":{"k":1}""");

        Assert.Equal(RequestHasher.Hash(plain), RequestHasher.Hash(traced));
        Assert.Equal(RequestHasher.Hash(traced), RequestHasher.Hash(otherAttempt));
        Assert.Equal(RequestHasher.Hash(onlyOther), RequestHasher.Hash(withOther));
        Assert.NotEqual(RequestHasher.Hash(plain), RequestHasher.Hash(onlyOther));
    }
}
