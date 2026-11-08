using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat.Tests;

/// <summary>One request as the fake server saw it (copied before the client disposes it).</summary>
public sealed record SeenRequest(HttpMethod Method, string PathAndQuery, Dictionary<string, string> Headers, string Body)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A fake v0.3 agent: records every request and answers with whatever the test returns.</summary>
public sealed class FakeV03Server(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Content is not null) foreach (var h in request.Content.Headers) headers["content:" + h.Key] = string.Join(",", h.Value);
        var seen = new SeenRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body);
        lock (Requests) Requests.Add(seen);
        var response = respond(seen);
        response.RequestMessage = request;
        return response;
    }

    public SeenRequest Last => Requests[^1];

    public HttpClient Client() => new(this);

    // ---- responses ----

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) => Json(node.ToJsonString(), status);

    /// <summary>A JSON-RPC success response.</summary>
    public static HttpResponseMessage Rpc(JsonNode? result) =>
        Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["result"] = result }.ToJsonString());

    public static HttpResponseMessage RpcError(int code, string message, HttpStatusCode status = HttpStatusCode.OK) =>
        Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), status);

    /// <summary>An SSE response with one <c>data:</c> frame per item.</summary>
    public static HttpResponseMessage Sse(params string[] data) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(data.Select(d => $"data: {d}\n\n")), Encoding.UTF8, "text/event-stream") };

    /// <summary>An SSE response that sends the frames and then keeps the connection open until the client cancels.</summary>
    public static HttpResponseMessage SseThenHang(params string[] data)
    {
        var content = new StreamContent(new HangingStream(Encoding.UTF8.GetBytes(string.Concat(data.Select(d => $"data: {d}\n\n")))));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static JsonObject RpcEvent(JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = "s1", ["result"] = result };
}

/// <summary>Serves some bytes, then blocks on read until cancelled (an agent that does not close the connection).</summary>
internal sealed class HangingStream(byte[] bytes) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < bytes.Length)
        {
            var n = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

/// <summary>Records what a real in-process server receives, then passes the request on.</summary>
public sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add(new SeenRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body));
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Builders for v0.3 cards and messages used by the client tests.</summary>
internal static class ClientData
{
    public static AgentCard CardWith(params AgentInterface[] interfaces) => new()
    {
        Name = "Card", Description = "d", Version = "1",
        SupportedInterfaces = [.. interfaces],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true, ExtendedAgentCard = true },
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "d", Tags = ["t"] }],
    };

    public static AgentInterface Itf(string binding, string url, string version, string? tenant = null) =>
        new() { ProtocolBinding = binding, Url = url, ProtocolVersion = version, Tenant = tenant };

    public static SendMessageRequest Send(string text, bool? returnImmediately = null) => new()
    {
        Message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText(text)] },
        Configuration = returnImmediately is null ? null : new SendMessageConfiguration { ReturnImmediately = returnImmediately.Value },
    };

    public static string TaskJson(string state = "working", string id = "t1") =>
        new JsonObject
        {
            ["kind"] = "task", ["id"] = id, ["contextId"] = "c1",
            ["status"] = new JsonObject { ["state"] = state },
        }.ToJsonString();

    public static string RestTaskJson(string state = "TASK_STATE_WORKING", string id = "t1") =>
        new JsonObject { ["id"] = id, ["contextId"] = "c1", ["status"] = new JsonObject { ["state"] = state } }.ToJsonString();

    public static string StatusUpdateJson(string state, bool final, string id = "t1") =>
        new JsonObject
        {
            ["kind"] = "status-update", ["taskId"] = id, ["contextId"] = "c1",
            ["status"] = new JsonObject { ["state"] = state }, ["final"] = final,
        }.ToJsonString();

    public static string RestStatusUpdateJson(string state, bool final, string id = "t1") =>
        new JsonObject
        {
            ["statusUpdate"] = new JsonObject
            {
                ["taskId"] = id, ["contextId"] = "c1", ["status"] = new JsonObject { ["state"] = state }, ["final"] = final,
            },
        }.ToJsonString();
}
