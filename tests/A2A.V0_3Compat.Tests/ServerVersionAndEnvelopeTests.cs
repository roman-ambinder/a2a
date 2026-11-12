using System.Net;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// The v0.3 surface serves only v0.3 (spec v1.0.1 §3.6.2: an absent version means 0.3, an unsupported one is
/// <c>VersionNotSupportedError</c>), and JSON-RPC requests must carry <c>"jsonrpc": "2.0"</c> on both surfaces.
/// </summary>
public class ServerVersionAndEnvelopeTests
{
    private const int TaskNotFound = -32001;
    private const int InvalidRequest = -32600;
    private const int VersionNotSupported = -32009;

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostRawAsync(HttpClient http, string json, string? version = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = V03Host.Json(json) };
        if (version is not null) request.Headers.Add("A2A-Version", version);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static int Code(JsonObject body) => body["error"]!["code"]!.GetValue<int>();

    [Theory]
    [InlineData(null)]
    [InlineData("0.3")]
    [InlineData("0.3.0")]
    [InlineData(" 0.3.1 ")]
    public async Task JsonRpc_accepts_no_version_and_0_3(string? version)
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await V03Host.RpcAsync(host.Http(), "tasks/get", new JsonObject { ["id"] = "nope" }, version: version);
        Assert.Equal(TaskNotFound, Code(body)); // served: the task lookup ran
    }

    [Theory]
    [InlineData("0.2")]
    [InlineData("0.2.6")]
    [InlineData("0.1")]
    [InlineData("0.4")]
    [InlineData("abc")]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public async Task JsonRpc_refuses_every_other_version(string version)
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await V03Host.RpcAsync(host.Http(), "tasks/get", new JsonObject { ["id"] = "nope" }, version: version);
        Assert.Equal(VersionNotSupported, Code(body));
        Assert.Contains(version, body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.2", false)]
    [InlineData("abc", false)]
    [InlineData("0.3", true)]
    public async Task Rest_routes_check_the_version_header_and_query(string version, bool served)
    {
        await using var host = await V03Host.StartAsync();
        foreach (var path in new[] { "/a2a/v1/tasks/nope", $"/a2a/v1/tasks/nope?A2A-Version={version}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (!path.Contains('?', StringComparison.Ordinal)) request.Headers.Add("A2A-Version", version);
            using var response = await host.Http().SendAsync(request);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(served ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(served ? TaskNotFound : VersionNotSupported, body["code"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("""{"id":1,"method":"tasks/get","params":{"id":"nope"}}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"tasks/get","params":{"id":"nope"}}""")]
    public async Task A_v03_request_without_jsonrpc_2_0_is_InvalidRequest(string json)
    {
        await using var host = await V03Host.StartAsync();
        var (_, body) = await PostRawAsync(host.Http(), json);
        Assert.Equal(InvalidRequest, Code(body));
    }

    [Fact]
    public async Task A_v1_request_without_jsonrpc_is_InvalidRequest()
    {
        await using var host = await V03Host.StartAsync();
        var (_, missing) = await PostRawAsync(host.Http(), """{"id":1,"method":"GetTask","params":{"id":"nope"}}""", "1.0");
        Assert.Equal(InvalidRequest, Code(missing));
        Assert.Equal(1, missing["id"]!.GetValue<int>()); // the id still comes back

        var (_, ok) = await PostRawAsync(host.Http(), """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"nope"}}""", "1.0");
        Assert.Equal(TaskNotFound, Code(ok));
    }
}
