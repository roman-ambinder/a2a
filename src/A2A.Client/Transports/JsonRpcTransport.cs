using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Client.Telemetry;
using A2A.Json;
using A2A.JsonRpc;

namespace A2A.Client.Transports;

/// <summary>JSON-RPC 2.0 over HTTP binding (spec §9).</summary>
public sealed class JsonRpcTransport : HttpTransportBase, IA2AClient, IResumableTransport
{
    private readonly Uri _endpoint;

    /// <summary>Creates the transport for the JSON-RPC <paramref name="endpoint"/>. <paramref name="options"/> defaults to a new <see cref="A2AClientOptions"/>.</summary>
    public JsonRpcTransport(HttpClient httpClient, Uri endpoint, A2AClientOptions? options = null) : base(httpClient, options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        return InvokeAsync<SendMessageRequest, SendMessageResponse>(A2AProtocol.Methods.SendMessage, request, RetryMode.UnsentOnly, options, key, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, options, PrepareSend(request, options), null, cancellationToken).Unframe(cancellationToken);

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options, CancellationToken cancellationToken) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, options, PrepareSend(request, options), null, cancellationToken);

    /// <summary>Subscribes and sends <c>Last-Event-ID</c> when <paramref name="lastEventId"/> is set. Frames carry their SSE ids.</summary>
    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken) =>
        StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, options, null, lastEventId, cancellationToken);

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskRequest, AgentTask>(A2AProtocol.Methods.GetTask, request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTasksRequest, ListTasksResponse>(A2AProtocol.Methods.ListTasks, request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<CancelTaskRequest, AgentTask>(A2AProtocol.Methods.CancelTask, request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, options, null, null, cancellationToken).Unframe(cancellationToken);

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<TaskPushNotificationConfig, TaskPushNotificationConfig>(A2AProtocol.Methods.CreateTaskPushNotificationConfig, config, RetryMode.None, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetTaskPushNotificationConfigRequest, TaskPushNotificationConfig>(A2AProtocol.Methods.GetTaskPushNotificationConfig, request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsResponse>(A2AProtocol.Methods.ListTaskPushNotificationConfigs, request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        await InvokeRawAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, request, RetryMode.None, options, null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<GetExtendedAgentCardRequest, AgentCard>(A2AProtocol.Methods.GetExtendedAgentCard, request, RetryMode.Idempotent, options, null, cancellationToken);

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, RetryMode retry, A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        var result = await InvokeRawAsync(method, parameters, retry, call, key, ct).ConfigureAwait(false);
        if (result is not { } r) throw ProtocolError($"{method} response has no result.");
        return Parse<TResult>(r, method + " result");
    }

    private Task<JsonElement?> InvokeRawAsync<TParams>(string method, TParams parameters, RetryMode retry, A2ARequestOptions? call, string? key, CancellationToken ct) =>
        ExecuteUnaryAsync(method, retry, call, async attemptCt =>
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

    private async IAsyncEnumerable<StreamFrame> StreamAsync<TParams>(string method, TParams parameters, A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        // The span covers opening the stream: from the request to the event-stream response headers.
        var activity = A2AClientTelemetry.Start(method);
        HttpRequestMessage? opening = null;
        HttpResponseMessage? opened = null;
        try
        {
            opening = await BuildRequestAsync(method, parameters, streaming: true, call, key, ct, lastEventId).ConfigureAwait(false);
            opened = await Http.SendAsync(opening, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsEventStream(opened))
            {
                var body = await opened.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var error = TryParseResponse(body);
                if (error?.Error is not null) throw WithStatus(error.Error.ToException(), opened);
                if (!opened.IsSuccessStatusCode) throw HttpError(opened, body);
                throw ProtocolError($"{method}: expected text/event-stream.");
            }
        }
        catch (Exception ex)
        {
            opened?.Dispose();
            opening?.Dispose();
            A2AClientTelemetry.Stop(activity, ex, ct);
            throw;
        }
        A2AClientTelemetry.Stop(activity, null, ct);

        using var request = opening;
        using var response = opened;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            var rpc = TryParseResponse(evt.Data) ?? throw ProtocolError($"{method}: SSE event is not a JSON-RPC response.");
            if (rpc.Error is not null) throw rpc.Error.ToException();
            if (rpc.Result is not { } r) throw ProtocolError($"{method}: SSE event has no result.");
            yield return new StreamFrame(Parse<StreamResponse>(r, "stream event"), evt.FrameId);
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync<TParams>(string method, TParams parameters, bool streaming, A2ARequestOptions? call, string? key, CancellationToken ct, string? lastEventId = null)
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
            await ApplyHeadersAsync(request, streaming, call, key, ct, lastEventId).ConfigureAwait(false);
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
