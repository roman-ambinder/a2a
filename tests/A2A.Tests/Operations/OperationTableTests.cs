using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using A2A.Server;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Diagnostics;
using A2A.Tests.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.Operations;

/// <summary>
/// The operation table: the built-in methods and routes, and extension operations over JSON-RPC and HTTP+JSON. In the
/// telemetry collection because one test listens to the server's spans (listeners are process-wide).
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class OperationTableTests
{
    private const string Echo = "x.echo";
    private const string EchoUri = "urn:test:x-echo";
    private const string AdminPolicy = "ext-admin";

    /// <summary>Returns its params with the caller's principal and tenant added.</summary>
    private static ValueTask<JsonElement> EchoHandler(A2AOperationContext context, JsonElement parameters, CancellationToken cancellationToken)
    {
        var json = $$"""{"echo":{{parameters.GetRawText()}},"principal":"{{context.Call.Principal}}","tenant":"{{context.Call.Tenant}}","kind":"{{context.Operation.Kind}}","policy":{{(context.Items.TryGetValue(A2AExtensionOperation.PolicySatisfiedItem, out var p) && p is true ? "true" : "false")}}}""";
        return ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static Action<Microsoft.Extensions.DependencyInjection.IServiceCollection> WithEcho(string? policy = null, bool allow = false,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? more = null) => s =>
    {
        s.AddA2AServer().AddExtensionOperation(Echo, EchoUri, EchoHandler, policy, allow);
        s.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireClaim(ClaimTypes.Name, "admin")));
        more?.Invoke(s);
    };

    private static HttpRequestMessage JsonRpc(string method, string @params = """{"say":"hi"}""", string path = "/a2a")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{@params}}}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        return req;
    }

    private static HttpRequestMessage Rest(string method, string body = """{"say":"hi"}""", string prefix = "/a2a")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{prefix}/ext/{method}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(TestAgentHost host, HttpRequestMessage req, string? user = "alice")
    {
        using (req)
        {
            using var resp = await host.Http(user).SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
    }

    // ---------------------------------------------------------------- the table

    [Fact]
    public void Builtin_methods_unchanged()
    {
        var table = new A2AOperationTable([]);
        (string Method, A2AOperation Kind)[] expected =
        [
            (A2AProtocol.Methods.SendMessage, A2AOperation.SendMessage),
            (A2AProtocol.Methods.SendStreamingMessage, A2AOperation.SendStreamingMessage),
            (A2AProtocol.Methods.GetTask, A2AOperation.GetTask),
            (A2AProtocol.Methods.ListTasks, A2AOperation.ListTasks),
            (A2AProtocol.Methods.CancelTask, A2AOperation.CancelTask),
            (A2AProtocol.Methods.SubscribeToTask, A2AOperation.SubscribeToTask),
            (A2AProtocol.Methods.CreateTaskPushNotificationConfig, A2AOperation.CreateTaskPushNotificationConfig),
            (A2AProtocol.Methods.GetTaskPushNotificationConfig, A2AOperation.GetTaskPushNotificationConfig),
            (A2AProtocol.Methods.ListTaskPushNotificationConfigs, A2AOperation.ListTaskPushNotificationConfigs),
            (A2AProtocol.Methods.DeleteTaskPushNotificationConfig, A2AOperation.DeleteTaskPushNotificationConfig),
            (A2AProtocol.Methods.GetExtendedAgentCard, A2AOperation.GetExtendedAgentCard),
        ];
        Assert.Equal(11, table.JsonRpcMethods.Count);
        foreach (var (method, kind) in expected)
            Assert.Same(A2AOperationDescriptor.For(kind), table.JsonRpcMethods[method]);

        // The REST routes, in their original order, each with the shared descriptor of its operation.
        (string Method, string Route, A2AOperation Kind)[] routes =
        [
            ("POST", "/message:send", A2AOperation.SendMessage),
            ("POST", "/message:stream", A2AOperation.SendStreamingMessage),
            ("GET", "/tasks", A2AOperation.ListTasks),
            ("GET", "/tasks/{id}", A2AOperation.GetTask),
            ("POST", "/tasks/{id}:cancel", A2AOperation.CancelTask),
            ("GET|POST", "/tasks/{id}:subscribe", A2AOperation.SubscribeToTask),
            ("POST", "/tasks/{id}/pushNotificationConfigs", A2AOperation.CreateTaskPushNotificationConfig),
            ("GET", "/tasks/{id}/pushNotificationConfigs", A2AOperation.ListTaskPushNotificationConfigs),
            ("GET", "/tasks/{id}/pushNotificationConfigs/{configId}", A2AOperation.GetTaskPushNotificationConfig),
            ("DELETE", "/tasks/{id}/pushNotificationConfigs/{configId}", A2AOperation.DeleteTaskPushNotificationConfig),
            ("GET", "/extendedAgentCard", A2AOperation.GetExtendedAgentCard),
        ];
        Assert.Equal(routes.Length, table.RestRoutes.Count);
        for (var i = 0; i < routes.Length; i++)
        {
            Assert.Equal(routes[i].Method, table.RestRoutes[i].Method);
            Assert.Equal(routes[i].Route, table.RestRoutes[i].Route);
            Assert.Same(A2AOperationDescriptor.For(routes[i].Kind), table.RestRoutes[i].Op);
        }
        Assert.Empty(table.Extensions);
    }

    [Fact]
    public async Task Table_lists_registered_extensions()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(allow: true));
        var table = host.App.Services.GetRequiredService<A2AOperationTable>();
        var ext = Assert.Single(table.Extensions);
        Assert.Equal(Echo, ext.Method);
        Assert.Equal(A2AOperationDescriptor.Extension(Echo), table.JsonRpcMethods[Echo]);
        var route = Assert.Single(table.RestRoutes, r => r.Op.Kind == A2AOperation.Extension);
        Assert.Equal(("POST", "/ext/" + Echo), (route.Method, route.Route));
    }

    // ---------------------------------------------------------------- authorization

    [Fact]
    public async Task Extension_without_policy_is_denied()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho());

        var (rpcStatus, rpc) = await SendAsync(host, JsonRpc(Echo));
        Assert.Equal(HttpStatusCode.Forbidden, rpcStatus);
        Assert.Contains("PERMISSION_DENIED", rpc.GetProperty("error").GetRawText());
        Assert.False(rpc.TryGetProperty("result", out _));

        var (restStatus, rest) = await SendAsync(host, Rest(Echo));
        Assert.Equal(HttpStatusCode.Forbidden, restStatus);
        Assert.Equal("PERMISSION_DENIED", rest.GetProperty("error").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Extension_with_allow_runs_over_both_bindings()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(allow: true));

        var (rpcStatus, rpc) = await SendAsync(host, JsonRpc(Echo));
        Assert.Equal(HttpStatusCode.OK, rpcStatus);
        var result = rpc.GetProperty("result");
        Assert.Equal("hi", result.GetProperty("echo").GetProperty("say").GetString());
        Assert.Equal("alice", result.GetProperty("principal").GetString());
        Assert.Equal(nameof(A2AOperation.Extension), result.GetProperty("kind").GetString());
        Assert.False(result.GetProperty("policy").GetBoolean());

        var (restStatus, rest) = await SendAsync(host, Rest(Echo));
        Assert.Equal(HttpStatusCode.OK, restStatus);
        Assert.Equal("hi", rest.GetProperty("echo").GetProperty("say").GetString());
        Assert.Equal("alice", rest.GetProperty("principal").GetString());

        // Tenants: from the route on both bindings, and from a "tenant" property in the params.
        var (_, rpcTenant) = await SendAsync(host, JsonRpc(Echo, path: "/a2a/t1"));
        Assert.Equal("t1", rpcTenant.GetProperty("result").GetProperty("tenant").GetString());
        var (_, restTenant) = await SendAsync(host, Rest(Echo, prefix: "/a2a/t2"));
        Assert.Equal("t2", restTenant.GetProperty("tenant").GetString());
        var (_, bodyTenant) = await SendAsync(host, JsonRpc(Echo, """{"tenant":"t3"}"""));
        Assert.Equal("t3", bodyTenant.GetProperty("result").GetProperty("tenant").GetString());
        var (_, conflict) = await SendAsync(host, JsonRpc(Echo, """{"tenant":"t3"}""", path: "/a2a/t1"));
        Assert.True(conflict.TryGetProperty("error", out _));

        // An empty REST body is an empty object.
        var (emptyStatus, empty) = await SendAsync(host, Rest(Echo, body: ""));
        Assert.Equal(HttpStatusCode.OK, emptyStatus);
        Assert.Equal(JsonValueKind.Object, empty.GetProperty("echo").ValueKind);

        // Unauthenticated callers are still refused (endpoint authorization and the anonymous backstop).
        var (anonStatus, _) = await SendAsync(host, JsonRpc(Echo), user: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonStatus);
    }

    [Fact]
    public async Task Extension_with_policy_runs_when_policy_passes_and_is_denied_when_it_fails()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(policy: AdminPolicy));

        var (rpcStatus, rpc) = await SendAsync(host, JsonRpc(Echo), user: "admin");
        Assert.Equal(HttpStatusCode.OK, rpcStatus);
        Assert.True(rpc.GetProperty("result").GetProperty("policy").GetBoolean());
        var (restStatus, rest) = await SendAsync(host, Rest(Echo), user: "admin");
        Assert.Equal(HttpStatusCode.OK, restStatus);
        Assert.True(rest.GetProperty("policy").GetBoolean());

        var (deniedRpcStatus, deniedRpc) = await SendAsync(host, JsonRpc(Echo), user: "alice");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRpcStatus);
        Assert.Contains("PERMISSION_DENIED", deniedRpc.GetProperty("error").GetRawText());
        var (deniedRestStatus, deniedRest) = await SendAsync(host, Rest(Echo), user: "alice");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRestStatus);
        Assert.Equal("PERMISSION_DENIED", deniedRest.GetProperty("error").GetProperty("status").GetString());
    }

    /// <summary>Allows extension operations only when the ASP.NET Core policy was satisfied.</summary>
    private sealed class PolicyOnly : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.Extension
                || (operation.Name == Echo && call.Items.TryGetValue(A2AExtensionOperation.PolicySatisfiedItem, out var v) && v is true));
    }

    [Fact]
    public async Task Custom_handler_reads_policy_result()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(policy: AdminPolicy,
            more: s => s.AddA2AServer().AddAuthorizationHandler<PolicyOnly>()));
        var (status, _) = await SendAsync(host, Rest(Echo), user: "admin");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---------------------------------------------------------------- card, unknown methods

    [Fact]
    public async Task Extension_appears_in_card_extensions()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(allow: true,
            more: s => s.AddA2AServer().AddExtensionOperation("x.other", EchoUri, EchoHandler, allow: true)));
        using var resp = await host.Http().GetAsync(A2AProtocol.WellKnownAgentCardPath);
        var card = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var uris = card.GetProperty("capabilities").GetProperty("extensions").EnumerateArray()
            .Select(e => e.GetProperty("uri").GetString()).ToArray();
        Assert.Contains(TestAgentHost.OptionalExtension, uris); // the host's own entry stays
        Assert.Single(uris, u => u == EchoUri);                  // one entry per URI
        var echo = card.GetProperty("capabilities").GetProperty("extensions").EnumerateArray().Single(e => e.GetProperty("uri").GetString() == EchoUri);
        Assert.False(echo.TryGetProperty("required", out var required) && required.GetBoolean());
    }

    [Fact]
    public async Task Unknown_method_is_MethodNotFound()
    {
        await using var host = await TestAgentHost.StartAsync(services: WithEcho(allow: true));

        var (_, rpc) = await SendAsync(host, JsonRpc("x.missing"));
        Assert.Equal(-32601, rpc.GetProperty("error").GetProperty("code").GetInt32());

        // A method that differs only in case is unknown too (JSON-RPC method names are case-sensitive).
        var (_, upper) = await SendAsync(host, JsonRpc("X.ECHO"));
        Assert.Equal(-32601, upper.GetProperty("error").GetProperty("code").GetInt32());

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            host.Handler.InvokeExtensionAsync("x.missing", A2A.Json.A2AJson.EmptyObject, new ServerCallContext()));
        Assert.Equal(A2AErrorCode.MethodNotFound, ex.Code);

        // HTTP+JSON has no route for an unregistered extension.
        using var resp = await host.Http("alice").SendAsync(Rest("x.missing"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------------------------------------------------------------- pipeline

    private sealed class Recorder : IA2AOperationFilter
    {
        public ConcurrentQueue<A2AOperationDescriptor> Seen { get; } = new();

        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            Seen.Enqueue(context.Operation);
            context.Items["recorder"] = "was here";
            return next(context, request, cancellationToken);
        }
    }

    [Fact]
    public async Task Extension_runs_through_host_filters_and_telemetry()
    {
        var recorder = new Recorder();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == A2ATelemetry.ServerSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        string? seenItem = null;
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton(recorder);
            s.AddA2AServer().AddOperationFilter<Recorder>(A2AFilterStage.BeforeAuthorization)
                .AddExtensionOperation(Echo, EchoUri, (context, p, _) =>
                {
                    seenItem = context.Items.TryGetValue("recorder", out var v) ? v as string : null;
                    return ValueTask.FromResult(p);
                }, allow: true);
        });

        var (status, _) = await SendAsync(host, Rest(Echo));
        Assert.Equal(HttpStatusCode.OK, status);
        var op = Assert.Single(recorder.Seen);
        Assert.Equal(A2AOperation.Extension, op.Kind);
        Assert.Equal(Echo, op.Name);
        Assert.Equal("was here", seenItem); // the handler gets the context the filters saw

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!spans.Any(a => a.OperationName == Echo) && DateTime.UtcNow < until) await Task.Delay(10);
        var span = Assert.Single(spans, a => a.OperationName == Echo);
        Assert.Equal(Echo, span.GetTagItem(A2ATelemetry.Tags.Operation));
    }

    [Fact]
    public async Task Extension_without_host_filters_runs_through_telemetry_and_authorization()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == A2ATelemetry.ServerSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = await TestAgentHost.StartAsync(services: WithEcho());

        var (status, _) = await SendAsync(host, JsonRpc(Echo));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!spans.Any(a => a.OperationName == Echo) && DateTime.UtcNow < until) await Task.Delay(10);
        var span = Assert.Single(spans, a => a.OperationName == Echo);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public void Registration_rejects_bad_names_and_collisions()
    {
        var builder = new ServiceCollection().AddA2AServer();
        builder.AddExtensionOperation(Echo, EchoUri, EchoHandler);

        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation(Echo, EchoUri, EchoHandler));           // duplicate
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("X.Echo", EchoUri, EchoHandler));       // duplicate route (case)
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation(A2AProtocol.Methods.GetTask, EchoUri, EchoHandler)); // built-in
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("x/echo", EchoUri, EchoHandler));
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("x echo", EchoUri, EchoHandler));
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("x:echo", EchoUri, EchoHandler));
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("..", EchoUri, EchoHandler));
        Assert.ThrowsAny<ArgumentException>(() => builder.AddExtensionOperation("", EchoUri, EchoHandler));
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("x.rel", "relative/uri", EchoHandler));
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation("x.rel", "/rooted/path", EchoHandler));
        Assert.Throws<ArgumentNullException>(() => builder.AddExtensionOperation("x.null", EchoUri, null!));

        builder.AddExtensionOperation("x.ok-1_2", "https://example.com/ext/v1", EchoHandler);
        Assert.Equal(2, builder.Services.Count(d => d.ServiceType == typeof(A2AExtensionOperation)));
    }

    [Fact]
    public async Task Default_handler_allows_only_registrations_with_allow()
    {
        var services = new ServiceCollection();
        services.AddA2AServer()
            .AddExtensionOperation("x.open", EchoUri, EchoHandler, allow: true)
            .AddExtensionOperation("x.closed", EchoUri, EchoHandler);
        await using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<A2AAuthorizationHandler>();
        var call = new ServerCallContext();
        Assert.True(await handler.AuthorizeAsync(call, A2AOperationDescriptor.Extension("x.open"), A2A.Json.A2AJson.EmptyObject, default));
        Assert.False(await handler.AuthorizeAsync(call, A2AOperationDescriptor.Extension("x.closed"), A2A.Json.A2AJson.EmptyObject, default));
        Assert.False(await handler.AuthorizeAsync(call, A2AOperationDescriptor.Extension("x.unknown"), A2A.Json.A2AJson.EmptyObject, default));
        call.Items[A2AExtensionOperation.PolicySatisfiedItem] = true;
        Assert.True(await handler.AuthorizeAsync(call, A2AOperationDescriptor.Extension("x.closed"), A2A.Json.A2AJson.EmptyObject, default));
        Assert.True(await handler.AuthorizeAsync(new ServerCallContext(), A2AOperationDescriptor.For(A2AOperation.GetTask), new GetTaskRequest(), default));
    }

    // ---------------------------------------------------------------- gRPC

    /// <summary>
    /// The gRPC binding is exempt (its proto is fixed, spec D21): its service never consults the table or calls extension
    /// operations. A source check, because this test project does not reference the gRPC assembly.
    /// </summary>
    [Fact]
    public void Grpc_ignores_extensions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var grpc = Path.Combine(dir.FullName, "src", "A2A.Server.Grpc");
        var files = Directory.GetFiles(grpc, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(nameof(A2AOperationTable), text);
            Assert.DoesNotContain(nameof(A2AExtensionOperation), text);
            Assert.DoesNotContain(nameof(IA2ARequestHandler.InvokeExtensionAsync), text);
        }
    }
}
