using System.Text.Json;
using A2A.Json;
using A2A.JsonRpc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>JSON-RPC 2.0 binding (spec §9): one POST endpoint, SSE for streaming methods.</summary>
internal sealed class JsonRpcEndpoint(A2ARequestHandler handler, IOptions<A2AHttpOptions> httpOptions,
    IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<JsonRpcEndpoint> logger)
{
    /// <summary>Display name of the JSON-RPC endpoints. The rate limiter uses it to pick the error format.</summary>
    internal const string DisplayName = "A2A JSON-RPC";

    private readonly A2AHttpOptions _http = httpOptions.Value;
    private readonly A2ALimits _limits = serverOptions.Value.Limits;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);

    public async Task HandleAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        JsonElement? id = null;
        ServerCallContext call;
        try { call = HttpPlumbing.CreateCallContext(http); }
        catch (Exception ex)
        {
            await WriteFailureAsync(http, ServerCallContext.Anonymous(), id, HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            JsonRpcRequest rpc;
            try
            {
                var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false);
                using var doc = HttpPlumbing.ParseBody(body, _limits.MaxJsonDepth);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new A2AException(A2AErrorCode.InvalidRequest, "Batch requests are not supported");
                if (doc.RootElement.TryGetProperty("id", out var rawId)) id = rawId.Clone();
                rpc = doc.RootElement.Deserialize(A2AJson.GetTypeInfo<JsonRpcRequest>())!;
            }
            catch (JsonException)
            {
                throw new A2AException(A2AErrorCode.JsonParse);
            }

            if (rpc.JsonRpc != "2.0" || string.IsNullOrEmpty(rpc.Method))
                throw new A2AException(A2AErrorCode.InvalidRequest);
            if (id is { } idv && idv.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                throw new A2AException(A2AErrorCode.InvalidRequest, "id must be a string, number, or null");

            handler.ValidateCall(call);
            var p = rpc.Params ?? EmptyObject;

            switch (rpc.Method)
            {
                case A2AProtocol.Methods.SendMessage:
                    await Ok(http, call, id, await handler.SendMessageAsync(Params<SendMessageRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.SendStreamingMessage:
                    await StreamAsync(http, call, id, handler.SendStreamingMessageAsync(Params<SendMessageRequest>(p), call, ct)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.GetTask:
                    await Ok(http, call, id, await handler.GetTaskAsync(Params<GetTaskRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.ListTasks:
                    await Ok(http, call, id, await handler.ListTasksAsync(Params<ListTasksRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.CancelTask:
                    await Ok(http, call, id, await handler.CancelTaskAsync(Params<CancelTaskRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.SubscribeToTask:
                    await StreamAsync(http, call, id, handler.SubscribeToTaskAsync(Params<SubscribeToTaskRequest>(p), call, ct)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.CreateTaskPushNotificationConfig:
                    await Ok(http, call, id, await handler.CreateTaskPushNotificationConfigAsync(Params<TaskPushNotificationConfig>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.GetTaskPushNotificationConfig:
                    await Ok(http, call, id, await handler.GetTaskPushNotificationConfigAsync(Params<GetTaskPushNotificationConfigRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.ListTaskPushNotificationConfigs:
                    await Ok(http, call, id, await handler.ListTaskPushNotificationConfigsAsync(Params<ListTaskPushNotificationConfigsRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.DeleteTaskPushNotificationConfig:
                    await handler.DeleteTaskPushNotificationConfigAsync(Params<DeleteTaskPushNotificationConfigRequest>(p), call, ct).ConfigureAwait(false);
                    await Ok(http, call, id, A2AJson.EmptyObject).ConfigureAwait(false);
                    break;
                case A2AProtocol.Methods.GetExtendedAgentCard:
                    await Ok(http, call, id, await handler.GetExtendedAgentCardAsync(Params<GetExtendedAgentCardRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                default:
                    throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{rpc.Method}' not found");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            if (http.Response.HasStarted)
            {
                logger.LogWarning(ex, "Error after JSON-RPC response started");
                return;
            }
            await WriteFailureAsync(http, call, id, HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
        }
    }

    private async Task WriteFailureAsync(HttpContext http, ServerCallContext call, JsonElement? id, A2AException a2a)
    {
        HttpPlumbing.SetAuthChallenge(http, a2a, handler.AgentCard);
        var status = a2a.Code switch
        {
            A2AErrorCode.Unauthenticated => 401,
            A2AErrorCode.PermissionDenied => 403,
            _ when a2a.HttpStatus is { } explicitStatus => explicitStatus,
            _ => 200, // JSON-RPC errors travel in a 200 response
        };
        HttpPlumbing.SetRetryAfter(http, a2a);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        await HttpPlumbing.WriteJsonAsync(http, JsonRpcResponse.Failure(id, a2a), A2AProtocol.JsonMediaType, status).ConfigureAwait(false);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static T Params<T>(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) throw A2AException.InvalidParams("params must be an object");
        try
        {
            return p.Deserialize(A2AJson.GetTypeInfo<T>()) ?? throw A2AException.InvalidParams("params are required");
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Invalid params: {ex.Message}");
        }
    }

    private static Task Ok<T>(HttpContext http, ServerCallContext call, JsonElement? id, T result)
    {
        HttpPlumbing.WriteExtensionsHeader(http, call);
        return HttpPlumbing.WriteJsonAsync(http, JsonRpcResponse.Success(id, A2AJson.SerializeToElement(result)), A2AProtocol.JsonMediaType);
    }

    private async Task StreamAsync(HttpContext http, ServerCallContext call, JsonElement? id, IAsyncEnumerable<StreamResponse> events)
    {
        var e = await HttpPlumbing.PrimeAsync(events, http.RequestAborted).ConfigureAwait(false)
                ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
        HttpPlumbing.WriteExtensionsHeader(http, call);
        HttpPlumbing.StartEventStream(http);
        await HttpPlumbing.PumpAsync(http, e,
            evt => A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Success(id, A2AJson.SerializeToElement(evt))),
            ex => A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Failure(id, HttpPlumbing.Normalize(ex, logger, _includeDetails))),
            _http.StreamKeepAliveInterval, logger).ConfigureAwait(false);
    }
}
