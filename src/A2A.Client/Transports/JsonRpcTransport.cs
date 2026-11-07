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

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        return InvokeAsync<SendMessageRequest, SendMessageResponse>(A2AProtocol.Methods.SendMessage, request, retry: true, options, key, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, options, PrepareSend(request, options), cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskRequest, AgentTask>(A2AProtocol.Methods.GetTask, request, retry: true, options, null, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTasksRequest, ListTasksResponse>(A2AProtocol.Methods.ListTasks, request, retry: true, options, null, cancellationToken);

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<CancelTaskRequest, AgentTask>(A2AProtocol.Methods.CancelTask, request, retry: true, options, null, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, options, null, cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<TaskPushNotificationConfig, TaskPushNotificationConfig>(A2AProtocol.Methods.CreateTaskPushNotificationConfig, config, retry: false, options, null, cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskPushNotificationConfigRequest, TaskPushNotificationConfig>(A2AProtocol.Methods.GetTaskPushNotificationConfig, request, retry: true, options, null, cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsResponse>(A2AProtocol.Methods.ListTaskPushNotificationConfigs, request, retry: true, options, null, cancellationToken);

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        await InvokeRawAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, request, retry: false, options, null, cancellationToken).ConfigureAwait(false);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetExtendedAgentCardRequest, AgentCard>(A2AProtocol.Methods.GetExtendedAgentCard, request, retry: true, options, null, cancellationToken);

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, bool retry, A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        var result = await InvokeRawAsync(method, parameters, retry, call, key, ct).ConfigureAwait(false);
        if (result is not { } r) throw ProtocolError($"{method} response has no result.");
        return Parse<TResult>(r, method + " result");
    }

    private Task<JsonElement?> InvokeRawAsync<TParams>(string method, TParams parameters, bool retry, A2ARequestOptions? call, string? key, CancellationToken ct) =>
        ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var request = await BuildRequestAsync(method, parameters, streaming: false, call, key, attemptCt).ConfigureAwait(false);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(attemptCt).ConfigureAwait(false);
            var rpc = TryParseResponse(body);
            if (rpc is null)
            {
                if (!response.IsSuccessStatusCode) throw HttpError(response, body);
                throw ProtocolError($"{method}: response is not a JSON-RPC response.");
            }
            if (rpc.Error is not null) throw WithStatus(rpc.Error.ToException(), response);
            return rpc.Result;
        }, ct);

    private async IAsyncEnumerable<StreamResponse> StreamAsync<TParams>(string method, TParams parameters, A2ARequestOptions? call, string? key, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, parameters, streaming: true, call, key, ct).ConfigureAwait(false);
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

    private async Task<HttpRequestMessage> BuildRequestAsync<TParams>(string method, TParams parameters, bool streaming, A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        var rpc = new JsonRpcRequest
        {
            Id = A2AJson.SerializeToElement(Guid.NewGuid().ToString("N")),
            Method = method,
            Params = A2AJson.SerializeToElement(parameters),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonBody(rpc, A2AProtocol.JsonMediaType) };
        try
        {
            await ApplyHeadersAsync(request, streaming, call, key, ct).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
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
            return root.Deserialize(A2AJson.GetTypeInfo<JsonRpcResponse>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static A2AException WithStatus(A2AException ex, HttpResponseMessage response) =>
        new(ex.Code, ex.Message) { Details = ex.Details, HttpStatus = (int)response.StatusCode, RetryAfter = RetryAfterOf(response, ex.Details) };
}
