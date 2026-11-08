using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pb = Lf.A2a.V1;

namespace A2A.Grpc.Tests;

/// <summary>
/// The P0 security contracts over gRPC: the same answers the HTTP bindings give (see A2A.Tests for the HTTP side).
/// </summary>
public class GrpcSecurityTests
{
    private static Metadata V(params (string Key, string Value)[] extra)
    {
        var m = new Metadata { { "a2a-version", "1.0" } };
        foreach (var (k, v) in extra) m.Add(k, v);
        return m;
    }

    private static Pb.SendMessageRequest Send(string text, string messageId = "m1", string? taskId = null, string? tenant = null)
    {
        var r = new Pb.SendMessageRequest { Message = new Pb.Message { MessageId = messageId, Role = Pb.Role.User, Parts = { new Pb.Part { Text = text } } } };
        if (taskId is not null) r.Message.TaskId = taskId;
        if (tenant is not null) r.Tenant = tenant;
        return r;
    }

    private static Google.Rpc.ErrorInfo Info(RpcException ex) => ex.GetRpcStatus()?.GetDetail<Google.Rpc.ErrorInfo>()
        ?? throw new Xunit.Sdk.XunitException($"No ErrorInfo in {ex.Status}");

    // ------------------------------------------------------------------ ownership

    public enum Op { GetTask, CancelTask, SubscribeToTask, SendMessage, SendStreamingMessage, GetPush, ListPush, DeletePush, CreatePush }

    public static TheoryData<Op> Ops => new(Enum.GetValues<Op>());

    private static async Task InvokeAsync(Pb.A2AService.A2AServiceClient raw, Op op, string taskId, string configId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var m = V();
        switch (op)
        {
            case Op.GetTask: await raw.GetTaskAsync(new Pb.GetTaskRequest { Id = taskId }, m).ResponseAsync; break;
            case Op.CancelTask: await raw.CancelTaskAsync(new Pb.CancelTaskRequest { Id = taskId }, m).ResponseAsync; break;
            case Op.SubscribeToTask:
            {
                using var call = raw.SubscribeToTask(new Pb.SubscribeToTaskRequest { Id = taskId }, m, cancellationToken: cts.Token);
                await call.ResponseStream.MoveNext(cts.Token);
                break;
            }
            case Op.SendMessage: await raw.SendMessageAsync(Send("more", Guid.NewGuid().ToString(), taskId), m).ResponseAsync; break;
            case Op.SendStreamingMessage:
            {
                using var call = raw.SendStreamingMessage(Send("more", Guid.NewGuid().ToString(), taskId), m, cancellationToken: cts.Token);
                await call.ResponseStream.MoveNext(cts.Token);
                break;
            }
            case Op.GetPush: await raw.GetTaskPushNotificationConfigAsync(new Pb.GetTaskPushNotificationConfigRequest { TaskId = taskId, Id = configId }, m).ResponseAsync; break;
            case Op.ListPush: await raw.ListTaskPushNotificationConfigsAsync(new Pb.ListTaskPushNotificationConfigsRequest { TaskId = taskId }, m).ResponseAsync; break;
            case Op.DeletePush: await raw.DeleteTaskPushNotificationConfigAsync(new Pb.DeleteTaskPushNotificationConfigRequest { TaskId = taskId, Id = configId }, m).ResponseAsync; break;
            case Op.CreatePush: await raw.CreateTaskPushNotificationConfigAsync(new Pb.TaskPushNotificationConfig { TaskId = taskId, Url = "https://hooks.example/x" }, m).ResponseAsync; break;
        }
    }

    [Theory, MemberData(nameof(Ops))]
    public async Task Foreign_owner_gets_not_found_indistinguishable_from_missing(Op op)
    {
        await using var host = await GrpcHost.StartAsync();
        var alice = host.Client("alice");
        var task = Assert.IsType<TaskResponse>(await alice.SendMessageAsync("input")).Task;
        var config = await alice.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/a" });

        var bob = host.Raw("bob");
        var foreign = await Assert.ThrowsAsync<RpcException>(() => InvokeAsync(bob, op, task.Id, config.Id!));
        var missing = await Assert.ThrowsAsync<RpcException>(() => InvokeAsync(bob, op, "no-such-task", config.Id!));

        Assert.Equal(StatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(missing.StatusCode, foreign.StatusCode);
        Assert.Equal("TASK_NOT_FOUND", Info(foreign).Reason);
        Assert.Equal(missing.Status.Detail, foreign.Status.Detail);
        Assert.Equal(task.Id, Info(foreign).Metadata["taskId"]);
        Assert.Equal("no-such-task", Info(missing).Metadata["taskId"]);

        // alice still sees it
        Assert.Equal(task.Id, (await alice.GetTaskAsync(task.Id)).Id);
    }

    [Fact]
    public async Task Tenant_comes_from_the_request_field()
    {
        await using var host = await GrpcHost.StartAsync();
        var acme = host.Client("alice", new A2AClientOptions { Tenant = "acme" });
        var task = Assert.IsType<TaskResponse>(await acme.SendMessageAsync("input")).Task;
        Assert.Equal(task.Id, (await acme.GetTaskAsync(task.Id)).Id);
        var other = await Assert.ThrowsAsync<RpcException>(() => host.Raw("alice").GetTaskAsync(new Pb.GetTaskRequest { Id = task.Id }, V()).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, other.StatusCode);
    }

    private sealed class FixedTenantResolver(string tenant) : ITenantResolver
    {
        public string Resolve(Microsoft.AspNetCore.Http.HttpContext http) => tenant;
    }

    [Fact]
    public async Task Resolver_tenant_that_differs_from_the_request_tenant_is_invalid_params()
    {
        await using var host = await GrpcHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<ITenantResolver>(new FixedTenantResolver("a"))));
        var raw = host.Raw("alice");

        var ex = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(Send("hello", tenant: "b"), V()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("INVALID_PARAMS", Info(ex).Reason);

        // the same tenant as the resolver's is fine
        var ok = await raw.SendMessageAsync(Send("hello", "m2", tenant: "a"), V()).ResponseAsync;
        Assert.NotNull(ok);
    }

    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task Unauthenticated_caller_is_refused_on_unary_and_streaming_calls()
    {
        await using var host = await GrpcHost.StartAsync();
        var anon = host.Raw(user: null);
        var unary = await Assert.ThrowsAsync<RpcException>(() => anon.GetTaskAsync(new Pb.GetTaskRequest { Id = "x" }, V()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, unary.StatusCode);

        using var stream = anon.SendStreamingMessage(Send("task"), V());
        var streaming = await Assert.ThrowsAsync<RpcException>(() => stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unauthenticated, streaming.StatusCode);

        var viaClient = await Assert.ThrowsAsync<A2AException>(() => host.Client(user: null).GetTaskAsync("x"));
        Assert.Equal(A2AErrorCode.Unauthenticated, viaClient.Code);
    }

    [Fact]
    public async Task Authenticated_user_with_the_reserved_anonymous_id_is_unauthenticated()
    {
        await using var host = await GrpcHost.StartAsync();
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Raw("anonymous").SendMessageAsync(Send("hello"), V()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Handler_backstop_refuses_anonymous_callers_without_endpoint_authorization()
    {
        // Endpoint authorization is skipped (anonymous allowed at the endpoint), but the handler still refuses anonymous callers
        // unless AllowAnonymous is set. Startup is allowed here only because AllowAnonymous makes the validator pass; then it is
        // turned off again at runtime through the options instance to reach the handler's own check.
        A2AServerOptions? options = null;
        await using var host = await GrpcHost.StartAsync(o => { o.AllowAnonymous = true; options = o; }, requireAuth: false);
        Assert.Equal("hi there", Assert.IsType<MessageResponse>(await host.Client(user: null).SendMessageAsync("hello")).Message.GetText());
        options!.AllowAnonymous = false;
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Raw(user: null).SendMessageAsync(Send("hello", "m2"), V()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    // ------------------------------------------------------------------ startup checks

    [Fact]
    public async Task Startup_fails_without_an_authorization_policy_on_the_grpc_endpoints()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GrpcHost.StartAsync(requireAuth: false));
        Assert.Contains("authorization policy", ex.Message);
    }

    [Fact]
    public async Task Startup_fails_when_the_grpc_endpoints_also_allow_anonymous()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GrpcHost.StartAsync(configureEndpoints: e => e.WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute())));
        Assert.Contains("authorization policy", ex.Message);
    }

    [Fact]
    public async Task Startup_fails_without_the_rate_limiter_middleware()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GrpcHost.StartAsync(useA2A: false));
        Assert.Contains("UseA2A", ex.Message);
    }

    [Fact]
    public async Task AddA2AGrpc_registers_the_aspnetcore_services()
    {
        // AddA2AGrpc includes AddA2AServerAspNetCore (one registration call per binding package), so the startup check and the
        // rate limit still apply without the explicit call.
        await using var host = await GrpcHost.StartAsync(aspNetCore: false);
        Assert.IsType<MessageResponse>(await host.Client().SendMessageAsync("hello"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => GrpcHost.StartAsync(aspNetCore: false, useA2A: false));
        Assert.Contains("UseA2A", ex.Message);
    }

    [Fact]
    public async Task Anonymous_mode_starts_without_endpoint_authorization()
    {
        await using var host = await GrpcHost.StartAsync(o => o.AllowAnonymous = true, requireAuth: false);
        Assert.IsType<MessageResponse>(await host.Client(user: null).SendMessageAsync("hello"));
    }

    // ------------------------------------------------------------------ rate limit

    [Fact]
    public async Task Rate_limit_is_resource_exhausted_with_retry_info()
    {
        await using var host = await GrpcHost.StartAsync(services: s => s.AddA2ARateLimiting(o =>
        {
            o.TokenLimit = 2;
            o.TokensPerPeriod = 1;
            o.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
        }));
        var raw = host.Raw();
        await raw.ListTasksAsync(new Pb.ListTasksRequest(), V()).ResponseAsync;
        await raw.ListTasksAsync(new Pb.ListTasksRequest(), V()).ResponseAsync;
        var ex = await Assert.ThrowsAsync<RpcException>(() => raw.ListTasksAsync(new Pb.ListTasksRequest(), V()).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        var info = Info(ex);
        Assert.Equal("RATE_LIMITED", info.Reason);
        Assert.Equal(A2AProtocol.ErrorDomain, info.Domain);
        Assert.Equal("true", info.Metadata["retryable"]);
        Assert.True(int.Parse(info.Metadata["retryAfter"]) >= 1);
        Assert.True(ex.GetRpcStatus()!.GetDetail<Google.Rpc.RetryInfo>()!.RetryDelay.Seconds >= 1);

        // Through the client: a RateLimited error with the server's wait (no retry, since the wait is long).
        var client = host.Client(options: new A2AClientOptions { MaxRetries = 0 });
        var a2a = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.RateLimited, a2a.Code);
        Assert.True(a2a.RetryAfter >= TimeSpan.FromSeconds(1));
        Assert.True(a2a.IsRetryable);

        // Another caller has its own bucket.
        await host.Raw("bob").ListTasksAsync(new Pb.ListTasksRequest(), V()).ResponseAsync;
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public async Task Message_larger_than_the_request_body_limit_is_rejected()
    {
        await using var host = await GrpcHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = 16 * 1024);
        var raw = host.Raw();
        Assert.Equal(Pb.SendMessageResponse.PayloadOneofCase.Message,
            (await raw.SendMessageAsync(Send("hello"), V()).ResponseAsync).PayloadCase);

        var big = Send(new string('x', 32 * 1024), "m-big");
        var ex = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(big, V()).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task Raw_part_over_the_limit_is_payload_too_large()
    {
        await using var host = await GrpcHost.StartAsync(o => o.Limits.MaxRawPartBytes = 1024);
        var req = Send("task", "m-raw");
        req.Message.Parts.Add(new Pb.Part { Raw = ByteString.CopyFrom(new byte[2048]) });
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Raw().SendMessageAsync(req, V()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(A2AErrors.PayloadTooLargeReason, Info(ex).Reason);
    }

    [Fact]
    public async Task Request_nested_deeper_than_the_limit_is_invalid()
    {
        await using var host = await GrpcHost.StartAsync(o => o.Limits.MaxJsonDepth = 8);
        var value = new Google.Protobuf.WellKnownTypes.Value { StringValue = "leaf" };
        for (var i = 0; i < 10; i++)
        {
            var s = new Google.Protobuf.WellKnownTypes.Struct();
            s.Fields["n"] = value;
            value = new Google.Protobuf.WellKnownTypes.Value { StructValue = s };
        }
        var req = Send("task", "m-deep");
        req.Message.Parts.Add(new Pb.Part { Data = value });
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Raw().SendMessageAsync(req, V()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("INVALID_PARAMS", Info(ex).Reason);
    }

    // ------------------------------------------------------------------ idempotency and lifecycle

    [Fact]
    public async Task Idempotency_key_metadata_deduplicates_and_detects_mismatch()
    {
        await using var host = await GrpcHost.StartAsync();
        var raw = host.Raw();
        var key = ("idempotency-key", "k-1");
        var first = await raw.SendMessageAsync(Send("task", "m-a"), V(key)).ResponseAsync;
        var again = await raw.SendMessageAsync(Send("task", "m-a"), V(key)).ResponseAsync;
        Assert.Equal(first.Task.Id, again.Task.Id);

        var other = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(Send("task", "m-b"), V(key)).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, other.StatusCode);
        Assert.Equal(A2AErrors.IdempotencyKeyMismatchReason, Info(other).Reason);
    }

    [Fact]
    public async Task Follow_up_to_a_terminal_task_is_rejected()
    {
        await using var host = await GrpcHost.StartAsync();
        var raw = host.Raw();
        var done = await raw.SendMessageAsync(Send("task", "m-1"), V()).ResponseAsync;
        Assert.Equal(Pb.TaskState.Completed, done.Task.Status.State);
        var ex = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(Send("more", "m-2", done.Task.Id), V()).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal("UNSUPPORTED_OPERATION", Info(ex).Reason);
    }

    // ------------------------------------------------------------------ unexpected errors

    private sealed class ThrowingAuthorization : A2A.Server.Authorization.A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(A2A.Server.ServerCallContext call, A2A.Server.Pipeline.A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret-detail");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_errors_are_generic_internal_unless_details_are_enabled(bool includeDetails)
    {
        await using var host = await GrpcHost.StartAsync(o => o.IncludeExceptionDetails = includeDetails,
            services: s => s.Replace(ServiceDescriptor.Singleton<A2A.Server.Authorization.A2AAuthorizationHandler, ThrowingAuthorization>()));
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.Raw().GetTaskAsync(new Pb.GetTaskRequest { Id = "x" }, V()).ResponseAsync);
        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Equal("INTERNAL", Info(ex).Reason);
        Assert.Equal(includeDetails, ex.Status.Detail.Contains("secret-detail"));
    }
}
