using A2A.Client;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using A2A.Grpc.Extensions.Protos;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Grpc.Tests;

/// <summary>Extension operations over gRPC: <c>a2a.extensions.v1.A2AExtensionService</c> (spec D23).</summary>
public class GrpcExtensionOperationTests
{
    private const string Echo = "x.echo";
    private const string EchoUri = "urn:test:x-echo";
    private const string AdminPolicy = "ext-admin";

    /// <summary>Returns its params with the caller's principal and the operation kind added.</summary>
    private static ValueTask<JsonElement> EchoHandler(A2AOperationContext context, JsonElement parameters, CancellationToken cancellationToken)
    {
        var json = $$"""{"echo":{{parameters.GetRawText()}},"principal":"{{context.Call.Principal}}","tenant":"{{context.Call.Tenant}}","kind":"{{context.Operation.Kind}}","policy":{{(context.Items.TryGetValue(A2AExtensionOperation.PolicySatisfiedItem, out var p) && p is true ? "true" : "false")}}}""";
        return ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static Task<GrpcHost> StartAsync(string? policy = null, bool allow = false, Action<A2AServerBuilder>? more = null,
        Action<A2A.Server.A2AServerOptions>? configure = null) =>
        GrpcHost.StartAsync(configure, s => s.AddAuthorization(o => o.AddPolicy(AdminPolicy, p => p.RequireClaim("sub", "admin"))),
            mapExtensions: true, server: b =>
            {
                b.AddExtensionOperation(Echo, EchoUri, EchoHandler, policy, allow);
                more?.Invoke(b);
            });

    /// <summary>Version metadata, and (unless <paramref name="withExtension"/> is false) the header that activates the echo extension.</summary>
    private static Metadata Version(bool withExtension = true, params (string Key, string Value)[] more)
    {
        var m = new Metadata { { "a2a-version", "1.0" } };
        if (withExtension) m.Add("a2a-extensions", EchoUri);
        foreach (var (k, v) in more) m.Add(k, v);
        return m;
    }

    private static ExtensionOperationRequest Request(string operation, string? payload) => new()
    {
        OperationId = operation,
        Payload = payload is null ? ByteString.Empty : ByteString.CopyFromUtf8(payload),
    };

    private static JsonElement Json(ExtensionOperationResponse response) => JsonDocument.Parse(response.Payload.Memory).RootElement.Clone();

    private static A2AErrorCode Code(RpcException ex) => GrpcErrors.FromRpcException(ex).Code;

    [Fact]
    public async Task Allowed_operation_round_trips_its_params()
    {
        await using var host = await StartAsync(allow: true);
        var response = await host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, """{"say":"héllo","n":[1,2]}"""), Version()).ResponseAsync;
        var json = Json(response);
        Assert.Equal("héllo", json.GetProperty("echo").GetProperty("say").GetString());
        Assert.Equal(2, json.GetProperty("echo").GetProperty("n").GetArrayLength());
        Assert.Equal("alice", json.GetProperty("principal").GetString());
        Assert.Equal("Extension", json.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_call_without_the_extension_metadata_is_ExtensionSupportRequired_and_the_handler_does_not_run()
    {
        var calls = 0;
        await using var host = await StartAsync(allow: true, more: b => b.AddExtensionOperation("x.count", "urn:test:x-count",
            (_, p, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(p); }, allow: true));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            host.RawExtensions().InvokeExtensionOperationAsync(Request("x.count", "{}"), Version(withExtension: false)).ResponseAsync);
        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, Code(ex));

        // Another extension in the metadata does not activate it.
        var other = await Assert.ThrowsAsync<RpcException>(() =>
            host.RawExtensions().InvokeExtensionOperationAsync(Request("x.count", "{}"), Version(false, ("a2a-extensions", GrpcHost.Extension))).ResponseAsync);
        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, Code(other));
        Assert.Equal(0, calls);

        var metadata = Version(false, ("a2a-extensions", "urn:test:x-count"));
        await host.RawExtensions().InvokeExtensionOperationAsync(Request("x.count", "{}"), metadata).ResponseAsync;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Empty_payload_is_the_empty_object()
    {
        await using var host = await StartAsync(allow: true);
        var response = await host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, null), Version()).ResponseAsync;
        var echo = Json(response).GetProperty("echo");
        Assert.Equal(JsonValueKind.Object, echo.ValueKind);
        Assert.Empty(echo.EnumerateObject());
    }

    [Fact]
    public async Task Unknown_operation_is_MethodNotFound()
    {
        await using var host = await StartAsync(allow: true);
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request("x.nope", "{}"), Version()).ResponseAsync);
        Assert.Equal(A2AErrorCode.MethodNotFound, Code(ex));
        // Names are ordinal: a different case is another name.
        var cased = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request("X.ECHO", "{}"), Version()).ResponseAsync);
        Assert.Equal(A2AErrorCode.MethodNotFound, Code(cased));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"a\":1} trailing")]
    public async Task Invalid_json_payload_is_InvalidParams(string payload)
    {
        await using var host = await StartAsync(allow: true);
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, payload), Version()).ResponseAsync);
        Assert.Equal(A2AErrorCode.InvalidParams, Code(ex));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Payload_nested_deeper_than_the_limit_is_InvalidParams()
    {
        await using var host = await StartAsync(allow: true, configure: o => o.Limits.MaxJsonDepth = 8);
        var deep = new StringBuilder().Insert(0, "[", 20).Append(']', 20).ToString();
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, deep), Version()).ResponseAsync);
        Assert.Equal(A2AErrorCode.InvalidParams, Code(ex));
        var shallow = await host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, "[[1]]"), Version()).ResponseAsync;
        Assert.Equal(JsonValueKind.Array, Json(shallow).GetProperty("echo").ValueKind);
    }

    [Fact]
    public async Task Operation_without_allow_or_policy_is_denied()
    {
        await using var host = await StartAsync();
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal(A2AErrorCode.PermissionDenied, Code(ex));
    }

    [Fact]
    public async Task Policy_decides_the_call()
    {
        await using var host = await StartAsync(policy: AdminPolicy);
        var ok = await host.RawExtensions("admin").InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync;
        Assert.True(Json(ok).GetProperty("policy").GetBoolean());
        var denied = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions("alice").InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task Missing_version_metadata_is_rejected()
    {
        await using var host = await StartAsync(allow: true);
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, "{}")).ResponseAsync);
        Assert.Equal(A2AErrorCode.VersionNotSupported, Code(ex));
    }

    [Fact]
    public async Task Activated_extensions_are_echoed_in_metadata_and_in_trailers_on_errors()
    {
        await using var host = await StartAsync(allow: true);
        var headers = Version(false, ("a2a-extensions", $"{GrpcHost.Extension},urn:other,{EchoUri}"));

        using var ok = host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, "{}"), headers);
        await ok.ResponseAsync;
        Assert.Equal($"{GrpcHost.Extension},{EchoUri}", (await ok.ResponseHeadersAsync).GetValue("a2a-extensions"));

        using var failed = host.RawExtensions().InvokeExtensionOperationAsync(Request("x.nope", "{}"), headers);
        await Assert.ThrowsAsync<RpcException>(() => failed.ResponseAsync);
        Assert.Equal($"{GrpcHost.Extension},{EchoUri}", failed.GetTrailers().GetValue("a2a-extensions"));
    }

    [Fact]
    public async Task Operation_filters_see_the_extension_descriptor()
    {
        var seen = new ConcurrentQueue<A2AOperationDescriptor>();
        await using var host = await StartAsync(allow: true, more: b =>
        {
            b.Services.AddSingleton(seen);
            b.AddOperationFilter<RecordingFilter>();
        });
        await host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync;
        var op = Assert.Single(seen);
        Assert.Equal(A2AOperation.Extension, op.Kind);
        Assert.Equal(Echo, op.Name);
        Assert.Equal(A2AOperationDescriptor.Extension(Echo), op);
    }

    [Fact]
    public async Task Card_still_lists_the_extension_uri()
    {
        await using var host = await StartAsync(allow: true);
        using var http = new HttpClient(host.Handler()) { BaseAddress = host.BaseAddress };
        var card = JsonDocument.Parse(await http.GetStringAsync(A2AProtocol.WellKnownAgentCardPath)).RootElement;
        var uris = card.GetProperty("capabilities").GetProperty("extensions").EnumerateArray().Select(e => e.GetProperty("uri").GetString()).ToArray();
        Assert.Contains(EchoUri, uris);
        Assert.Contains(GrpcHost.Extension, uris);
    }

    [Fact]
    public async Task Streaming_operation_is_unimplemented()
    {
        await using var host = await StartAsync(allow: true);
        using var call = host.RawExtensions().InvokeStreamingExtensionOperation(Request(Echo, "{}"), Version());
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(default));
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task The_service_does_not_replace_the_a2a_service_and_the_a2a_service_still_works()
    {
        await using var host = await StartAsync(allow: true);
        Assert.IsType<MessageResponse>(await host.Client().SendMessageAsync("hello"));
    }

    [Fact]
    public async Task Extension_service_has_its_own_authorization()
    {
        // Unauthenticated callers are refused by the extension endpoints' own RequireAuthorization().
        await using var host = await StartAsync(allow: true);
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions(user: null).InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync);
        Assert.Contains(ex.StatusCode, new[] { StatusCode.Unauthenticated, StatusCode.PermissionDenied });
    }

    [Fact]
    public async Task Startup_fails_when_the_extension_service_has_no_authorization_policy()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GrpcHost.StartAsync(mapExtensions: true, requireExtensionsAuth: false));
        Assert.Contains("authorization policy", ex.Message);
    }

    [Fact]
    public async Task Anonymous_mode_serves_the_extension_service_without_endpoint_authorization()
    {
        await using var host = await GrpcHost.StartAsync(o => o.AllowAnonymous = true, mapExtensions: true, requireAuth: false, requireExtensionsAuth: false,
            server: b => b.AddExtensionOperation(Echo, EchoUri, EchoHandler, allow: true));
        var response = await host.RawExtensions(user: null).InvokeExtensionOperationAsync(Request(Echo, "{}"), Version()).ResponseAsync;
        Assert.Equal("Extension", Json(response).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Request_larger_than_the_body_limit_is_rejected()
    {
        await using var host = await StartAsync(allow: true, configure: o => o.Limits.MaxRequestBodyBytes = 1024);
        var big = "{\"s\":\"" + new string('x', 4096) + "\"}";
        var ex = await Assert.ThrowsAsync<RpcException>(() => host.RawExtensions().InvokeExtensionOperationAsync(Request(Echo, big), Version()).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    private sealed class RecordingFilter(ConcurrentQueue<A2AOperationDescriptor> seen) : IA2AOperationFilter
    {
        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            seen.Enqueue(context.Operation);
            return next(context, request, cancellationToken);
        }
    }
}
