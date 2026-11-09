using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>HTTP+JSON/REST binding (spec §11).</summary>
#pragma warning disable A2AEXP005 // the live exchange of A2A 1.1
public sealed class HttpJsonTransport : HttpTransportBase, IA2AClient, IA2AExtensionOperationClient, IResumableTransport, ITransportBinding, ILiveTransport
#pragma warning restore A2AEXP005
{
    string ITransportBinding.Binding => ProtocolBindings.HttpJson;

    private readonly string _baseUrl;

    /// <summary>Creates the transport for the HTTP+JSON endpoint at <paramref name="baseUrl"/>. <paramref name="options"/> defaults to a new <see cref="A2AClientOptions"/>.</summary>
    public HttpJsonTransport(HttpClient httpClient, Uri baseUrl, A2AClientOptions? options = null) : base(httpClient, options)
    {
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).AbsoluteUri.TrimEnd('/');
    }

    /// <inheritdoc />
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        return SendAsync<SendMessageResponse>(HttpMethod.Post, Path(request.Tenant, "/message:send"), request, RetryMode.UnsentOnly, options, key, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken).Unframe(cancellationToken);

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options, CancellationToken cancellationToken) =>
        StreamAsync(HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken);

    /// <summary>
    /// A2A 1.1 SendLiveMessage: the downlink is a streaming <c>POST /message:live</c> (opened when
    /// <see cref="A2ALiveConnection.Events"/> is read), and each later request a unary one, answered with the task.
    /// </summary>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        var live = LiveRequests.WithVersion11(options);
        string? taskId = first.Subscribe?.Id ?? first.SendMessage?.Message?.TaskId;
        A2ALiveConnection connection = new HttpLiveConnection(
            token => LiveRequests.Track(StreamAsync(HttpMethod.Post, Path(first.Tenant, "/message:live"), first, live, null, null, token).Unframe(token), id => taskId ??= id, token),
            (request, token) => SendAsync<AgentTask>(HttpMethod.Post, Path(request.Tenant, "/message:live"), request, RetryMode.None, live, null, token),
            () => taskId);
        return Task.FromResult(connection);
    }

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("historyLength", request.HistoryLength);
        return SendAsync<AgentTask>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.Id)}") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query()
            .Add("contextId", request.ContextId)
            .Add("status", request.Status is { } s ? ProtoEnumConverter<TaskState>.ToProtoName(s) : null)
            .Add("pageSize", request.PageSize)
            .Add("pageToken", request.PageToken)
            .Add("historyLength", request.HistoryLength)
            .Add("statusTimestampAfter", request.StatusTimestampAfter is { } t ? TimestampConverter.FormatUtc(t) : null)
            .Add("includeArtifacts", request.IncludeArtifacts is { } b ? (b ? "true" : "false") : null);
        return SendAsync<ListTasksResponse>(HttpMethod.Get, Path(request.Tenant, "/tasks") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentTask>(HttpMethod.Post, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:cancel"), request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ((IResumableTransport)this).SubscribeToTaskAsync(request, options, null, cancellationToken).Unframe(cancellationToken);

    /// <summary>Subscribes and sends <c>Last-Event-ID</c> when <paramref name="lastEventId"/> is set. Frames carry their SSE ids.</summary>
    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        var method = Options.RestSubscribeMethod;
        return StreamAsync(method, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:subscribe"), method == HttpMethod.Get ? null : request, options, null, lastEventId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(HttpMethod.Post, Path(config.Tenant, $"/tasks/{Esc(config.TaskId)}/pushNotificationConfigs"), config, RetryMode.None, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"), null, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("pageSize", request.PageSize).Add("pageToken", request.PageToken);
        return SendAsync<ListTaskPushNotificationConfigsResponse>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUnaryAsync(RetryMode.None, options, async attemptCt =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"));
            await ApplyHeadersAsync(req, streaming: false, options, null, attemptCt).ConfigureAwait(false);
            using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentCard>(HttpMethod.Get, Path(request.Tenant, "/extendedAgentCard"), null, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ExtensionOperationParams.RequireMethod(method);
        var body = ExtensionOperationParams.Normalize(parameters);
        // The tenant goes in the path: the one in the params, else the one of the options.
        var url = Path(ExtensionOperationParams.TenantOf(body), "/ext/" + Esc(method));
        return ExecuteUnaryAsync(RetryMode.UnsentOnly, options, async attemptCt =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.GetRawText())),
            };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(A2AProtocol.MediaType) { CharSet = "utf-8" };
            await ApplyHeadersAsync(req, streaming: false, options, null, attemptCt).ConfigureAwait(false);
            using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            using var payload = await ResponseBody.ReadUtf8Async(resp.Content, attemptCt).ConfigureAwait(false);
            if (payload.Memory.Length == 0) return A2AJson.EmptyObject;
            try
            {
                using var doc = JsonDocument.Parse(payload.Memory);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed result of '{method}' in agent response: {ex.Message}", ex); }
        }, cancellationToken);
    }

    private string Path(string? tenant, string path)
    {
        var t = tenant ?? Options.Tenant;
        return string.IsNullOrEmpty(t) ? _baseUrl + path : $"{_baseUrl}/{Esc(t)}{path}";
    }

    private static string Esc(string? s) => Uri.EscapeDataString(s ?? string.Empty);

    private Task<T> SendAsync<T>(HttpMethod method, string url, object? body, RetryMode retry, A2ARequestOptions? call, string? key, CancellationToken ct) =>
        ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonBody(body, A2AProtocol.MediaType);
            await ApplyHeadersAsync(req, streaming: false, call, key, attemptCt).ConfigureAwait(false);
            using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            try
            {
                return await ResponseBody.DeserializeAsync<T>(resp.Content, attemptCt).ConfigureAwait(false)
                    ?? throw new JsonException($"Null {typeof(T).Name} payload.");
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed {typeof(T).Name} in agent response: {ex.Message}", ex); }
        }, ct);

    private async IAsyncEnumerable<StreamFrame> StreamAsync(HttpMethod method, string url, object? body, A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonBody(body, A2AProtocol.MediaType);
        await ApplyHeadersAsync(req, streaming: true, call, key, ct, lastEventId).ConfigureAwait(false);
        using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, ct).ConfigureAwait(false);
        if (!IsEventStream(resp)) throw ProtocolError("Expected text/event-stream.");

#if NETSTANDARD2_0
        // Stream has no DisposeAsync on netstandard2.0.
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
        // Each event is parsed from its UTF-8 bytes in the reader's buffer: no string and no copy per event.
        await foreach (var frame in SseReader.ReadFramesAsync(stream, SseReader.DefaultMaxEventBytes, ct).ConfigureAwait(false))
        {
            StreamResponse? item = null;
            try
            {
                item = ParseStreamEvent(frame);
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed stream event: {ex.Message}", ex); }
            if (item is not null) yield return new StreamFrame(item, frame.FrameId);
        }
    }

    // "error" first: ParseStreamEvent reads found[0] as the error member and the rest as the known event members.
    private static readonly byte[][] EventMembers =
        ["error"u8.ToArray(), "task"u8.ToArray(), "message"u8.ToArray(), "statusUpdate"u8.ToArray(), "artifactUpdate"u8.ToArray(),
         "messageUpdate"u8.ToArray()];

    /// <summary>
    /// One stream event, or null for an event of a type this SDK does not know (A2A #2269: a newer protocol version; skipped,
    /// not fatal). A google.rpc.Status <c>error</c> member, or a frame named <c>error</c> (A2A #1262: a bare google.rpc.Status),
    /// is thrown. The common case (an object with a known member and no <c>error</c>) binds straight from the event's UTF-8
    /// bytes; the rest takes the <see cref="JsonDocument"/> path, so malformed events fail exactly as before.
    /// </summary>
    private static StreamResponse? ParseStreamEvent(Sse.SseFrame frame)
    {
        var errorFrame = string.Equals(frame.EventType, "error", StringComparison.Ordinal);
        var bytes = frame.Data.Span;
        // Invalid UTF-8 takes the text path below, where it becomes U+FFFD as it always did.
        if (!errorFrame && System.Text.Unicode.Utf8.IsValid(bytes))
        {
            Span<(int Start, int End)> found = stackalloc (int, int)[6];
            if (JsonMembers.TryScan(bytes, EventMembers, found, out var root, out var isObject) && isObject && found[0].Start < 0)
            {
                var known = false;
                foreach (var m in found[1..]) known |= m.Start >= 0;
                return known ? JsonSerializer.Deserialize(bytes[root], A2AJson.GetTypeInfo<StreamResponse>()) ?? throw ProtocolError("Empty stream event.") : null;
            }
        }
        var data = frame.DataText;
        using var doc = JsonDocument.Parse(data);
        var element = doc.RootElement;
        // A 1.1 StreamError (string code, spec §4.2.4) is an event; a google.rpc.Status (number code) ends the stream.
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var err)
            && !(err.ValueKind == JsonValueKind.Object && err.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String))
            throw ParseStatus(err, null);
        if (errorFrame) throw element.ValueKind == JsonValueKind.Object ? ParseStatus(element, null) : ProtocolError($"Stream error: {data}");
        if (!IsKnownStreamEvent(element)) return null;
        return element.Deserialize(A2AJson.GetTypeInfo<StreamResponse>()) ?? throw ProtocolError("Empty stream event.");
    }

    /// <summary>Parses a google.rpc.Status error body (spec §11.6) from its bytes; the text is read only for a non-A2A error.</summary>
    private static async Task<A2AException> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await ResponseBody.BufferAsync(resp.Content, ct).ConfigureAwait(false);
        if (IsJson(resp))
        {
            using var body = await ResponseBody.ReadUtf8Async(resp.Content, ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body.Memory);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
                    return ParseStatus(err, resp);
            }
            catch (JsonException)
            {
                // fall through
            }
        }
        return HttpError(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static A2AException ParseStatus(JsonElement err, HttpResponseMessage? resp)
    {
        var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
        List<JsonElement>? details = err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(e => e.Clone()).ToList()
            : null;
        if (!A2AErrors.TryGetCodeFromDetails(details, out var code))
        {
            code = (resp?.StatusCode ?? 0) switch
            {
                System.Net.HttpStatusCode.Unauthorized => A2AErrorCode.Unauthenticated,
                System.Net.HttpStatusCode.Forbidden => A2AErrorCode.PermissionDenied,
                System.Net.HttpStatusCode.NotFound => A2AErrorCode.TaskNotFound,
                System.Net.HttpStatusCode.BadRequest => A2AErrorCode.InvalidParams,
                _ => A2AErrorCode.Internal,
            };
        }
        return new A2AException(code, message) { Details = details, HttpStatus = resp is null ? null : (int)resp.StatusCode, RetryAfter = RetryAfterOf(resp, details) };
    }

    private sealed class Query
    {
        private readonly StringBuilder _sb = new();

        public Query Add(string name, string? value)
        {
            if (value is null) return this;
            _sb.Append(_sb.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            return this;
        }

        public Query Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

        public override string ToString() => _sb.ToString();
    }
}
