using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Json;
using A2A.JsonRpc;

namespace A2A.Client.Transports;

/// <summary>JSON-RPC 2.0 over HTTP binding (spec §9).</summary>
public sealed class JsonRpcTransport : HttpTransportBase, IA2AClient, IResumableTransport, ITransportBinding
{
    string ITransportBinding.Binding => ProtocolBindings.JsonRpc;

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
        ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var request = await BuildRequestAsync(method, parameters, streaming: false, call, key, attemptCt).ConfigureAwait(false);
            using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            JsonRpcResponse? rpc;
            using (var body = await ResponseBody.ReadUtf8Async(response.Content, attemptCt).ConfigureAwait(false))
                rpc = TryParseResponse(body.Span);
            if (rpc is null)
            {
                if (!response.IsSuccessStatusCode) throw HttpError(response, await response.Content.ReadAsStringAsync(attemptCt).ConfigureAwait(false));
                throw ProtocolError($"{method}: response is not a JSON-RPC response.");
            }
            if (rpc.Error is not null) throw WithStatus(rpc.Error.ToException(), response);
            return rpc.Result;
        }, ct);

    private async IAsyncEnumerable<StreamFrame> StreamAsync<TParams>(string method, TParams parameters, A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, parameters, streaming: true, call, key, ct, lastEventId).ConfigureAwait(false);
        using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!IsEventStream(response))
        {
            await ResponseBody.BufferAsync(response.Content, ct).ConfigureAwait(false);
            JsonRpcResponse? error;
            using (var body = await ResponseBody.ReadUtf8Async(response.Content, ct).ConfigureAwait(false))
                error = TryParseResponse(body.Span);
            if (error?.Error is not null) throw WithStatus(error.Error.ToException(), response);
            if (!response.IsSuccessStatusCode) throw HttpError(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw ProtocolError($"{method}: expected text/event-stream.");
        }

#if NETSTANDARD2_0
        // Stream has no DisposeAsync on netstandard2.0.
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            JsonRpcResponse? parsed;
            using (var data = ResponseBody.FromString(evt.Data))
                parsed = TryParseResponse(data.Span);
            var rpc = parsed ?? throw ProtocolError($"{method}: SSE event is not a JSON-RPC response.");
            if (rpc.Error is not null) throw rpc.Error.ToException();
            if (rpc.Result is not { } r) throw ProtocolError($"{method}: SSE event has no result.");
            // A2A #2269: an event of a type this SDK does not know (a newer protocol version) is skipped, not fatal.
            if (!IsKnownStreamEvent(r)) continue;
            yield return new StreamFrame(Parse<StreamResponse>(r, "stream event"), evt.FrameId);
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync<TParams>(string method, TParams parameters, bool streaming, A2ARequestOptions? call, string? key, CancellationToken ct, string? lastEventId = null)
    {
        var body = JsonRpcWriter.Request(Guid.NewGuid().ToString("N"), method, parameters, A2AJson.GetTypeInfo<TParams>());
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(A2AProtocol.JsonMediaType) { CharSet = "utf-8" };
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
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

    private static readonly byte[][] EnvelopeNames = ["jsonrpc"u8.ToArray(), "result"u8.ToArray(), "error"u8.ToArray()];

    /// <summary>
    /// The JSON-RPC response in <paramref name="body"/>, or null when it is not one: not a JSON object, no <c>jsonrpc</c> of
    /// "2.0", neither <c>result</c> nor <c>error</c>, or an envelope that does not bind. One reader pass locates the members,
    /// then the envelope is bound from the same bytes (no <see cref="JsonDocument"/>). Bodies the pass does not settle take the
    /// former <see cref="JsonDocument"/> path, so every answer is the one it always was.
    /// </summary>
    private static JsonRpcResponse? TryParseResponse(ReadOnlySpan<byte> body)
    {
        Span<(int Start, int End)> found = stackalloc (int, int)[3];
        if (!JsonMembers.TryScan(body, EnvelopeNames, found, out var root, out var isObject)) return TryParseResponseSlow(body);
        if (!isObject || found[0].Start < 0) return null;
        var version = body[found[0].Start..found[0].End];
        if (version[0] == (byte)'n') return null;                       // "jsonrpc": null
        if (version[0] != (byte)'"') return TryParseResponseSlow(body); // not a string: the former path throws as it did
        var reader = new Utf8JsonReader(version);
        reader.Read();
        if (!reader.ValueTextEquals("2.0"u8))
            return reader.ValueIsEscaped || !System.Text.Unicode.Utf8.IsValid(reader.ValueSpan) ? TryParseResponseSlow(body) : null;
        if (found[1].Start < 0 && found[2].Start < 0) return null;
        try
        {
            return JsonSerializer.Deserialize(body[root], A2AJson.GetTypeInfo<JsonRpcResponse>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonRpcResponse? TryParseResponseSlow(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
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
