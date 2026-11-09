using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Client;
using A2A.Server;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.Operations;

/// <summary>The client helper for custom operations, over JSON-RPC and HTTP+JSON, against a real test host.</summary>
public sealed partial class ClientCustomOperationTests
{
    private const string Method = "x.echo";
    private const string Uri = "urn:test:x-client-echo";

    /// <summary>Echoes the params with what the server saw: the tenant, and how often the extension was requested.</summary>
    private static ValueTask<JsonElement> EchoHandler(A2AOperationContext context, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("fail", out _))
            throw A2AException.InvalidParams("asked to fail");
        var json = $$$"""{"echo":{{{parameters.GetRawText()}}},"tenant":"{{{context.Call.Tenant}}}","requested":{{{context.Call.RequestedExtensions.Count(e => e == Uri)}}}}""";
        return ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static Task<TestAgentHost> StartAsync() =>
        TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddExtensionOperation(Method, Uri, EchoHandler, allow: true));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_helper_round_trips_and_adds_the_extension_once(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding);
        var options = new A2ARequestOptions();

        var first = await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"hi"}"""), options);
        await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"again"}"""), options);

        Assert.Equal("hi", first.GetProperty("echo").GetProperty("say").GetString());
        Assert.Equal(1, first.GetProperty("requested").GetInt32());
        Assert.Equal([Uri], options.Extensions);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Undefined_params_are_sent_as_the_empty_object(string binding)
    {
        await using var host = await StartAsync();

        var result = await host.Client(binding).InvokeExtensionOperationAsync(Method, Uri, default);

        Assert.Equal(JsonValueKind.Object, result.GetProperty("echo").ValueKind);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_tenant_of_the_options_is_used(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding, options: new A2AClientOptions { Tenant = "t1" });

        var result = await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"hi"}"""));

        Assert.Equal("t1", result.GetProperty("tenant").GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_tenant_of_the_client_applies_when_the_transport_has_other_options(string binding)
    {
        await using var host = await StartAsync();
        var client = new A2AClient(host.Client(binding), new A2AClientOptions { Tenant = "t2" });

        var result = await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"hi"}"""));

        Assert.Equal("t2", result.GetProperty("tenant").GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_tenant_in_the_params_wins_over_the_options(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding, options: new A2AClientOptions { Tenant = "t1" });

        var result = await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"tenant":"t2"}"""));

        Assert.Equal("t2", result.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task Params_that_are_not_an_object_with_a_tenant_are_InvalidParams_on_JSON_RPC_and_work_on_HTTP_JSON()
    {
        await using var host = await StartAsync();
        var options = new A2AClientOptions { Tenant = "t1" };

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            host.Client(ProtocolBindings.JsonRpc, options: options).InvokeExtensionOperationAsync(Method, Uri, Json("[1,2]")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);

        // The tenant is in the path on HTTP+JSON, so the body can be any JSON value.
        var result = await host.Client(ProtocolBindings.HttpJson, options: options).InvokeExtensionOperationAsync(Method, Uri, Json("[1,2]"));
        Assert.Equal("t1", result.GetProperty("tenant").GetString());
        Assert.Equal(2, result.GetProperty("echo").GetArrayLength());

        // Without a tenant, non-object params are sent as they are.
        var plain = await host.Client(ProtocolBindings.JsonRpc).InvokeExtensionOperationAsync(Method, Uri, Json("[1,2]"));
        Assert.Equal(2, plain.GetProperty("echo").GetArrayLength());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_reserved_name_is_refused_before_anything_is_sent(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding);

        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeExtensionOperationAsync("rpc.x", Uri, Json("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeExtensionOperationAsync("SendMessage", Uri, Json("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeExtensionOperationAsync("", Uri, Json("{}")));
        // The same through the interface, without the helper.
        await Assert.ThrowsAsync<ArgumentException>(() => ((IA2AExtensionOperationClient)client).InvokeExtensionOperationAsync("GetTask", Json("{}")));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Without_the_extension_the_server_refuses_with_ExtensionSupportRequired(string binding)
    {
        await using var host = await StartAsync();
        var client = (IA2AExtensionOperationClient)host.Client(binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.InvokeExtensionOperationAsync(Method, Json("{}")));

        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_server_error_is_an_A2AException_with_its_code(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding);

        var failed = await Assert.ThrowsAsync<A2AException>(() => client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"fail":true}""")));
        Assert.Equal(A2AErrorCode.InvalidParams, failed.Code);
        Assert.Contains("asked to fail", failed.Message);

        var missing = await Assert.ThrowsAnyAsync<A2AException>(() => client.InvokeExtensionOperationAsync("x.missing", Uri, Json("{}")));
        Assert.Contains(missing.Code, new[] { A2AErrorCode.MethodNotFound, A2AErrorCode.TaskNotFound });
    }

    public sealed record Sum(int A, int B);
    public sealed record Total(int Value);
    public sealed record Strict(int Echo);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Sum))]
    [JsonSerializable(typeof(Total))]
    [JsonSerializable(typeof(Strict))]
    private sealed partial class ClientTypedJson : JsonSerializerContext;

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_typed_helper_round_trips_with_the_typed_server_overload(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddExtensionOperation("x.sum", Uri,
            ClientTypedJson.Default.Sum, ClientTypedJson.Default.Total, (_, request, _) => ValueTask.FromResult(new Total(request.A + request.B)), allow: true));

        var total = await host.Client(binding).InvokeExtensionOperationAsync("x.sum", Uri, new Sum(2, 3), ClientTypedJson.Default.Sum, ClientTypedJson.Default.Total);

        Assert.Equal(5, total.Value);
    }

    [Fact]
    public async Task A_result_of_the_wrong_shape_is_InvalidAgentResponse()
    {
        await using var host = await StartAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc)
            .InvokeExtensionOperationAsync(Method, Uri, new Sum(1, 2), ClientTypedJson.Default.Sum, ClientTypedJson.Default.Strict));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    /// <summary>An <see cref="IA2AClient"/> that does not implement <see cref="IA2AExtensionOperationClient"/>.</summary>
    public class PlainClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_client_whose_transport_cannot_call_custom_operations_throws_UnsupportedOperation()
    {
        var transport = DispatchProxy.Create<IA2AClient, PlainClientProxy>();
        Assert.False(transport is IA2AExtensionOperationClient);

        var viaClient = await Assert.ThrowsAsync<A2AException>(() => new A2AClient(transport).InvokeExtensionOperationAsync(Method, Json("{}")));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, viaClient.Code);

        var viaHelper = await Assert.ThrowsAsync<A2AException>(() => transport.InvokeExtensionOperationAsync(Method, Uri, Json("{}")));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, viaHelper.Code);
        await Task.CompletedTask;
    }
}
