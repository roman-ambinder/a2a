using System.Net;
using System.Net.Http.Headers;
using System.Text;
using A2A.Client;
using A2A.Client.Transports;

namespace A2A.Tests;

/// <summary>
/// Response bodies are read as UTF-8 bytes (streamed on HTTP+JSON success paths) yet decode exactly as the former text path
/// did: BOM, charset, invalid UTF-8, and the error-message wording all stay the same.
/// </summary>
public class ClientBodyTests
{
    private const string Task1 = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"}}""";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private sealed class Fixed(HttpStatusCode status, string? mediaType, byte[] body, string? charset = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            if (mediaType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = charset };
            return Task.FromResult(new HttpResponseMessage(status) { Content = content, ReasonPhrase = "Reason" });
        }
    }

    private static IA2AClient Client(string binding, HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var options = new A2AClientOptions { MaxRetries = 0 };
        return binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri("http://x/a2a"), options)
            : new HttpJsonTransport(http, new Uri("http://x/a2a"), options);
    }

    private static byte[] Body(string binding, Encoding encoding, string task, bool bom = false)
    {
        var json = binding == ProtocolBindings.JsonRpc ? $$"""{"jsonrpc":"2.0","id":"1","result":{{task}}}""" : task;
        var bytes = encoding.GetBytes(json);
        return bom ? encoding.GetPreamble().Concat(bytes).ToArray() : bytes;
    }

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Bom_charset_and_utf16_decode_as_before(string binding)
    {
        var withBom = await Client(binding, new Fixed(HttpStatusCode.OK, "application/json", Body(binding, new UTF8Encoding(true), Task1, bom: true)))
            .GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("t1", withBom.Id);

        var utf16 = await Client(binding, new Fixed(HttpStatusCode.OK, "application/json", Body(binding, Encoding.Unicode, Task1, bom: true)))
            .GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("t1", utf16.Id);

        var latin1 = await Client(binding, new Fixed(HttpStatusCode.OK, "application/json", Body(binding, Encoding.Latin1, Task1.Replace("\"t1\"", "\"té\"")), "iso-8859-1"))
            .GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("té", latin1.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Invalid_utf8_is_replaced_as_the_text_form_did(string binding)
    {
        var body = Body(binding, Encoding.UTF8, Task1.Replace("\"t1\"", "\"t\u0001\""));
        body[Array.IndexOf(body, (byte)1)] = 0xFF;
        var task = await Client(binding, new Fixed(HttpStatusCode.OK, "application/json", body)).GetTaskAsync(new GetTaskRequest { Id = "t1" });
        Assert.Equal("t�", task.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Malformed_result_names_the_position_within_the_result(string binding)
    {
        var json = binding == ProtocolBindings.JsonRpc ? "{\"jsonrpc\":\"2.0\",\n\"id\":\"1\",\n\"result\":   {\n\"id\": 5}}" : "\n {\"id\": 5}";
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Client(binding, new Fixed(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(json))).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.EndsWith("Path: $.id | LineNumber: 1 | BytePositionInLine: " + (binding == ProtocolBindings.JsonRpc ? "7." : "9."), ex.Message);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"1.0","id":"1","result":{}}""")]
    [InlineData("""{"id":"1","result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"1"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"1","error":{"code":"x","message":"m"}}""")]
    [InlineData("[1]")]
    [InlineData("{")]
    [InlineData("")]
    public async Task Json_rpc_bodies_that_are_not_responses_are_reported_as_before(string json)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Client(ProtocolBindings.JsonRpc, new Fixed(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(json))).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal("GetTask: response is not a JSON-RPC response.", ex.Message);

        var http = await Assert.ThrowsAsync<A2AException>(() =>
            Client(ProtocolBindings.JsonRpc, new Fixed(HttpStatusCode.BadGateway, "application/json", Encoding.UTF8.GetBytes(json))).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal(json.Trim().Length == 0 ? "HTTP 502 Reason" : $"HTTP 502 Reason: {json}", http.Message);
    }

    [Fact]
    public async Task Rest_error_bodies_are_parsed_from_bytes_and_text_is_kept_for_others()
    {
        var status = Bom.Concat(Encoding.UTF8.GetBytes("""{"error":{"code":400,"message":"bad"}}""")).ToArray();
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Client(ProtocolBindings.HttpJson, new Fixed(HttpStatusCode.BadRequest, "application/json", status)).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal((A2AErrorCode.InvalidParams, "bad", 400), (ex.Code, ex.Message, ex.HttpStatus));

        var text = await Assert.ThrowsAsync<A2AException>(() =>
            Client(ProtocolBindings.HttpJson, new Fixed(HttpStatusCode.BadGateway, "text/html", Encoding.UTF8.GetBytes("<p>gateway é</p>"))).GetTaskAsync(new GetTaskRequest { Id = "t1" }));
        Assert.Equal("HTTP 502 Reason: <p>gateway é</p>", text.Message);
    }

    [Fact]
    public async Task Card_is_parsed_from_bytes_and_raw_text_keeps_its_decoding()
    {
        var card = A2A.Json.A2AJson.Serialize(TestAgentHost.DefaultCard());
        var resolver = new A2ACardResolver(new HttpClient(new Fixed(HttpStatusCode.OK, "application/json", Bom.Concat(Encoding.UTF8.GetBytes(card)).ToArray())),
            new A2ACardResolverOptions { RequireHttps = false });
        var resolved = await resolver.ResolveAsync(new Uri("http://x/"));
        Assert.Equal(card, resolved.RawJson);
        Assert.Equal(TestAgentHost.DefaultCard().Name, resolved.Card.Name);
    }
}
