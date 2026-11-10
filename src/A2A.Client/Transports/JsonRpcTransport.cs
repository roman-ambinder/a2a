using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Json;
using A2A.JsonRpc;

namespace A2A.Client.Transports;

/// <summary>JSON-RPC 2.0 over HTTP binding (spec §9).</summary>
#pragma warning disable A2AEXP005 // the live exchange of A2A 1.1
public sealed class JsonRpcTransport : HttpTransportBase, IA2AClient, IA2AExtensionOperationClient, IResumableTransport,
    ITransportBinding, ILiveTransport
#pragma warning restore A2AEXP005
{
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    private static readonly byte[][] StreamEventNames =
    [
        "task"u8.ToArray(), "message"u8.ToArray(), "statusUpdate"u8.ToArray(), "artifactUpdate"u8.ToArray(),
        "messageUpdate"u8.ToArray(), "error"u8.ToArray()
    ];

    private static readonly byte[][] EnvelopeNames = ["jsonrpc"u8.ToArray(), "result"u8.ToArray(), "error"u8.ToArray()];

    private readonly Uri _endpoint;

    /// <summary>
    ///     Creates the transport for the JSON-RPC <paramref name="endpoint" />. <paramref name="options" /> defaults to a
    ///     new <see cref="A2AClientOptions" />.
    /// </summary>
    public JsonRpcTransport(HttpClient httpClient, Uri endpoint, A2AClientOptions? options = null) : base(httpClient,
        options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        return InvokeAsync<SendMessageRequest, SendMessageResponse>(A2AProtocol.Methods.SendMessage, request,
            RetryMode.UnsentOnly, options, key, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, options, PrepareSend(request, options),
            null, cancellationToken).Unframe(cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<GetTaskRequest, AgentTask>(A2AProtocol.Methods.GetTask, request, RetryMode.Idempotent,
            options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<ListTasksRequest, ListTasksResponse>(A2AProtocol.Methods.ListTasks, request,
            RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<CancelTaskRequest, AgentTask>(A2AProtocol.Methods.CancelTask, request, RetryMode.Idempotent,
            options, null, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, options, null, null, cancellationToken)
            .Unframe(cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return InvokeAsync<TaskPushNotificationConfig, TaskPushNotificationConfig>(
            A2AProtocol.Methods.CreateTaskPushNotificationConfig, config, RetryMode.None, options, null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<GetTaskPushNotificationConfigRequest, TaskPushNotificationConfig>(
            A2AProtocol.Methods.GetTaskPushNotificationConfig, request, RetryMode.Idempotent, options, null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeAsync<ListTaskPushNotificationConfigsRequest, ListTaskPushNotificationConfigsResponse>(
            A2AProtocol.Methods.ListTaskPushNotificationConfigs, request, RetryMode.Idempotent, options, null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        await InvokeRawAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, request, RetryMode.None, options,
            null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return InvokeAsync<GetExtendedAgentCardRequest, AgentCard>(A2AProtocol.Methods.GetExtendedAgentCard, request,
            RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExtensionOperationParams.RequireMethod(method);
        var withTenant = ExtensionOperationParams.WithTenant(parameters, Options.Tenant);
        return InvokeExtensionAsync(method, withTenant, options, cancellationToken);
    }

    /// <summary>
    ///     A2A 1.1 SendLiveMessage: the downlink is a streaming call of the method (opened when
    ///     <see cref="A2ALiveConnection.Events" />
    ///     is read), and each later request a unary call of it, answered with the task.
    /// </summary>
    [Experimental("A2AEXP005",
        UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        var live = LiveRequests.WithVersion11(options);
        var taskId = first.Subscribe?.Id ?? first.SendMessage?.Message?.TaskId;
        A2ALiveConnection connection = new HttpLiveConnection(
            token => LiveRequests.Track(
                StreamAsync(A2AProtocol.Methods.SendLiveMessage, first, live, null, null, token).Unframe(token),
                id => taskId ??= id, token),
            (request, token) => InvokeAsync<StreamRequest, AgentTask>(A2AProtocol.Methods.SendLiveMessage, request,
                RetryMode.None, live, null, token),
            () => taskId);
        return Task.FromResult(connection);
    }

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request,
        A2ARequestOptions? options, CancellationToken cancellationToken)
    {
        return StreamAsync(A2AProtocol.Methods.SendStreamingMessage, request, options, PrepareSend(request, options),
            null, cancellationToken);
    }

    /// <summary>
    ///     Subscribes and sends <c>Last-Event-ID</c> when <paramref name="lastEventId" /> is set. Frames carry their SSE
    ///     ids.
    /// </summary>
    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        return StreamAsync(A2AProtocol.Methods.SubscribeToTask, request, options, null, lastEventId, cancellationToken);
    }

    string ITransportBinding.Binding => ProtocolBindings.JsonRpc;

    private async Task<JsonElement> InvokeExtensionAsync(string method, JsonElement parameters,
        A2ARequestOptions? options, CancellationToken cancellationToken)
    {
        var result = await InvokeCoreAsync(method, () => ExtensionRequest(method, parameters), RetryMode.UnsentOnly,
            options, null, cancellationToken).ConfigureAwait(false);
        return result ?? NullElement; // the response has "result": null
    }

    private static byte[] ExtensionRequest(string method, JsonElement parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", Guid.NewGuid().ToString("N"));
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            parameters.WriteTo(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        return ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var request = await BuildRequestAsync(method, parameters, false, call, key, attemptCt)
                .ConfigureAwait(false);
            using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseContentRead, attemptCt)
                .ConfigureAwait(false);
            JsonRpcResponse? rpc;
            using (var responseBody =
                   await ResponseBody.ReadUtf8Async(response.Content, attemptCt).ConfigureAwait(false))
            {
                // The common case binds the result straight from the body: no JsonDocument for it, no second parse.
                if (TryReadResult<TResult>(responseBody.Span, out var fast)) return fast;
                rpc = TryParseResponse(responseBody.Span);
            }

            var result = await ResultOrThrowAsync(rpc, response, method, attemptCt).ConfigureAwait(false);
            if (result is not { } r) throw ProtocolError($"{method} response has no result.");
            return Parse<TResult>(r, method + " result");
        }, ct);
    }

    /// <summary>
    ///     The fast path of a typed call. False (the caller takes the former path) unless the body is one object with
    ///     <c>"jsonrpc":"2.0"</c>, a non-null result, no error member and no repeated member, and the result binds.
    /// </summary>
    private static bool TryReadResult<T>(ReadOnlySpan<byte> body, out T value)
    {
        value = default!;
        Span<(int Start, int End)> found = stackalloc (int, int)[3];
        if (!JsonMembers.TryScan(body, EnvelopeNames, found, out _, out var isObject) || !isObject) return false;
        if (found[0].Start < 0 || found[1].Start < 0 || found[2].Start >= 0) return false;
        if (!body[found[0].Start..found[0].End].SequenceEqual("\"2.0\""u8)) return false;
        var result = body[found[1].Start..found[1].End];
        if (result[0] == (byte)'n') return false; // null: the former path reports it
        try
        {
            if (JsonSerializer.Deserialize(result, A2AJson.GetTypeInfo<T>()) is not { } bound) return false;
            value = bound;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The result of a parsed response, or the error it carries (or the HTTP or protocol error when it is none).</summary>
    private static async Task<JsonElement?> ResultOrThrowAsync(JsonRpcResponse? rpc, HttpResponseMessage response,
        string method, CancellationToken ct)
    {
        if (rpc is null)
        {
            if (!response.IsSuccessStatusCode)
                throw HttpError(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw ProtocolError($"{method}: response is not a JSON-RPC response.");
        }

        if (rpc.Error is not null) throw WithStatus(rpc.Error.ToException(), response);
        return rpc.Result;
    }

    private Task<JsonElement?> InvokeRawAsync<TParams>(string method, TParams parameters, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        return InvokeCoreAsync(method,
            () => JsonRpcWriter.Request(Guid.NewGuid().ToString("N"), method, parameters,
                A2AJson.GetTypeInfo<TParams>()), retry, call, key, ct);
    }

    private Task<JsonElement?> InvokeCoreAsync(string method, Func<byte[]> body, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        return ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var request = await BuildRequestAsync(body(), false, call, key, attemptCt)
                .ConfigureAwait(false);
            using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseContentRead, attemptCt)
                .ConfigureAwait(false);
            JsonRpcResponse? rpc;
            using (var responseBody =
                   await ResponseBody.ReadUtf8Async(response.Content, attemptCt).ConfigureAwait(false))
            {
                rpc = TryParseResponse(responseBody.Span);
            }

            return await ResultOrThrowAsync(rpc, response, method, attemptCt).ConfigureAwait(false);
        }, ct);
    }

    private async IAsyncEnumerable<StreamFrame> StreamAsync<TParams>(string method, TParams parameters,
        A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = await BuildRequestAsync(method, parameters, true, call, key, ct, lastEventId)
            .ConfigureAwait(false);
        using var response = await SendCheckedAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!IsEventStream(response))
        {
            await ResponseBody.BufferAsync(response.Content, ct).ConfigureAwait(false);
            JsonRpcResponse? error;
            using (var body = await ResponseBody.ReadUtf8Async(response.Content, ct).ConfigureAwait(false))
            {
                error = TryParseResponse(body.Span);
            }

            if (error?.Error is not null) throw WithStatus(error.Error.ToException(), response);
            if (!response.IsSuccessStatusCode)
                throw HttpError(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            throw ProtocolError($"{method}: expected text/event-stream.");
        }

#if NETSTANDARD2_0
        // Stream has no DisposeAsync on netstandard2.0.
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
        // Each event is parsed from its UTF-8 bytes in the reader's buffer: no string and no copy per event.
        await foreach (var frame in SseReader.ReadFramesAsync(stream, SseReader.DefaultMaxEventBytes, ct)
                           .ConfigureAwait(false))
            // A2A #2269: an event of a type this SDK does not know (a newer protocol version) is skipped, not fatal.
            if (ReadStreamEvent(frame.Data.Span, method) is { } item)
                yield return new StreamFrame(item, frame.FrameId);
    }

    /// <summary>
    ///     The stream event in one SSE data payload, or null for an event type this SDK does not know. The common case (a
    ///     <c>"2.0"</c> response with a result object and no error) binds the result straight from <paramref name="data" />;
    ///     anything
    ///     else takes the former path, on the text form of the payload, so every error is the one it always was.
    /// </summary>
    private static StreamResponse? ReadStreamEvent(ReadOnlySpan<byte> data, string method)
    {
        if (Utf8.IsValid(data) && TryReadStreamEvent(data, out var fast, out var known)) return known ? fast : null;

        // Invalid UTF-8 becomes U+FFFD here, as it did when every payload was decoded to a string first.
        JsonRpcResponse? parsed;
        using (var bytes = ResponseBody.FromString(Encoding.UTF8.GetString(data)))
        {
            parsed = TryParseResponse(bytes.Span);
        }

        var rpc = parsed ?? throw ProtocolError($"{method}: SSE event is not a JSON-RPC response.");
        if (rpc.Error is not null) throw rpc.Error.ToException();
        if (rpc.Result is not { } r) throw ProtocolError($"{method}: SSE event has no result.");
        return IsKnownStreamEvent(r) ? Parse<StreamResponse>(r, "stream event") : null;
    }

    /// <summary>
    ///     The fast path of <see cref="ReadStreamEvent" />. False (the caller takes the former path) unless the payload is one
    ///     object with <c>"jsonrpc":"2.0"</c>, a result object, no error member and no repeated member, and the result binds.
    /// </summary>
    private static bool TryReadStreamEvent(ReadOnlySpan<byte> body, out StreamResponse? evt, out bool known)
    {
        evt = null;
        known = false;
        Span<(int Start, int End)> found = stackalloc (int, int)[3];
        if (!JsonMembers.TryScan(body, EnvelopeNames, found, out _, out var isObject) || !isObject) return false;
        if (found[0].Start < 0 || found[1].Start < 0 || found[2].Start >= 0) return false;
        if (!body[found[0].Start..found[0].End].SequenceEqual("\"2.0\""u8)) return false;
        var result = body[found[1].Start..found[1].End];
        if (result[0] != (byte)'{') return false;
        Span<(int Start, int End)> members = stackalloc (int, int)[6];
        if (!JsonMembers.TryScan(result, StreamEventNames, members, out _, out _)) return false;
        foreach (var m in members) known |= m.Start >= 0;
        if (!known) return true;
        try
        {
            evt = JsonSerializer.Deserialize(result, A2AJson.GetTypeInfo<StreamResponse>());
        }
        catch (JsonException)
        {
            return false;
        }

        return evt is not null;
    }

    private Task<HttpRequestMessage> BuildRequestAsync<TParams>(string method, TParams parameters, bool streaming,
        A2ARequestOptions? call, string? key, CancellationToken ct, string? lastEventId = null)
    {
        return BuildRequestAsync(
            JsonRpcWriter.Request(Guid.NewGuid().ToString("N"), method, parameters, A2AJson.GetTypeInfo<TParams>()),
            streaming, call, key, ct, lastEventId);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(byte[] body, bool streaming, A2ARequestOptions? call,
        string? key, CancellationToken ct, string? lastEventId = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(A2AProtocol.JsonMediaType) { CharSet = "utf-8" };
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

    /// <summary>
    ///     The JSON-RPC response in <paramref name="body" />, or null when it is not one: not a JSON object, no <c>jsonrpc</c>
    ///     of
    ///     "2.0", neither <c>result</c> nor <c>error</c>, or an envelope that does not bind. One reader pass locates the
    ///     members,
    ///     then the envelope is bound from the same bytes (no <see cref="JsonDocument" />). Bodies the pass does not settle
    ///     take the
    ///     former <see cref="JsonDocument" /> path, so every answer is the one it always was.
    /// </summary>
    private static JsonRpcResponse? TryParseResponse(ReadOnlySpan<byte> body)
    {
        Span<(int Start, int End)> found = stackalloc (int, int)[3];
        if (!JsonMembers.TryScan(body, EnvelopeNames, found, out var root, out var isObject))
            return TryParseResponseSlow(body);
        if (!isObject || found[0].Start < 0) return null;
        var version = body[found[0].Start..found[0].End];
        if (version[0] == (byte)'n') return null; // "jsonrpc": null
        if (version[0] != (byte)'"')
            return TryParseResponseSlow(body); // not a string: the former path throws as it did
        var reader = new Utf8JsonReader(version);
        reader.Read();
        if (!reader.ValueTextEquals("2.0"u8))
            return reader.ValueIsEscaped || !Utf8.IsValid(reader.ValueSpan) ? TryParseResponseSlow(body) : null;
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

    private static A2AException WithStatus(A2AException ex, HttpResponseMessage response)
    {
        return new A2AException(ex.Code, ex.Message)
        {
            Details = ex.Details, Metadata = ex.Metadata, HttpStatus = (int)response.StatusCode,
            RetryAfter = RetryAfterOf(response, ex.Details)
        };
    }
}