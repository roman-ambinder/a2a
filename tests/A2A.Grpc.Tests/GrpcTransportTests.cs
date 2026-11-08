using System.Net.Http;
using A2A.Client;
using A2A.Client.Grpc;
using Google.Protobuf;
using Grpc.Core;
using Pb = Lf.A2a.V1;

namespace A2A.Grpc.Tests;

/// <summary>Client-side semantics of <see cref="GrpcTransport"/>: metadata, idempotency key, timeout, retries.</summary>
public class GrpcTransportTests
{
    /// <summary>A call invoker that answers unary calls from a script and records each attempt's options.</summary>
    private sealed class ScriptedInvoker : CallInvoker
    {
        private readonly Queue<Func<object>> _script = new();
        public List<(string Method, CallOptions Options, object Request)> Calls { get; } = [];

        public ScriptedInvoker Then(Func<object> step) { _script.Enqueue(step); return this; }
        public ScriptedInvoker ThenFail(RpcException ex) => Then(() => throw ex);
        public ScriptedInvoker ThenReturn(IMessage response) => Then(() => response);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Calls.Add((method.Name, options, request!));
            Task<TResponse> response;
            try { response = Task.FromResult((TResponse)_script.Dequeue()()); }
            catch (Exception ex) { response = Task.FromException<TResponse>(ex); }
            return new AsyncUnaryCall<TResponse>(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private static Pb.Task ATask(string id = "t1") => new() { Id = id, ContextId = "c", Status = new Pb.TaskStatus { State = Pb.TaskState.Completed } };

    private static Pb.SendMessageResponse AReply() => new() { Message = new Pb.Message { MessageId = "r", Role = Pb.Role.Agent, Parts = { new Pb.Part { Text = "ok" } } } };

    private static RpcException Retryable() => GrpcErrors.ToRpcException(new A2AException(A2AErrorCode.Internal) { Retryable = true, RetryAfter = TimeSpan.Zero });

    private static RpcException Transport(HttpRequestError error) =>
        new(new Status(StatusCode.Unavailable, "Error starting gRPC call.", new HttpRequestException(error, "transport")));

    private static GrpcTransport Transport(ScriptedInvoker invoker, int maxRetries = 2) =>
        new(invoker, new A2AClientOptions { MaxRetries = maxRetries, Extensions = ["urn:a"] });

    [Fact]
    public async Task Missing_required_field_fails_before_the_call()
    {
        var invoker = new ScriptedInvoker().ThenReturn(ATask());
        var client = new A2AClient(Transport(invoker));

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(""));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("id is required", ex.Message);
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task Sends_version_extensions_idempotency_key_and_per_call_headers_as_metadata()
    {
        var invoker = new ScriptedInvoker().ThenReturn(AReply());
        var options = new A2ARequestOptions { Extensions = ["urn:b"] };
        options.Headers["X-Trace"] = "abc";
        options.Headers["A2A-Version"] = "1.0"; // replaces the SDK value, does not duplicate it
        var message = Message.FromUser("hi");
        await Transport(invoker).SendMessageAsync(new SendMessageRequest { Message = message }, options);

        var headers = Assert.Single(invoker.Calls).Options.Headers!;
        Assert.Equal("1.0", Assert.Single(headers, h => h.Key == "a2a-version").Value);
        Assert.Equal("urn:a,urn:b", headers.GetValue("a2a-extensions"));
        Assert.Equal("abc", headers.GetValue("x-trace"));
        Assert.Equal(message.MessageId, headers.GetValue("idempotency-key"));
    }

    [Fact]
    public async Task Idempotency_key_option_wins_and_an_empty_message_id_is_filled()
    {
        var invoker = new ScriptedInvoker().ThenReturn(AReply());
        var message = Message.FromUser("hi");
        message.MessageId = "";
        await Transport(invoker).SendMessageAsync(new SendMessageRequest { Message = message }, new A2ARequestOptions { IdempotencyKey = "k-7" });
        var (_, callOptions, request) = Assert.Single(invoker.Calls);
        Assert.Equal("k-7", callOptions.Headers!.GetValue("idempotency-key"));
        Assert.False(string.IsNullOrEmpty(((Pb.SendMessageRequest)request).Message.MessageId));
    }

    [Fact]
    public async Task Other_methods_send_no_idempotency_key()
    {
        var invoker = new ScriptedInvoker().ThenReturn(ATask());
        await Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { IdempotencyKey = "k" });
        Assert.Null(Assert.Single(invoker.Calls).Options.Headers!.GetValue("idempotency-key"));
    }

    [Fact]
    public async Task Each_attempt_gets_a_deadline_from_the_timeout()
    {
        var invoker = new ScriptedInvoker().ThenReturn(ATask()).ThenReturn(ATask());
        var transport = Transport(invoker);
        var before = DateTime.UtcNow;
        await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { Timeout = TimeSpan.FromSeconds(5) });
        await transport.GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { Timeout = Timeout.InfiniteTimeSpan });
        var deadline = invoker.Calls[0].Options.Deadline!.Value;
        Assert.InRange(deadline, before.AddSeconds(4), DateTime.UtcNow.AddSeconds(6));
        Assert.Null(invoker.Calls[1].Options.Deadline);
    }

    [Fact]
    public async Task Deadline_exceeded_is_a_timeout()
    {
        var invoker = new ScriptedInvoker().ThenFail(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline")));
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { Timeout = TimeSpan.FromSeconds(1) }));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Read_calls_retry_on_errors_marked_retryable_and_on_transport_failures()
    {
        var invoker = new ScriptedInvoker().ThenFail(Retryable()).ThenFail(Transport(HttpRequestError.ResponseEnded)).ThenReturn(ATask());
        Assert.Equal("t1", (await Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" })).Id);
        Assert.Equal(3, invoker.Calls.Count);
    }

    [Fact]
    public async Task Read_calls_retry_on_io_failures()
    {
        var io = new RpcException(new Status(StatusCode.Unavailable, "connection reset", new System.IO.IOException("reset")));
        var invoker = new ScriptedInvoker().ThenFail(io).ThenReturn(ATask());
        Assert.Equal("t1", (await Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" })).Id);
        Assert.Equal(2, invoker.Calls.Count);
    }

    [Fact]
    public async Task Client_side_failures_that_are_not_transport_failures_are_not_retried()
    {
        // Grpc.Net.Client reports a response it cannot parse as INTERNAL with the parse error as the debug exception.
        var parse = new RpcException(new Status(StatusCode.Internal, "Failed to deserialize response message.", new InvalidOperationException("bad bytes")));
        var invoker = new ScriptedInvoker().ThenFail(parse).ThenReturn(ATask());
        var ex = await Assert.ThrowsAsync<A2AException>(() => Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.Single(invoker.Calls);
    }

    [Fact]
    public async Task Max_retries_bounds_the_attempts()
    {
        var invoker = new ScriptedInvoker().ThenFail(Retryable()).ThenFail(Retryable());
        await Assert.ThrowsAsync<A2AException>(() => Transport(invoker, maxRetries: 1).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(2, invoker.Calls.Count);

        var once = new ScriptedInvoker().ThenFail(Retryable());
        await Assert.ThrowsAsync<A2AException>(() =>
            Transport(once).GetTaskAsync(new GetTaskRequest { Id = "t1" }, new A2ARequestOptions { MaxRetries = 0 }));
        Assert.Single(once.Calls);
    }

    [Fact]
    public async Task Errors_not_marked_retryable_are_not_retried()
    {
        var invoker = new ScriptedInvoker().ThenFail(GrpcErrors.ToRpcException(A2AException.TaskNotFound("t1")));
        var ex = await Assert.ThrowsAsync<A2AException>(() => Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Single(invoker.Calls);
    }

    [Fact]
    public async Task Send_message_retries_a_transport_failure_only_when_the_request_never_left()
    {
        var unsent = new ScriptedInvoker().ThenFail(Transport(HttpRequestError.ConnectionError)).ThenReturn(AReply());
        await Transport(unsent).SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") });
        Assert.Equal(2, unsent.Calls.Count);
        Assert.Equal(unsent.Calls[0].Options.Headers!.GetValue("idempotency-key"), unsent.Calls[1].Options.Headers!.GetValue("idempotency-key"));

        var maybeSent = new ScriptedInvoker().ThenFail(Transport(HttpRequestError.ResponseEnded));
        await Assert.ThrowsAsync<A2AException>(() => Transport(maybeSent).SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") }));
        Assert.Single(maybeSent.Calls);

        var retryable = new ScriptedInvoker().ThenFail(Retryable()).ThenReturn(AReply());
        await Transport(retryable).SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("hi") });
        Assert.Equal(2, retryable.Calls.Count);
    }

    [Fact]
    public async Task Push_config_create_and_delete_are_never_retried()
    {
        var create = new ScriptedInvoker().ThenFail(Retryable());
        await Assert.ThrowsAsync<A2AException>(() => Transport(create).CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = "t", Url = "https://h/x" }));
        Assert.Single(create.Calls);

        var delete = new ScriptedInvoker().ThenFail(Transport(HttpRequestError.ConnectionError));
        await Assert.ThrowsAsync<A2AException>(() => Transport(delete).DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t", Id = "c" }));
        Assert.Single(delete.Calls);
    }

    [Fact]
    public async Task Server_unavailable_status_is_retried()
    {
        var invoker = new ScriptedInvoker().ThenFail(new RpcException(new Status(StatusCode.Unavailable, "draining"))).ThenReturn(new Pb.ListTaskPushNotificationConfigsResponse());
        Assert.Empty((await Transport(invoker).ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = "t" })).Configs);
        Assert.Equal(2, invoker.Calls.Count);
    }

    [Fact]
    public async Task Configure_request_hook_is_refused_rather_than_silently_skipped()
    {
        var invoker = new ScriptedInvoker().ThenReturn(ATask());
        var options = new A2ARequestOptions { ConfigureRequest = (_, _) => ValueTask.CompletedTask };
        await Assert.ThrowsAsync<NotSupportedException>(() => Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }, options));
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var invoker = new ScriptedInvoker().ThenFail(new RpcException(new Status(StatusCode.Cancelled, "canceled")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transport(invoker).GetTaskAsync(new GetTaskRequest { Id = "t1" }, null, cts.Token));
    }
}
