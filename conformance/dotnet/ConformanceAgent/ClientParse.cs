// ACTS "tck-client-parse" behavior (a2a-itk docs/conformance/05_sdk-integration.md, section 7).
// The runner sends {operation, wire_payload} in a data part. We run that payload through this SDK's real client,
// over a fake HTTP handler that answers every request with the payload, and return what the client parsed.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using A2A.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;

namespace ConformanceAgent;

internal static class ClientParse
{
    public const string Behavior = "tck-client-parse";
    private static readonly Uri BaseUrl = new("http://127.0.0.1:1/");

    public static async Task RunAsync(Message message, TaskUpdater updater, CancellationToken ct)
    {
        var request = message.Parts.OfType<DataPart>()
            .Select(p => p.Data)
            .FirstOrDefault(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("operation", out var op) && op.ValueKind == JsonValueKind.String);
        if (request.ValueKind != JsonValueKind.Object)
        {
            await updater.FailAsync($"{Behavior} needs a data part with operation and wire_payload", ct);
            return;
        }

        var operation = request.GetProperty("operation").GetString()!;
        var payload = request.TryGetProperty("wire_payload", out var wire) ? wire.GetRawText() : "null";
        JsonNode parsed;
        try
        {
            parsed = await ParseAsync(operation, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            parsed = ErrorOf(ex, payload);
        }

        await updater.AddArtifactAsync([Part.FromData(JsonSerializer.SerializeToElement(parsed))], name: Behavior, cancellationToken: ct);
        await updater.CompleteAsync($"{operation} parsed", ct);
    }

    private static async Task<JsonNode> ParseAsync(string operation, string payload, CancellationToken ct)
    {
        using var http = new HttpClient(new FixedHandler(payload));
        var client = new A2AClient(new JsonRpcTransport(http, BaseUrl));
        switch (operation)
        {
            case "send_message":
                var sent = await client.SendMessageAsync(Message.FromUser("acts"), cancellationToken: ct);
                return Node(A2AJson.SerializeToElement(sent));
            case "get_task":
                return Node(A2AJson.SerializeToElement(await client.GetTaskAsync("acts", cancellationToken: ct)));
            case "get_agent_card":
            case "get_extended_agent_card" when !IsEnvelope(payload):
                // Cards are fetched over plain HTTP on every binding, so there is no envelope.
                var resolver = new A2ACardResolver(http, new A2ACardResolverOptions { RequireHttps = false });
                return Node(A2AJson.SerializeToElement(await resolver.GetAgentCardAsync(BaseUrl, ct)));
            case "get_extended_agent_card":
                return Node(A2AJson.SerializeToElement(await client.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest(), cancellationToken: ct)));
            default:
                throw new NotSupportedException($"unsupported client operation \"{operation}\"");
        }
    }

    private static JsonNode Node(JsonElement e) => JsonNode.Parse(e.GetRawText()) ?? new JsonObject();

    private static bool IsEnvelope(string payload) =>
        JsonNode.Parse(payload) is JsonObject o && (o.ContainsKey("jsonrpc") || o.ContainsKey("result") || o.ContainsKey("error"));

    /// <summary>The error the client raised. Uses the wire error when the payload had one, so the test checks what the client surfaced.</summary>
    private static JsonObject ErrorOf(Exception ex, string payload)
    {
        var body = new JsonObject { ["message"] = ex.Message };
        if (ex is A2AException a2a) body["code"] = (int)a2a.Code;
        try
        {
            if (JsonNode.Parse(payload) is JsonObject o && o["error"] is JsonObject wire)
            {
                body = (JsonObject)wire.DeepClone();
                if (ex is A2AException typed) body["code"] = (int)typed.Code;
            }
        }
        catch (JsonException)
        {
        }
        return new JsonObject { ["error"] = body };
    }

    /// <summary>Answers every request with the payload; copies the request's JSON-RPC id into the reply, as a real server would.</summary>
    private sealed class FixedHandler(string payload) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = payload;
            if (request.Content is not null)
            {
                try
                {
                    var sent = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
                    if (JsonNode.Parse(payload) is JsonObject reply && reply.ContainsKey("jsonrpc") && sent?["id"] is { } id)
                    {
                        reply["id"] = id.DeepClone();
                        body = reply.ToJsonString();
                    }
                }
                catch (JsonException)
                {
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
