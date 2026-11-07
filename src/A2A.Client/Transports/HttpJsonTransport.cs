using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>HTTP+JSON/REST binding (spec §11).</summary>
public sealed class HttpJsonTransport : HttpTransportBase, IA2AClient, IResumableTransport
{
    private readonly string _baseUrl;

    public HttpJsonTransport(HttpClient httpClient, Uri baseUrl, A2AClientOptions? options = null) : base(httpClient, options)
    {
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).AbsoluteUri.TrimEnd('/');
    }

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        return SendAsync<SendMessageResponse>(HttpMethod.Post, Path(request.Tenant, "/message:send"), request, retry: true, options, key, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken).Unframe(cancellationToken);

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request, A2ARequestOptions? options, CancellationToken cancellationToken) =>
        StreamAsync(HttpMethod.Post, Path(request.Tenant, "/message:stream"), request, options, PrepareSend(request, options), null, cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("historyLength", request.HistoryLength);
        return SendAsync<AgentTask>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.Id)}") + q, null, retry: true, options, null, cancellationToken);
    }

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
        return SendAsync<ListTasksResponse>(HttpMethod.Get, Path(request.Tenant, "/tasks") + q, null, retry: true, options, null, cancellationToken);
    }

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentTask>(HttpMethod.Post, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:cancel"), request, retry: true, options, null, cancellationToken);

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ((IResumableTransport)this).SubscribeToTaskAsync(request, options, null, cancellationToken).Unframe(cancellationToken);

    /// <summary>Subscribes and sends <c>Last-Event-ID</c> when <paramref name="lastEventId"/> is set. Frames carry their SSE ids.</summary>
    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        var method = Options.RestSubscribeMethod;
        return StreamAsync(method, Path(request.Tenant, $"/tasks/{Esc(request.Id)}:subscribe"), method == HttpMethod.Get ? null : request, options, null, lastEventId, cancellationToken);
    }

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(HttpMethod.Post, Path(config.Tenant, $"/tasks/{Esc(config.TaskId)}/pushNotificationConfigs"), config, retry: false, options, null, cancellationToken);

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<TaskPushNotificationConfig>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"), null, retry: true, options, null, cancellationToken);

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var q = new Query().Add("pageSize", request.PageSize).Add("pageToken", request.PageToken);
        return SendAsync<ListTaskPushNotificationConfigsResponse>(HttpMethod.Get, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs") + q, null, retry: true, options, null, cancellationToken);
    }

    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUnaryAsync(retry: false, options, async attemptCt =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, Path(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"));
            await ApplyHeadersAsync(req, streaming: false, options, null, attemptCt).ConfigureAwait(false);
            using var resp = await Http.SendAsync(req, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync<AgentCard>(HttpMethod.Get, Path(request.Tenant, "/extendedAgentCard"), null, retry: true, options, null, cancellationToken);

    private string Path(string? tenant, string path)
    {
        var t = tenant ?? Options.Tenant;
        return string.IsNullOrEmpty(t) ? _baseUrl + path : $"{_baseUrl}/{Esc(t)}{path}";
    }

    private static string Esc(string? s) => Uri.EscapeDataString(s ?? string.Empty);

    private Task<T> SendAsync<T>(HttpMethod method, string url, object? body, bool retry, A2ARequestOptions? call, string? key, CancellationToken ct) =>
        ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonBody(body, A2AProtocol.MediaType);
            await ApplyHeadersAsync(req, streaming: false, call, key, attemptCt).ConfigureAwait(false);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(attemptCt).ConfigureAwait(false);
            try { return A2AJson.Deserialize<T>(text); }
            catch (JsonException ex) { throw ProtocolError($"Malformed {typeof(T).Name} in agent response: {ex.Message}", ex); }
        }, ct);

    private async IAsyncEnumerable<StreamFrame> StreamAsync(HttpMethod method, string url, object? body, A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonBody(body, A2AProtocol.MediaType);
        await ApplyHeadersAsync(req, streaming: true, call, key, ct, lastEventId).ConfigureAwait(false);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, ct).ConfigureAwait(false);
        if (!IsEventStream(resp)) throw ProtocolError("Expected text/event-stream.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            StreamResponse? item;
            try
            {
                using var doc = JsonDocument.Parse(evt.Data);
                if (doc.RootElement.TryGetProperty("error", out var err)) throw ParseStatus(err, null);
                item = doc.RootElement.Deserialize(A2AJson.GetTypeInfo<StreamResponse>());
            }
            catch (JsonException ex) { throw ProtocolError($"Malformed stream event: {ex.Message}", ex); }
            if (item is null) throw ProtocolError("Empty stream event.");
            yield return new StreamFrame(item, evt.FrameId);
        }
    }

    /// <summary>Parses a google.rpc.Status error body (spec §11.6).</summary>
    private static async Task<A2AException> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (IsJson(resp) && !string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
                    return ParseStatus(err, resp);
            }
            catch (JsonException)
            {
                // fall through
            }
        }
        return HttpError(resp, body);
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
