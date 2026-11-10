using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Client;
using A2A.Server.Pipeline;

namespace A2A.Grpc.Tests;

/// <summary>The client helper for custom operations over gRPC, against a real test host that maps the extension service.</summary>
public sealed partial class GrpcClientCustomOperationTests
{
    private const string Method = "x.echo";
    private const string Uri = "urn:test:x-grpc-client-echo";

    private static ValueTask<JsonElement> EchoHandler(A2AOperationContext context, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("fail", out _))
            throw A2AException.InvalidParams("asked to fail");
        var json = $$$"""{"echo":{{{parameters.GetRawText()}}},"tenant":"{{{context.Call.Tenant}}}","requested":{{{context.Call.RequestedExtensions.Count(e => e == Uri)}}}}""";
        return ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static Task<GrpcHost> StartAsync() =>
        GrpcHost.StartAsync(mapExtensions: true, server: b => b.AddExtensionOperation(Method, Uri, EchoHandler, allow: true));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task The_helper_round_trips_and_adds_the_extension_once()
    {
        await using var host = await StartAsync();
        var client = host.Client();
        var options = new A2ARequestOptions();

        var first = await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"héllo"}"""), options);
        await client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"again"}"""), options);

        Assert.Equal("héllo", first.GetProperty("echo").GetProperty("say").GetString());
        Assert.Equal(1, first.GetProperty("requested").GetInt32());
        Assert.Equal([Uri], options.Extensions);
    }

    [Fact]
    public async Task Undefined_params_are_sent_as_the_empty_object()
    {
        await using var host = await StartAsync();

        var result = await host.Client().InvokeExtensionOperationAsync(Method, Uri, default);

        Assert.Equal(JsonValueKind.Object, result.GetProperty("echo").ValueKind);
    }

    [Fact]
    public async Task The_tenant_of_the_options_is_added_to_the_params()
    {
        await using var host = await StartAsync();

        var result = await host.Client(options: new A2AClientOptions { Tenant = "t1" }).InvokeExtensionOperationAsync(Method, Uri, Json("""{"say":"hi"}"""));

        Assert.Equal("t1", result.GetProperty("tenant").GetString());
        Assert.Equal("t1", result.GetProperty("echo").GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task The_tenant_in_the_params_wins_over_the_options()
    {
        await using var host = await StartAsync();

        var result = await host.Client(options: new A2AClientOptions { Tenant = "t1" }).InvokeExtensionOperationAsync(Method, Uri, Json("""{"tenant":"t2"}"""));

        Assert.Equal("t2", result.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task Params_that_are_not_an_object_with_a_tenant_are_InvalidParams()
    {
        await using var host = await StartAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            host.Client(options: new A2AClientOptions { Tenant = "t1" }).InvokeExtensionOperationAsync(Method, Uri, Json("[1,2]")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);

        var plain = await host.Client().InvokeExtensionOperationAsync(Method, Uri, Json("[1,2]"));
        Assert.Equal(2, plain.GetProperty("echo").GetArrayLength());
    }

    [Fact]
    public async Task A_reserved_name_is_refused_before_anything_is_sent()
    {
        await using var host = await StartAsync();
        var client = host.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeExtensionOperationAsync("rpc.x", Uri, Json("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeExtensionOperationAsync("GetTask", Uri, Json("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => ((IA2AExtensionOperationClient)client).InvokeExtensionOperationAsync("rpc.y", Json("{}")));
    }

    [Fact]
    public async Task Without_the_extension_the_server_refuses_with_ExtensionSupportRequired()
    {
        await using var host = await StartAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => ((IA2AExtensionOperationClient)host.Client()).InvokeExtensionOperationAsync(Method, Json("{}")));

        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, ex.Code);
    }

    [Fact]
    public async Task A_server_error_is_an_A2AException_with_its_code()
    {
        await using var host = await StartAsync();
        var client = host.Client();

        var failed = await Assert.ThrowsAsync<A2AException>(() => client.InvokeExtensionOperationAsync(Method, Uri, Json("""{"fail":true}""")));
        Assert.Equal(A2AErrorCode.InvalidParams, failed.Code);

        var missing = await Assert.ThrowsAsync<A2AException>(() => client.InvokeExtensionOperationAsync("x.missing", Uri, Json("{}")));
        Assert.Equal(A2AErrorCode.MethodNotFound, missing.Code);
    }

    public sealed record Sum(int A, int B);
    public sealed record Total(int Value);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Sum))]
    [JsonSerializable(typeof(Total))]
    private sealed partial class TypedJson : JsonSerializerContext;

    [Fact]
    public async Task The_typed_helper_round_trips_with_the_typed_server_overload()
    {
        await using var host = await GrpcHost.StartAsync(mapExtensions: true, server: b => b.AddExtensionOperation("x.sum", Uri,
            TypedJson.Default.Sum, TypedJson.Default.Total, (_, request, _) => ValueTask.FromResult(new Total(request.A + request.B)), allow: true));

        var total = await host.Client().InvokeExtensionOperationAsync("x.sum", Uri, new Sum(2, 3), TypedJson.Default.Sum, TypedJson.Default.Total);

        Assert.Equal(5, total.Value);
    }
}
