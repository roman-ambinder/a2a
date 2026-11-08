using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Server.AspNetCore;
using A2A.Server.Streaming;
using A2A.Signing;
using A2A.V0_3Compat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.V0_3Compat;

/// <summary>
/// The v0.3 JSON-RPC methods and HTTP+JSON routes. Every operation goes through <see cref="IA2ARequestHandler"/> with a context
/// from <c>HttpPlumbing.CreateCallContext</c>, so authentication, ownership, tenancy, limits and filters are the code of the
/// v1.0 bindings. The request is converted to a v1.0 call, so the context says protocol version 1.0.
/// </summary>
internal sealed partial class V03Surface
{
    private const string JsonRpcBinding = ProtocolBindings.JsonRpc;
    private const string RestBinding = ProtocolBindings.HttpJson;

    private readonly IA2ARequestHandler _handler;
    private readonly ILogger _logger;
    private readonly A2ALimits _limits;
    private readonly TimeSpan _keepAlive;
    private readonly bool _includeDetails;

    public V03Surface(IA2ARequestHandler handler, IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILoggerFactory loggerFactory)
    {
        _handler = handler;
        _logger = loggerFactory.CreateLogger("A2A.Server.V0_3Compat");
        _limits = serverOptions.Value.Limits;
        _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;
        _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
        V03LossReporter.UseLogger(_logger);
    }

    private static readonly HashSet<string> JsonRpcMethods = new(StringComparer.Ordinal)
    {
        V03Compat.Methods.SendMessage,
        V03Compat.Methods.SendStreamingMessage,
        V03Compat.Methods.GetTask,
        V03Compat.Methods.CancelTask,
        V03Compat.Methods.ResubscribeTask,
        V03Compat.Methods.GetExtendedCard,
        V03Compat.Methods.SetPushConfig,
        V03Compat.Methods.GetPushConfig,
        V03Compat.Methods.ListPushConfig,
        V03Compat.Methods.DeletePushConfig,
        // Not a v0.3 method, but a v0.3 client may try it: answer MethodNotFound here, not "version not supported" from the core.
        ListTasksMethod,
    };

    private const string ListTasksMethod = "tasks/list";

    /// <summary>True when the wrapper takes this JSON-RPC method (the v0.3 methods and <c>tasks/list</c>), ignoring case.</summary>
    internal static bool IsV03Method(string method) =>
        JsonRpcMethods.Contains(method) || JsonRpcMethods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));

    // ======================================================================
    // JSON-RPC
    // ======================================================================

    /// <summary>
    /// Reads the body once (bounded, like the core endpoint), looks at <c>method</c>, and either handles a v0.3 method or hands
    /// the buffered body to <paramref name="core"/>. Malformed JSON and every other method go to the core, so its answers do not change.
    /// </summary>
    public RequestDelegate WrapJsonRpc(RequestDelegate core) => async http =>
    {
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            await core(http).ConfigureAwait(false);
            return;
        }

        PooledBody body;
        try { body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Any read failure (too large, bad request, client abort): the core endpoint answers it.
            // It reads the body again, so give it a stream that fails the same way.
            http.Request.Body = new FailingStream(ex);
            await core(http).ConfigureAwait(false);
            return;
        }

        JsonElement? id = null;
        string? method = null;
        JsonNode? parameters = null;
        string? jsonRpc = null;
        byte[]? passThrough = null;
        using (body)
        {
            try
            {
                var root = HttpPlumbing.CheckJsonRpcBody(body.Memory, _limits.MaxJsonDepth, out var members);
                var json = body.Memory.Span;
                if (json[root][0] == (byte)'{' && members.Method.Start >= 0)
                {
                    method = JsonSerializer.Deserialize(json[members.Method.Start..members.Method.End], A2AJson.GetTypeInfo<string>());
                    if (method is not null && JsonRpcMethods.Contains(method))
                    {
                        if (members.Id.Start >= 0) id = JsonSerializer.Deserialize(json[members.Id.Start..members.Id.End], A2AJson.GetTypeInfo<JsonElement>());
                        jsonRpc = members.JsonRpc.Start >= 0 ? JsonSerializer.Deserialize(json[members.JsonRpc.Start..members.JsonRpc.End], A2AJson.GetTypeInfo<string>()) : "2.0";
                        if (members.Params.Start >= 0 && json[members.Params.Start] != (byte)'n')
                            parameters = V03Json.Parse(json[members.Params.Start..members.Params.End]);
                    }
                    else method = null;
                }
            }
            catch (Exception ex) when (ex is JsonException or A2AException)
            {
                method = null; // the core reports it
            }
            if (method is null) passThrough = body.Memory.ToArray();
        }

        if (method is null)
        {
            http.Request.Body = new MemoryStream(passThrough!, writable: false);
            http.Request.ContentLength = passThrough!.Length;
            await core(http).ConfigureAwait(false);
            return;
        }

        await HandleJsonRpcAsync(http, method, jsonRpc, id, parameters).ConfigureAwait(false);
    };

    private async Task HandleJsonRpcAsync(HttpContext http, string method, string? jsonRpc, JsonElement? id, JsonNode? parameters)
    {
        var ct = http.RequestAborted;
        ServerCallContext call;
        try { call = CreateCall(http, JsonRpcBinding); }
        catch (Exception ex)
        {
            await WriteRpcFailureAsync(http, ServerCallContext.Anonymous(), id, HttpPlumbing.Normalize(ex, _logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            if (jsonRpc != "2.0") throw new A2AException(A2AErrorCode.InvalidRequest);
            if (id is { } idv && idv.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                throw new A2AException(A2AErrorCode.InvalidRequest, "id must be a string, number, or null");
            RequireNotV1Header(http);
            _handler.ValidateCall(call);
            V03LossReporter.Request(JsonRpcBinding, method);

            switch (method)
            {
                case V03Compat.Methods.SendMessage:
                {
                    var request = ReadParams(parameters, V03JsonCodec.Instance.ReadSendRequest);
                    RefusePush(request);
                    var result = await _handler.SendMessageAsync(request, call, ct).ConfigureAwait(false);
                    await RpcOkAsync(http, call, id, V03JsonCodec.Instance.WriteSendResult(result)).ConfigureAwait(false);
                    break;
                }
                case V03Compat.Methods.SendStreamingMessage:
                {
                    var request = ReadParams(parameters, V03JsonCodec.Instance.ReadSendRequest);
                    RefusePush(request);
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await RpcStreamAsync(http, call, id,
                        _handler.SendStreamingMessageAsync(request, call, HttpPlumbing.LastEventId(http), caughtUp.Signal, ct), caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case V03Compat.Methods.GetTask:
                {
                    var request = ReadParams(parameters, V03JsonCodec.ReadGetTaskParams);
                    var task = await _handler.GetTaskAsync(request, call, ct).ConfigureAwait(false);
                    await RpcOkAsync(http, call, id, V03JsonCodec.Instance.WriteTask(task)).ConfigureAwait(false);
                    break;
                }
                case V03Compat.Methods.CancelTask:
                {
                    var request = ReadParams(parameters, V03JsonCodec.ReadCancelParams);
                    var task = await _handler.CancelTaskAsync(request, call, ct).ConfigureAwait(false);
                    await RpcOkAsync(http, call, id, V03JsonCodec.Instance.WriteTask(task)).ConfigureAwait(false);
                    break;
                }
                case V03Compat.Methods.ResubscribeTask:
                {
                    var request = ReadParams(parameters, V03JsonCodec.ReadResubscribeParams);
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await RpcStreamAsync(http, call, id,
                        _handler.SubscribeToTaskAsync(request, call, HttpPlumbing.ResumeAfter(http, request.Id), caughtUp.Signal, ct), caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case V03Compat.Methods.GetExtendedCard:
                {
                    var card = await ExtendedCardAsync(call, ct).ConfigureAwait(false);
                    await RpcOkAsync(http, call, id, card).ConfigureAwait(false);
                    break;
                }
                case ListTasksMethod:
                    throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{method}' not found");
                default:
                    throw PushNotSupported(); // the four push config methods
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            var a2a = ex is JsonException json ? A2AException.InvalidParams($"Invalid params: {json.Message}") : HttpPlumbing.Normalize(ex, _logger, _includeDetails);
            if (http.Response.HasStarted)
            {
                ErrorAfterResponseStarted(_logger, ex);
                return;
            }
            await WriteRpcFailureAsync(http, call, id, a2a).ConfigureAwait(false);
        }
    }

    private static T ReadParams<T>(JsonNode? parameters, Func<JsonNode?, T> read) => read(parameters ?? new JsonObject());

    private static async Task RpcOkAsync(HttpContext http, ServerCallContext call, JsonElement? id, JsonNode result)
    {
        WriteExtensions(http, call);
        http.Response.StatusCode = 200;
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(V03Json.ToUtf8(V03JsonCodec.WriteSuccess(id, result)), http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteRpcFailureAsync(HttpContext http, ServerCallContext call, JsonElement? id, A2AException a2a)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, a2a, call).ConfigureAwait(false);
        var status = a2a.Code switch
        {
            A2AErrorCode.Unauthenticated => 401,
            A2AErrorCode.PermissionDenied => 403,
            _ when a2a.HttpStatus is { } explicitStatus => explicitStatus,
            _ => 200, // JSON-RPC errors travel in a 200 response
        };
        HttpPlumbing.SetRetryAfter(http, a2a);
        WriteExtensions(http, call);
        http.Response.StatusCode = status;
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(V03Json.ToUtf8(V03JsonCodec.WriteError(id, a2a)), http.RequestAborted).ConfigureAwait(false);
    }

    private async Task RpcStreamAsync(HttpContext http, ServerCallContext call, JsonElement? id, IAsyncEnumerable<SequencedEvent> events, Task startNow)
    {
        var primed = await HttpPlumbing.PrimeAsync(events, _keepAlive, startNow, http.RequestAborted).ConfigureAwait(false)
                     ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
        WriteExtensions(http, call);
        HttpPlumbing.StartEventStream(http);
        await HttpPlumbing.PumpAsync(http, primed,
            evt => V03Json.ToUtf8(V03JsonCodec.WriteSuccess(id, V03JsonCodec.Instance.WriteStreamEvent(evt.Event))),
            ex => V03Json.ToUtf8(V03JsonCodec.WriteError(id, HttpPlumbing.Normalize(ex, _logger, _includeDetails))),
            _keepAlive, _logger).ConfigureAwait(false);
    }

    // ======================================================================
    // HTTP+JSON
    // ======================================================================

    private const string RoutePrefix = "/v1";

    /// <summary>Maps the v0.3 routes into <paramref name="group"/>; each also gets a <c>/{tenant}</c> variant unless the pattern has a tenant.</summary>
    public void MapRest(RouteGroupBuilder group, bool tenantInPattern)
    {
        foreach (var tenantPrefix in tenantInPattern ? new[] { "" } : new[] { "", "/{tenant}" })
        {
            Map(group, "POST", tenantPrefix + RoutePrefix + "/message:send", SendMessage);
            Map(group, "POST", tenantPrefix + RoutePrefix + "/message:stream", SendStreamingMessage);
            Map(group, "GET", tenantPrefix + RoutePrefix + "/tasks/{id}", GetTask);
            Map(group, "POST", tenantPrefix + RoutePrefix + "/tasks/{id}:cancel", CancelTask);
            Map(group, "GET|POST", tenantPrefix + RoutePrefix + "/tasks/{id}:subscribe", Subscribe);
            Map(group, "GET", tenantPrefix + RoutePrefix + "/card", GetCard);
            // Push configs are refused (-32003), but the routes exist so the answer is the protocol's, not a 404.
            Map(group, "POST", tenantPrefix + RoutePrefix + "/tasks/{id}/pushNotificationConfigs", PushRefused);
            Map(group, "GET", tenantPrefix + RoutePrefix + "/tasks/{id}/pushNotificationConfigs", PushRefused);
            Map(group, "GET", tenantPrefix + RoutePrefix + "/tasks/{id}/pushNotificationConfigs/{configId}", PushRefused);
            Map(group, "DELETE", tenantPrefix + RoutePrefix + "/tasks/{id}/pushNotificationConfigs/{configId}", PushRefused);
        }
    }

    private static void Map(RouteGroupBuilder group, string methods, string route, RequestDelegate handler) =>
        // The display name starts with "A2A " so the startup check finds the endpoint and demands authorization for it.
        group.MapMethods(route, methods.Split('|'), handler).WithDisplayName($"A2A HTTP+JSON v0.3 {methods} {route}");

    private Task SendMessage(HttpContext http) => Run(http, "message:send", async (call, ct) =>
    {
        var request = V03ProtoJsonCodec.Instance.ReadSendRequest(await BodyAsync(http).ConfigureAwait(false));
        RefusePush(request);
        return V03ProtoJsonCodec.Instance.WriteSendResult(await _handler.SendMessageAsync(request, call, ct).ConfigureAwait(false));
    });

    private Task SendStreamingMessage(HttpContext http) => RunStream(http, "message:stream", async (call, caughtUp, ct) =>
    {
        var request = V03ProtoJsonCodec.Instance.ReadSendRequest(await BodyAsync(http).ConfigureAwait(false));
        RefusePush(request);
        return _handler.SendStreamingMessageAsync(request, call, HttpPlumbing.LastEventId(http), caughtUp, ct);
    });

    private Task GetTask(HttpContext http) => Run(http, "tasks/get", async (call, ct) =>
    {
        var request = new GetTaskRequest { Id = Route(http, "id"), HistoryLength = QueryInt(http, "historyLength"), Tenant = call.Tenant };
        return V03ProtoJsonCodec.Instance.WriteTask(await _handler.GetTaskAsync(request, call, ct).ConfigureAwait(false));
    });

    private Task CancelTask(HttpContext http) => Run(http, "tasks/cancel", async (call, ct) =>
    {
        var request = new CancelTaskRequest { Id = Route(http, "id") };
        return V03ProtoJsonCodec.Instance.WriteTask(await _handler.CancelTaskAsync(request, call, ct).ConfigureAwait(false));
    });

    private Task Subscribe(HttpContext http) => RunStream(http, "tasks/resubscribe", (call, caughtUp, ct) =>
    {
        var id = Route(http, "id");
        return Task.FromResult(_handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = id, Tenant = call.Tenant }, call, HttpPlumbing.ResumeAfter(http, id), caughtUp, ct));
    });

    private Task GetCard(HttpContext http) => Run(http, "card", async (call, ct) => await ExtendedCardAsync(call, ct).ConfigureAwait(false));

    private Task PushRefused(HttpContext http) => Run(http, "tasks/pushNotificationConfig", (_, _) => throw PushNotSupported());

    private async Task Run(HttpContext http, string operation, Func<ServerCallContext, CancellationToken, Task<JsonNode>> action)
    {
        ServerCallContext call;
        try { call = CreateCall(http, RestBinding); }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteRestErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, _logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            RequireNotV1Header(http);
            _handler.ValidateCall(call);
            V03LossReporter.Request(RestBinding, operation);
            var result = await action(call, http.RequestAborted).ConfigureAwait(false);
            WriteExtensions(http, call);
            http.Response.StatusCode = 200;
            http.Response.ContentType = A2AProtocol.JsonMediaType;
            await http.Response.Body.WriteAsync(V03Json.ToUtf8(result), http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteRestErrorAsync(http, call, RestError(ex)).ConfigureAwait(false);
        }
    }

    private async Task RunStream(HttpContext http, string operation,
        Func<ServerCallContext, Action, CancellationToken, Task<IAsyncEnumerable<SequencedEvent>>> action)
    {
        ServerCallContext call;
        try { call = CreateCall(http, RestBinding); }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteRestErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, _logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            RequireNotV1Header(http);
            _handler.ValidateCall(call);
            V03LossReporter.Request(RestBinding, operation);
            var caughtUp = HttpPlumbing.CaughtUpSignal();
            var events = await action(call, caughtUp.Signal, http.RequestAborted).ConfigureAwait(false);
            var primed = await HttpPlumbing.PrimeAsync(events, _keepAlive, caughtUp.Fired, http.RequestAborted).ConfigureAwait(false)
                         ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
            WriteExtensions(http, call);
            HttpPlumbing.StartEventStream(http);
            await HttpPlumbing.PumpAsync(http, primed,
                evt => V03Json.ToUtf8(V03ProtoJsonCodec.Instance.WriteStreamEvent(evt.Event)),
                ex =>
                {
                    var a2a = HttpPlumbing.Normalize(ex, _logger, _includeDetails);
                    return V03Json.ToUtf8(V03ProtoJsonCodec.WriteError(a2a, HttpPlumbing.StatusFor(a2a)));
                },
                _keepAlive, _logger, errorEventType: "error").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteRestErrorAsync(http, call, RestError(ex)).ConfigureAwait(false);
        }
    }

    private A2AException RestError(Exception ex) =>
        ex is JsonException json ? A2AException.InvalidParams($"Invalid request body: {json.Message}") : HttpPlumbing.Normalize(ex, _logger, _includeDetails);

    private static async Task WriteRestErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, ex, call).ConfigureAwait(false);
        HttpPlumbing.SetRetryAfter(http, ex);
        WriteExtensions(http, call);
        var status = HttpPlumbing.StatusFor(ex);
        http.Response.StatusCode = status;
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(V03Json.ToUtf8(V03ProtoJsonCodec.WriteError(ex, status)), http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The body as a JSON tree: read with the same limits as the v1.0 bindings, proto field names (<c>history_length</c>) rewritten.</summary>
    private async Task<JsonNode> BodyAsync(HttpContext http)
    {
        using var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false);
        if (body.Length == 0) throw A2AException.InvalidParams("A request body is required");
        try
        {
            var root = HttpPlumbing.CheckDepthAndDuplicates(body.Memory, _limits.MaxJsonDepth);
            using var doc = JsonDocument.Parse(body.Memory[root], new JsonDocumentOptions { MaxDepth = A2AJson.MaxJsonDepth });
            return V03Json.ToNode(ProtoJsonFieldNames.Normalize(doc.RootElement));
        }
        catch (JsonException ex) { throw A2AException.InvalidParams($"Invalid request body: {ex.Message}"); }
    }

    private static string Route(HttpContext http, string name) =>
        http.GetRouteValue(name) as string ?? throw A2AException.InvalidParams($"{name} is required");

    /// <summary>A query parameter by its JSON name (<c>historyLength</c>) or its proto name (<c>history_length</c>).</summary>
    private static int? QueryInt(HttpContext http, string name)
    {
        string? value = http.Request.Query[name];
        if (string.IsNullOrEmpty(value))
        {
            var snake = new System.Text.StringBuilder();
            foreach (var c in name) snake.Append(char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : c.ToString());
            value = http.Request.Query[snake.ToString()];
        }
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
            ? i : throw A2AException.InvalidParams($"{name} must be an integer");
    }

    // ======================================================================
    // Shared
    // ======================================================================

    private static ServerCallContext CreateCall(HttpContext http, string binding)
    {
        // v0.3 clients send X-A2A-Extensions; the core reads A2A-Extensions.
        var headers = http.Request.Headers;
        if (!headers.ContainsKey(A2AProtocol.Headers.Extensions) && headers.TryGetValue(V03Compat.ExtensionsHeader, out var extensions))
            headers[A2AProtocol.Headers.Extensions] = extensions;
        // The converted request is a v1.0 call; the caller spoke v0.3 on the wire.
        return HttpPlumbing.CreateCallContext(http, binding, A2AProtocol.Version, wireVersion: A2AProtocol.ImplicitVersion);
    }

    private static void WriteExtensions(HttpContext http, ServerCallContext call)
    {
        HttpPlumbing.WriteExtensionsHeader(http, call);
        if (call.ActivatedExtensions.Count > 0 && !http.Response.HasStarted)
            http.Response.Headers[V03Compat.ExtensionsHeader] = string.Join(",", call.ActivatedExtensions);
    }

    /// <summary>The shape picked the v0.3 surface; a header of version 1.0 or later can only veto it.</summary>
    private static void RequireNotV1Header(HttpContext http)
    {
        string? version = http.Request.Headers[A2AProtocol.Headers.Version];
        if (string.IsNullOrWhiteSpace(version)) version = http.Request.Query[A2AProtocol.Headers.Version];
        if (!V03Compat.IsV1OrLater(version)) return;
        throw new A2AException(A2AErrorCode.VersionNotSupported,
            $"This is a protocol v0.3 request, but {A2AProtocol.Headers.Version} says '{version}'. Send v1.0 methods (for example SendMessage) with v1.0, and v0.3 methods without a v1.0 version.")
        {
            Metadata = new() { ["requestedVersion"] = version!.Trim() },
        };
    }

    private static A2AException PushNotSupported() =>
        new(A2AErrorCode.PushNotificationNotSupported, "Push notifications are not supported on the v0.3 interface of this agent.");

    private static void RefusePush(SendMessageRequest request)
    {
        if (request.Configuration?.TaskPushNotificationConfig is not null) throw PushNotSupported();
    }

    /// <summary>The extended card in v0.3 form (a pure v0.3 card; it says no push notifications, as v0.3 push is refused).</summary>
    private async Task<JsonNode> ExtendedCardAsync(ServerCallContext call, CancellationToken ct)
    {
        var card = await _handler.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest { Tenant = call.Tenant }, call, ct).ConfigureAwait(false);
        var v1 = AgentCardCanonicalizer.ToNode(card);
        // An extended card the host built by hand may not list interfaces: take them from the public card.
        if (v1["supportedInterfaces"] is not JsonArray { Count: > 0 })
            v1["supportedInterfaces"] = AgentCardCanonicalizer.ToNode(_handler.AgentCard)["supportedInterfaces"]?.DeepClone();
        if (!V03CardCodec.TryToV03(v1, pushNotifications: false, out var v03, out var problem))
            throw new A2AException(A2AErrorCode.Internal, $"The extended Agent Card cannot be served to v0.3 clients: {problem}");
        return v03;
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Error after v0.3 JSON-RPC response started")]
    private static partial void ErrorAfterResponseStarted(ILogger logger, Exception exception);

    /// <summary>A body stream that throws <paramref name="error"/> on the first read.</summary>
    private sealed class FailingStream(Exception error) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw error;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw error;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
