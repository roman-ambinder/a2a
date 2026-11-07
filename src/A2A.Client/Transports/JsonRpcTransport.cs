using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client.Sse;
using A2A.Json;
using A2A.JsonRpc;

namespace A2A.Client.Transports;

/// <summary>JSON-RPC 2.0 over HTTP binding (spec §9).</summary>
public sealed class JsonRpcTransport : HttpTransportBase, IA2AClient
{
    private readonly Uri _endpoint;

    public JsonRpcTransport(HttpClient httpClient, Uri endpoint, A2AClientOptions? options = null) : base(httpClient, options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<SendMessageRequest, SendMessageResponse>(A2AProtocol.Methods.SendMessage, request, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskRequest, AgentTask>(A2AProtocol.Methods.GetTask, request, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTasksRequest, ListTasksResponse>(A2AProtocol.Methods.ListTasks, request, cancellationToken);

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<CancelTaskRequest, AgentTask>(A2AProtocol.Methods.CancelTask, request, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default) =>
        InvokeAsync<TaskPushNotificationConfig, TaskPushNotificationConfig>(A2AProtocol.Methods.CreateTaskPushNotificationConfig, config, cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskPushNotificationConfigRequest, TaskPushNotificationConfig>(A2AProtocol.Methods.GetTaskPushNotificationConfig, request, cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsResponse>(A2AProtocol.Methods.ListTaskPushNotificationConfigs, request, cancellationToken);

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        await InvokeRawAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, request, cancellationToken).ConfigureAwait(false);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetExtendedAgentCardRequest, AgentCard>(A2AProtocol.Methods.GetExtendedAgentCard, request, cancellationToken);

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, CancellationToken ct)
    {
        var result = await InvokeRawAsync(method, parameters, ct).ConfigureAwait(false);
        if (result is not { } r) throw ProtocolError($"{method} response has no result.");
        return Parse<TResult>(r, method + " result");
    }

    private async Task<JsonElement?> InvokeRawAsync<TParams>(string method, TParams parameters, CancellationToken ct)
    {
        using var request = BuildRequest(method, parameters, streaming: false);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var rpc = TryParseResponse(body);
        if (rpc is null)
        {
            if (!response.IsSuccessStatusCode) throw HttpError(response, body);
            throw ProtocolError($"{method}: response is not a JSON-RPC response.");
        }
        if (rpc.Error is not null) throw WithStatus(rpc.Error.ToException(), response);
        return rpc.Result;
    }

    private async IAsyncEnumerable<StreamResponse> StreamAsync<TParams>(string method, TParams parameters, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = BuildRequest(method, parameters, streaming: true);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!IsEventStream(response))
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var rpc = TryParseResponse(body);
            if (rpc?.Error is not null) throw WithStatus(rpc.Error.ToException(), response);
            if (!response.IsSuccessStatusCode) throw HttpError(response, body);
            throw ProtocolError($"{method}: expected text/event-stream.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            var rpc = TryParseResponse(evt.Data) ?? throw ProtocolError($"{method}: SSE event is not a JSON-RPC response.");
            if (rpc.Error is not null) throw rpc.Error.ToException();
            if (rpc.Result is not { } r) throw ProtocolError($"{method}: SSE event has no result.");
            yield return Parse<StreamResponse>(r, "stream event");
        }
    }

    private HttpRequestMessage BuildRequest<TParams>(string method, TParams parameters, bool streaming)
    {
        var rpc = new JsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N")),
            Method = method,
            Params = A2AJson.SerializeToElement(parameters),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonBody(rpc, A2AProtocol.JsonMediaType) };
        ApplyHeaders(request, streaming);
        return request;
    }

    private static JsonRpcResponse? TryParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("jsonrpc", out var v) || v.GetString() != "2.0") return null;
            if (!root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)) return null;
            return root.Deserialize<JsonRpcResponse>(A2AJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static A2AException WithStatus(A2AException ex, HttpResponseMessage response) =>
        new(ex.Code, ex.Message) { Details = ex.Details, HttpStatus = (int)response.StatusCode };
}
