using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Resilience;
using A2A.Client.Sse;
using A2A.Client.Streaming;
using A2A.Client.Transports;
using A2A.V0_3Compat;

namespace A2A.Client.V0_3Compat;

/// <summary>
///     The v0.3 HTTP+JSON binding. Routes live under <c>{base}/v1</c> (or <c>{base}/{tenant}/v1</c>) and bodies are
///     ProtoJSON
///     of the v0.3 proto (<see cref="V03ProtoJsonCodec" />). Subscribe uses GET, as the v0.3 proto says. The extended card
///     is
///     <c>GET /v1/card</c> and uses the JSON-RPC card dialect.
/// </summary>
internal sealed class V03HttpJsonTransport : HttpTransportBase, IA2ATransport, IResumableTransport, ITransportBinding
{
    private static readonly V03ProtoJsonCodec Codec = V03ProtoJsonCodec.Instance;
    private readonly string _baseUrl;

    public V03HttpJsonTransport(HttpClient httpClient, Uri baseUrl, A2AClientOptions? options = null)
        : base(httpClient, V03ClientSupport.CopyForV03(options))
    {
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).AbsoluteUri.TrimEnd('/');
    }

    public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var key = PrepareSend(request, options);
        var result = await SendAsync(HttpMethod.Post, Url(request.Tenant, "/message:send"), SendBody(request),
            RetryMode.UnsentOnly, options, key, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadSendResult(result), "message:send result");
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
        var query = request.HistoryLength is { } h ? "?historyLength=" + h.ToString(CultureInfo.InvariantCulture) : "";
        var result = await SendAsync(HttpMethod.Get, Url(request.Tenant, $"/tasks/{Esc(request.Id)}") + query, null,
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadTask(result), "task");
    }

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ListTasksCoreAsync(request, options, cancellationToken);
    }

    /// <summary>
    ///     v0.3 REST <c>GET /v1/tasks</c> answers every task of the caller as an array, with no paging or filters. The
    ///     request's filters are applied here, to that array: <c>contextId</c>, <c>status</c>,
    ///     <c>statusTimestampAfter</c>, <c>historyLength</c> and <c>includeArtifacts</c>. <c>pageSize</c> pages the filtered
    ///     array: the next page token is an offset into it (<c>v03-offset:n</c>), so a caller that pages on reaches every
    ///     task. Each page fetches the list again, so a page may repeat or skip a task that changed in between.
    /// </summary>
    private async Task<ListTasksResponse> ListTasksCoreAsync(ListTasksRequest request, A2ARequestOptions? options,
        CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, Url(request.Tenant, "/tasks"), null, RetryMode.Idempotent, options,
            null, cancellationToken).ConfigureAwait(false);
        var tasks = V03ClientSupport.Read(() =>
        {
            var array = result as JsonArray ?? throw new JsonException("tasks/list result must be an array");
            return array.Select(t => Codec.ReadTask(t)).ToList();
        }, "tasks/list result");

        IEnumerable<AgentTask> selected = tasks;
        if (request.ContextId is { Length: > 0 } contextId) selected = selected.Where(t => t.ContextId == contextId);
        if (request.Status is { } status) selected = selected.Where(t => t.Status.State == status);
        if (request.StatusTimestampAfter is { } after)
            selected = selected.Where(t => t.Status.Timestamp is { } at && at > after);
        var filtered = selected.ToList();
        var offset = 0;
        if (request.PageToken is { Length: > 0 } token)
        {
            if (!token.StartsWith(OffsetTokenPrefix, StringComparison.Ordinal) ||
                !int.TryParse(token.AsSpan(OffsetTokenPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                    out offset))
                throw new A2AException(A2AErrorCode.InvalidParams,
                    "pageToken did not come from this v0.3 HTTP+JSON client.");
        }

        var take = request.PageSize is > 0 and var size ? size : int.MaxValue;
        var list = filtered.Skip(offset).Take(take).ToList();
        var next = offset + list.Count < filtered.Count
            ? OffsetTokenPrefix + (offset + list.Count).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        foreach (var task in list)
        {
            if (request.IncludeArtifacts == false) task.Artifacts = null;
            if (request.HistoryLength is { } length && task.History is { } history && history.Count > length)
                task.History = length <= 0 ? null : history.GetRange(history.Count - length, length);
        }

        return new ListTasksResponse { Tasks = list, NextPageToken = next };
    }

    /// <summary>The page tokens of <see cref="ListTasksAsync" />: an offset into the filtered array.</summary>
    private const string OffsetTokenPrefix = "v03-offset:";

    public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The v0.3 proto CancelTaskRequest has only the task name: request metadata cannot be sent.
        if (request.Metadata is { Count: > 0 }) V03LossReporter.Drop("tasks/cancel.metadata");
        var body = new JsonObject { ["name"] = $"tasks/{request.Id}" };
        var result = await SendAsync(HttpMethod.Post, Url(request.Tenant, $"/tasks/{Esc(request.Id)}:cancel"), body,
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadTask(result), "task");
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
        var taskId = config.TaskId ?? string.Empty;
        var query = config.Id is { } id ? "?configId=" + Esc(id) : "";
        // The whole CreateTaskPushNotificationConfigRequest, as the a2a-sdk 0.3 Python client sends it and its server reads
        // it (the proto's http rule says the body is only the config). configId also goes in the query, for a server that
        // follows the http rule.
        var body = new JsonObject { ["parent"] = $"tasks/{taskId}", ["config"] = Codec.WritePushConfig(config) };
        if (config.Id is { Length: > 0 }) body["configId"] = config.Id;
        var result = await SendAsync(HttpMethod.Post,
            Url(config.Tenant, $"/tasks/{Esc(taskId)}/pushNotificationConfigs") + query, body,
            RetryMode.None, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadPushConfig(result), "push config");
    }

    public async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync(HttpMethod.Get,
            Url(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"), null,
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() => Codec.ReadPushConfig(result), "push config");
    }

    public async Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        if (request.PageSize is { } size)
            query.Append("?pageSize=").Append(size.ToString(CultureInfo.InvariantCulture));
        if (request.PageToken is { } token)
            query.Append(query.Length == 0 ? '?' : '&').Append("pageToken=").Append(Esc(token));
        var result = await SendAsync(HttpMethod.Get,
            Url(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs") + query, null,
            RetryMode.Idempotent, options, null, cancellationToken).ConfigureAwait(false);
        return V03ClientSupport.Read(() =>
        {
            var o = V03Json.Obj(result, "push config list");
            var configs = V03Json.Arr(o, "configs");
            return new ListTaskPushNotificationConfigsResponse
            {
                Configs = configs is null ? [] : configs.Select(c => Codec.ReadPushConfig(c!)).ToList(),
                NextPageToken = V03Json.Str(o, "nextPageToken") is { Length: > 0 } next ? next : null
            };
        }, "push config list");
    }

    public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        await SendRawAsync(HttpMethod.Delete,
            Url(request.Tenant, $"/tasks/{Esc(request.TaskId)}/pushNotificationConfigs/{Esc(request.Id)}"), null,
            RetryMode.None, options, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync(HttpMethod.Get, Url(request.Tenant, "/card"), null, RetryMode.Idempotent, options,
            null, cancellationToken).ConfigureAwait(false);
        // This route answers in the JSON-RPC card dialect (the v0.3 card schema), not ProtoJSON.
        return V03ClientSupport.Read(
            () => V03CardCodec.FromV03(result as JsonObject ?? throw new JsonException("card must be an object")),
            "extended card");
    }

    IAsyncEnumerable<StreamFrame> IResumableTransport.SendStreamingFramesAsync(SendMessageRequest request,
        A2ARequestOptions? options, CancellationToken cancellationToken)
    {
        return StreamAsync(HttpMethod.Post, Url(request.Tenant, "/message:stream"), SendBody(request), options,
            PrepareSend(request, options), null, cancellationToken);
    }

    IAsyncEnumerable<StreamFrame> IResumableTransport.SubscribeToTaskAsync(SubscribeToTaskRequest request,
        A2ARequestOptions? options, string? lastEventId, CancellationToken cancellationToken)
    {
        // The v0.3 proto declares GET for subscribe, and a2a-python's v0.3 client uses it.
        return StreamAsync(HttpMethod.Get, Url(request.Tenant, $"/tasks/{Esc(request.Id)}:subscribe"), null, options,
            null, lastEventId, cancellationToken);
    }

    string ITransportBinding.Binding => ProtocolBindings.HttpJson;

    // ---- plumbing ----

    private string Url(string? tenant, string path)
    {
        var t = tenant ?? Options.Tenant;
        return string.IsNullOrEmpty(t) ? $"{_baseUrl}/v1{path}" : $"{_baseUrl}/{Esc(t)}/v1{path}";
    }

    /// <summary>
    ///     One path segment. "." and ".." are refused: System.Uri removes dot segments, even escaped as %2E%2E, so the request
    ///     would go to another path of the same origin, with the same credentials.
    /// </summary>
    private static string Esc(string? s)
    {
        if (s is "." or "..") throw A2AException.InvalidParams("A path segment cannot be '.' or '..'.");
        return Uri.EscapeDataString(s ?? string.Empty);
    }

    /// <summary>The send body. The tenant travels in the URL, so it is not reported as dropped.</summary>
    private static JsonObject SendBody(SendMessageRequest request)
    {
        return Codec.WriteSendRequest(new SendMessageRequest
            { Message = request.Message, Configuration = request.Configuration, Metadata = request.Metadata });
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, JsonNode? body, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        return await SendRawAsync(method, url, body, retry, call, key, ct).ConfigureAwait(false) ??
               throw ProtocolError("The response body is empty.");
    }

    /// <summary>Sends one call. The result is null when the body is empty (push config delete).</summary>
    private async Task<JsonNode?> SendRawAsync(HttpMethod method, string url, JsonNode? body, RetryMode retry,
        A2ARequestOptions? call, string? key, CancellationToken ct)
    {
        var reply = await ExecuteUnaryAsync(retry, call, async attemptCt =>
        {
            using var req = await BuildRequestAsync(method, url, body, false, call, key, attemptCt)
                .ConfigureAwait(false);
            using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseContentRead, attemptCt)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, attemptCt).ConfigureAwait(false);
            using var bytes = await ResponseBody.ReadUtf8Async(resp.Content, attemptCt).ConfigureAwait(false);
            if (bytes.Span.IsEmpty || bytes.Span.TrimStart((byte)' ').IsEmpty)
                return new Reply(null);
            var node = V03ClientSupport.TryParse(bytes.Span) ??
                       throw ProtocolError("The response body is not valid JSON.");
            return new Reply(node);
        }, ct).ConfigureAwait(false);
        return reply.Value;
    }

    private async IAsyncEnumerable<StreamFrame> StreamAsync(HttpMethod method, string url, JsonNode? body,
        A2ARequestOptions? call, string? key, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = await BuildRequestAsync(method, url, body, true, call, key, ct, lastEventId)
            .ConfigureAwait(false);
        using var resp = await SendCheckedAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ReadErrorAsync(resp, ct).ConfigureAwait(false);
        if (!IsEventStream(resp)) throw ProtocolError("Expected text/event-stream.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var evt in SseReader.ReadEventsAsync(stream, SseReader.DefaultMaxEventBytes,
                           Options.StreamKeepAliveTimeout, Options.TimeProvider, ct).ConfigureAwait(false))
        {
            var node = V03ClientSupport.TryParse(V03ClientSupport.Utf8(evt.Data)) ??
                       throw ProtocolError("Malformed stream event: not JSON.");
            if (node is JsonObject o)
            {
                if (o["error"] is { } error) throw ErrorOf(error, null, null);
                // An event with only members this SDK does not know comes from a newer version: skip it.
                if (o["task"] is null && o["message"] is null && o["statusUpdate"] is null &&
                    o["artifactUpdate"] is null) continue;
            }

            var isFinal = false;
            var item = V03ClientSupport.Read(() => Codec.ReadStreamEvent(node, out isFinal), "stream event");
            yield return new StreamFrame(item, evt.FrameId);
            // v0.3 ends the stream on final: true. Some servers leave the connection open, so do not wait for it to close.
            if (isFinal) yield break;
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string url, JsonNode? body,
        bool streaming, A2ARequestOptions? call, string? key, CancellationToken ct, string? lastEventId = null)
    {
        var req = new HttpRequestMessage(method, url);
        try
        {
            if (body is not null)
            {
                req.Content = new ByteArrayContent(V03Json.ToUtf8(body));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(V03ClientSupport.JsonMediaType)
                    { CharSet = "utf-8" };
            }

            await ApplyHeadersAsync(req, streaming, call, key, ct, lastEventId).ConfigureAwait(false);
            V03ClientSupport.UseV03ExtensionsHeader(req);
        }
        catch
        {
            req.Dispose();
            throw;
        }

        return req;
    }

    /// <summary>
    ///     An error response. The body is <c>{"code":n,"message":…}</c>: <c>code</c> is used when it is an A2A code, else the
    ///     HTTP
    ///     status decides. A body that is not a JSON object becomes a plain HTTP error.
    /// </summary>
    private static async Task<A2AException> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await ResponseBody.BufferAsync(resp.Content, ct).ConfigureAwait(false);
        if (IsJson(resp))
        {
            using var body = await ResponseBody.ReadUtf8Async(resp.Content, ct).ConfigureAwait(false);
            if (V03ClientSupport.TryParse(body.Span) is JsonObject root)
            {
                var error = root["error"] as JsonObject ?? root;
                if (error.ContainsKey("code") || error.ContainsKey("message"))
                    return ErrorOf(error, resp, resp.StatusCode);
            }
        }

        return HttpError(resp, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static A2AException ErrorOf(JsonNode error, HttpResponseMessage? resp, HttpStatusCode? status)
    {
        var obj = error as JsonObject;
        var code = V03ClientSupport.CodeOf(V03ClientSupport.Long(obj?["code"]) ?? 0) ??
                   V03ClientSupport.FromStatus(status ?? 0);
        return new A2AException(code, V03ClientSupport.Text(obj?["message"]))
        {
            HttpStatus = resp is null ? null : (int)resp.StatusCode,
            RetryAfter = RetryAfterOf(resp, null)
        };
    }

    private sealed record Reply(JsonNode? Value);
}