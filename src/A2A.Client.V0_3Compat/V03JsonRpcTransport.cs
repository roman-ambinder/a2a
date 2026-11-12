using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Client.Transports;
using A2A.V0_3Compat;

namespace A2A.Client.V0_3Compat;

/// <summary>The v0.3 JSON-RPC 2.0 binding. Shapes come from the v0.3 JSON schema (<see cref="V03JsonCodec" />).</summary>
internal sealed class V03JsonRpcTransport : HttpTransportBase, IA2AClient, IResumableTransport, ITransportBinding
{
    private static readonly V03JsonCodec Codec = V03JsonCodec.Instance;
    private readonly Uri _endpoint;

    public V03JsonRpcTransport(HttpClient httpClient, Uri endpoint, A2AClientOptions? options = null)
        : base(httpClient, V03ClientSupport.CopyForV03(options))
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        var result = await InvokeAsync(V03Compat.Methods.SendMessage, Codec.WriteSendRequest(request),
            RetryMode.UnsentOnly, options, key, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadSendResult(result), "message/send result");
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return ((IResumableTransport)this).SendStreamingFramesAsync(request, options, cancellationToken)
            .Unframe(cancellationToken);
    }

    public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.GetTask, V03JsonCodec.WriteGetTaskParams(request),
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadTask(result), "tasks/get result");
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<ListTasksResponse>(V03ClientSupport.Unsupported("ListTasks"));
    }

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.CancelTask, V03JsonCodec.WriteCancelParams(request),
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadTask(result), "tasks/cancel result");
    }

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return ((IResumableTransport)this).SubscribeToTaskAsync(request, options, null, cancellationToken)
            .Unframe(cancellationToken);
    }

    public async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        TaskPushNotificationConfig config, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.SetPushConfig, Codec.WritePushConfig(config), RetryMode.None,
            options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadPushConfig(result), "push config");
    }

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.GetPushConfig,
            V03JsonCodec.WritePushConfigIdParams(request.TaskId, request.Id), RetryMode.Idempotent, options, null,
            cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadPushConfig(result), "push config");
    }

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.ListPushConfig, V03JsonCodec.WriteTaskIdParams(request.TaskId),
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() =>
        {
            var array = result as JsonArray ?? throw new JsonException("result must be an array");
            return new ListTaskPushNotificationConfigsResponse
                { Configs = array.Select(c => Codec.ReadPushConfig(c!)).ToList() };
        }, "push config list");
    }

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        await InvokeRawAsync(V03Compat.Methods.DeletePushConfig,
            V03JsonCodec.WritePushConfigIdParams(request.TaskId, request.Id), RetryMode.None, options, null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(V03Compat.Methods.GetExtendedCard, null, RetryMode.Idempotent, options, null,
            cancellationToken).ConfigureAwait(false);
        // The v0.3 card carries no signature the SDK can verify: it is read as unsigned.
        return V03ClientSupport.Read(
            () => V03CardCodec.FromV03(result as JsonObject ?? throw new JsonException("result must be an object")),
            "extended card");
    }

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request,
        A2ARequestOptions? options, CancellationToken cancellationToken)
    {
        return StreamAsync(V03Compat.Methods.SendStreamingMessage, Codec.WriteSendRequest(request), options,
            PrepareSend(request, options), null, cancellationToken);
    }

    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        return StreamAsync(V03Compat.Methods.ResubscribeTask, V03JsonCodec.WriteTaskIdParams(request.Id), options, null,
            lastEventId, cancellationToken);
    }

    string ITransportBinding.Binding => ProtocolBindings.JsonRpc;

    // ---- plumbing ----

    private async Task<JsonNode> InvokeAsync(string method, JsonObject? parameters, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        return await InvokeRawAsync(method, parameters, retry, call, key, ct).ConfigureAwait(false) ??
               throw ProtocolError($"{method} response has no result.");
    }

    /// <summary>Sends one call. The result is null when the server answered <c>"result": null</c> (push config delete).</summary>
    private async Task<JsonNode?> InvokeRawAsync(string method, JsonObject? parameters, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        var result = await ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var request = await BuildRequestAsync(method, parameters, false, call, key, attemptCt)
                .ConfigureAwait(false);
            using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseContentRead, attemptCt)
                .ConfigureAwait(false);
            JsonObject? envelope;
            using (var body = await ResponseBody.ReadUtf8Async(response.Content, attemptCt).ConfigureAwait(false))
            {
                envelope = TryParseEnvelope(body.Span, response.IsSuccessStatusCode);
            }

            if (envelope is null)
            {
                if (!response.IsSuccessStatusCode)
                    throw HttpError(response,
                        await response.Content.ReadAsStringAsync(attemptCt).ConfigureAwait(false));
                throw ProtocolError($"{method}: response is not a JSON-RPC response.");
            }

            if (envelope["error"] is { } error) throw ErrorOf(error, response);
            return new Reply(envelope["result"]);
        }, ct).ConfigureAwait(false);
        return result.Value;
    }

    private async IAsyncEnumerable<StreamFrame> StreamAsync(string method, JsonObject parameters,
        A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, parameters, true, call, key, ct, lastEventId)
            .ConfigureAwait(false);
        using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!IsEventStream(response))
        {
            await ResponseBody.BufferAsync(response.Content, ct).ConfigureAwait(false);
            JsonObject? envelope;
            using (var body = await ResponseBody.ReadUtf8Async(response.Content, ct).ConfigureAwait(false))
            {
                envelope = TryParseEnvelope(body.Span);
            }

            if (envelope?["error"] is { } error) throw ErrorOf(error, response);
            if (!response.IsSuccessStatusCode)
                throw HttpError(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw ProtocolError($"{method}: expected text/event-stream.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            var envelope = TryParseEnvelope(V03ClientSupport.Utf8(evt.Data)) ??
                           throw ProtocolError($"{method}: SSE event is not a JSON-RPC response.");
            if (envelope["error"] is { } error) throw ErrorOf(error, null);
            if (envelope["result"] is not { } result) throw ProtocolError($"{method}: SSE event has no result.");
            var item = ReadEvent(result, out var final);
            yield return new StreamFrame(item, evt.FrameId);
            // v0.3 ends the stream on final: true. Some servers leave the connection open, so do not wait for it to close.
            if (final) yield break;
        }
    }

    private static StreamResponse ReadEvent(JsonNode result, out bool final)
    {
        var isFinal = false;
        var item = V03ClientSupport.Read(() => Codec.ReadStreamEvent(result, out isFinal), "stream event");
        final = isFinal;
        return item;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(string method, JsonObject? parameters, bool streaming,
        A2ARequestOptions? call, string? key, CancellationToken ct, string? lastEventId = null)
    {
        var envelope = new JsonObject
            { ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = method };
        // A retry builds the request again from the same params, so hand over a copy.
        if (parameters is not null) envelope["params"] = parameters.DeepClone();
        var content = new ByteArrayContent(V03Json.ToUtf8(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(V03ClientSupport.JsonMediaType) { CharSet = "utf-8" };
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        try
        {
            await ApplyHeadersAsync(request, streaming, call, key, ct, lastEventId).ConfigureAwait(false);
            V03ClientSupport.UseV03ExtensionsHeader(request);
        }
        catch
        {
            request.Dispose();
            throw;
        }

        return request;
    }

    /// <summary>
    ///     The JSON-RPC response in <paramref name="body" />, or null when it is not one (not an object, wrong version,
    ///     no result or error).
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="idOnlyIsNullResult">
    ///     True for a successful (2xx) unary response: a2a-sdk 0.3 (Python) serializes with exclude_none, so a null result
    ///     (tasks/pushNotificationConfig/delete) has no "result" member at all, and an envelope with an id and no error is a
    ///     null result. On any other status, or on a stream event, such a body is not an envelope, so the HTTP error is kept.
    /// </param>
    private static JsonObject? TryParseEnvelope(ReadOnlySpan<byte> body, bool idOnlyIsNullResult = false)
    {
        if (V03ClientSupport.TryParse(body) is not JsonObject root) return null;
        if (V03ClientSupport.Text(root["jsonrpc"]) != "2.0") return null;
        return root.ContainsKey("result") || root.ContainsKey("error") || (idOnlyIsNullResult && root.ContainsKey("id"))
            ? root
            : null;
    }

    private static A2AException ErrorOf(JsonNode error, HttpResponseMessage? response)
    {
        var obj = error as JsonObject;
        var code = V03ClientSupport.CodeOf(V03ClientSupport.Long(obj?["code"]) ?? 0) ?? A2AErrorCode.Internal;
        var message = V03ClientSupport.Text(obj?["message"]);
        return new A2AException(code, message)
        {
            HttpStatus = response is null ? null : (int)response.StatusCode, RetryAfter = RetryAfterOf(response, null)
        };
    }

    // ExecuteUnaryAsync<T> needs a non-null T: a null JSON result is carried in a box.
    private sealed record Reply(JsonNode? Value);
}