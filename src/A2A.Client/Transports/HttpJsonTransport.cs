using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Client.Telemetry;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>HTTP+JSON/REST binding (spec §11).</summary>
public sealed class HttpJsonTransport : HttpTransportBase, IA2AClient, IResumableTransport
{
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
        return SendAsync<SendMessageResponse>(A2AProtocol.Methods.SendMessage, HttpMethod.Post, Path(request.Tenant, "/message:send"), request, RetryMode.UnsentOnly, options, key, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken).Unframe(cancellationToken);

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options, CancellationToken cancellationToken) =>
        StreamAsync(A2AProtocol.Methods.SendStreamingMessage, HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken);

    /// <inheritdoc />
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("historyLength", request.HistoryLength);
        return SendAsync<AgentTask>(A2AProtocol.Methods.GetTask, HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.Id)}") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
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
        return SendAsync<ListTasksResponse>(A2AProtocol.Methods.ListTasks, HttpMethod.Get, Path(request.Tenant, "/tasks") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentTask>(A2AProtocol.Methods.CancelTask, HttpMethod.Post, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:cancel"), request, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ((IResumableTransport)this).SubscribeToTaskAsync(request, options, null, cancellationToken).Unframe(cancellationToken);

    /// <summary>Subscribes and sends <c>Last-Event-ID</c> when <paramref name="lastEventId"/> is set. Frames carry their SSE ids.</summary>
    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        var method = Options.RestSubscribeMethod;
        return StreamAsync(A2AProtocol.Methods.SubscribeToTask, method, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:subscribe"), method == HttpMethod.Get ? null : request, options, null, lastEventId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(A2AProtocol.Methods.CreateTaskPushNotificationConfig, HttpMethod.Post, Path(config.Tenant, $"/tasks/{Esc(config.TaskId)}/pushNotificationConfigs"), config, RetryMode.None, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(A2AProtocol.Methods.GetTaskPushNotificationConfig, HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"), null, RetryMode.Idempotent, options, null, cancellationToken);

    /// <inheritdoc />
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("pageSize", request.PageSize).Add("pageToken", request.PageToken);
        return SendAsync<ListTaskPushNotificationConfigsResponse>(A2AProtocol.Methods.ListTaskPushNotificationConfigs, HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs") + q, null, RetryMode.Idempotent, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUnaryAsync(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, RetryMode.None, options, async attemptCt =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"));
            await ApplyHeadersAsync(req, streaming: false, options, null, attemptCt).ConfigureAwait(false);
            using var resp = await Http.SendAsync(req, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentCard>(A2AProtocol.Methods.GetExtendedAgentCard, HttpMethod.Get, Path(request.Tenant, "/extendedAgentCard"), null, RetryMode.Idempotent, options, null, cancellationToken);

    private string Path(string? tenant, string path)
    {
        var t = tenant ?? Options.Tenant;
        return string.IsNullOrEmpty(t) ? _baseUrl + path : $"{_baseUrl}/{Esc(t)}{path}";
    }

    private static string Esc(string? s) => Uri.EscapeDataString(s ?? string.Empty);

    private Task<T> SendAsync<T>(string operation, HttpMethod method, string url, object? body, RetryMode retry, A2ARequestOptions? call, string? key, CancellationToken ct) =>
        ExecuteUnaryAsync(operation, retry, call, async attemptCt =>
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonBody(body, A2AProtocol.MediaType);
            await ApplyHeadersAsync(req, streaming: false, call, key, attemptCt).ConfigureAwait(false);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            try
            {
                return await ResponseBody.DeserializeAsync<T>(resp.Content, attemptCt).ConfigureAwait(false)
                    ?? throw new JsonException($"Null {typeof(T).Name} payload.");
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed {typeof(T).Name} in agent response: {ex.Message}", ex); }
        }, ct);

    private async IAsyncEnumerable<StreamFrame> StreamAsync(string operation, HttpMethod method, string url, object? body, A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        // The span covers opening the stream: from the request to the event-stream response headers.
        var activity = A2AClientTelemetry.Start(operation);
        HttpRequestMessage? opening = null;
        HttpResponseMessage? opened = null;
        try
        {
            opening = new HttpRequestMessage(method, url);
            if (body is not null) opening.Content = JsonBody(body, A2AProtocol.MediaType);
            await ApplyHeadersAsync(opening, streaming: true, call, key, ct, lastEventId).ConfigureAwait(false);
            opened = await Http.SendAsync(opening, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!opened.IsSuccessStatusCode) throw await ReadErrorAsync(opened, ct).ConfigureAwait(false);
            if (!IsEventStream(opened)) throw ProtocolError("Expected text/event-stream.");
        }
        catch (Exception ex)
        {
            opened?.Dispose();
            opening?.Dispose();
            A2AClientTelemetry.Stop(activity, ex, ct);
            throw;
        }
        A2AClientTelemetry.Stop(activity, null, ct);

        using var req = opening;
        using var resp = opened;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            StreamResponse? item;
            try
            {
                item = ParseStreamEvent(evt.Data);
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed stream event: {ex.Message}", ex); }
            if (item is null) throw ProtocolError("Empty stream event.");
            yield return new StreamFrame(item, evt.FrameId);
        }
    }

    private static readonly byte[][] ErrorName = ["error"u8.ToArray()];

    /// <summary>
    /// One stream event: a google.rpc.Status <c>error</c> member is thrown, anything else binds as a <see cref="StreamResponse"/>.
    /// The common case (an object without <c>error</c>) binds straight from the event's UTF-8 bytes; the rest takes the former
    /// <see cref="JsonDocument"/> path, so malformed events fail exactly as before.
    /// </summary>
    private static StreamResponse? ParseStreamEvent(string data)
    {
        using (var bytes = ResponseBody.FromString(data))
        {
            Span<(int Start, int End)> found = stackalloc (int, int)[1];
            if (JsonMembers.TryScan(bytes.Span, ErrorName, found, out var root, out var isObject) && isObject && found[0].Start < 0)
                return JsonSerializer.Deserialize(bytes.Span[root], A2AJson.GetTypeInfo<StreamResponse>());
        }
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("error", out var err)) throw ParseStatus(err, null);
        return doc.RootElement.Deserialize(A2AJson.GetTypeInfo<StreamResponse>());
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
