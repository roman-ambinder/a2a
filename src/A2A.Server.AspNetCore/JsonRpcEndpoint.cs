using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;
using A2A.JsonRpc;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// JSON-RPC 2.0 binding (spec §9): one POST endpoint, SSE for streaming methods. Methods are looked up in the
/// <see cref="A2AOperationTable"/>: the 11 built-in methods and the registered extension operations.
/// </summary>
/// <remarks>
/// <c>MapA2A</c> and <c>MapA2AJsonRpc</c> create and map it; a host that maps its own routes can create one with
/// <c>ActivatorUtilities.CreateInstance</c> and map <see cref="HandleAsync"/>.
/// </remarks>
/// <param name="handler">The server core.</param>
/// <param name="table">The operations to serve.</param>
/// <param name="serverOptions">The server options (limits, streaming, error details).</param>
/// <param name="environment">The host environment (error details in Development).</param>
/// <param name="logger">The logger.</param>
public sealed class JsonRpcEndpoint(IA2ARequestHandler handler, A2AOperationTable table,
    IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<JsonRpcEndpoint> logger)
{
    /// <summary>Display name of the JSON-RPC endpoints. The rate limiter uses it to pick the error format.</summary>
    internal const string DisplayName = "A2A JSON-RPC";

    private readonly A2ALimits _limits = serverOptions.Value.Limits;
    private readonly TimeSpan _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
    private readonly bool _allowAnonymous = serverOptions.Value.AllowAnonymous;

    /// <summary>Handles one JSON-RPC request: a single JSON-RPC object (no batches) in the body of a POST.</summary>
    /// <param name="http">The HTTP request.</param>
    /// <returns>A task that completes when the response is written.</returns>
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
            var p = rpc.Params ?? A2AJson.EmptyObject;

            if (!table.JsonRpcMethods.TryGetValue(rpc.Method, out var operation))
                throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{rpc.Method}' not found");

            switch (operation.Kind)
            {
                case A2AOperation.SendMessage:
                    await Ok(http, call, id, await handler.SendMessageAsync(Params<SendMessageRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.SendStreamingMessage:
                {
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await StreamAsync(http, call, id,
                        handler.SendStreamingMessageAsync(Params<SendMessageRequest>(p), call, HttpPlumbing.LastEventId(http), caughtUp.Signal, ct),
                        caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case A2AOperation.GetTask:
                    await Ok(http, call, id, await handler.GetTaskAsync(Params<GetTaskRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.ListTasks:
                    await Ok(http, call, id, await handler.ListTasksAsync(Params<ListTasksRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.CancelTask:
                    await Ok(http, call, id, await handler.CancelTaskAsync(Params<CancelTaskRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.SubscribeToTask:
                {
                    var subscribe = Params<SubscribeToTaskRequest>(p);
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await StreamAsync(http, call, id,
                        handler.SubscribeToTaskAsync(subscribe, call, HttpPlumbing.ResumeAfter(http, subscribe.Id), caughtUp.Signal, ct),
                        caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case A2AOperation.CreateTaskPushNotificationConfig:
                    await Ok(http, call, id, await handler.CreateTaskPushNotificationConfigAsync(Params<TaskPushNotificationConfig>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.GetTaskPushNotificationConfig:
                    await Ok(http, call, id, await handler.GetTaskPushNotificationConfigAsync(Params<GetTaskPushNotificationConfigRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.ListTaskPushNotificationConfigs:
                    await Ok(http, call, id, await handler.ListTaskPushNotificationConfigsAsync(Params<ListTaskPushNotificationConfigsRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.DeleteTaskPushNotificationConfig:
                    await handler.DeleteTaskPushNotificationConfigAsync(Params<DeleteTaskPushNotificationConfigRequest>(p), call, ct).ConfigureAwait(false);
                    await Ok(http, call, id, A2AJson.EmptyObject).ConfigureAwait(false);
                    break;
                case A2AOperation.GetExtendedAgentCard:
                    await Ok(http, call, id, await handler.GetExtendedAgentCardAsync(Params<GetExtendedAgentCardRequest>(p), call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.Extension:
                {
                    var extension = table.GetExtension(operation.Name);
                    await ExtensionPolicy.RequireAsync(http, extension, call, _allowAnonymous).ConfigureAwait(false);
                    await Ok(http, call, id, await handler.InvokeExtensionAsync(extension.Method, p, call, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                }
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
                AspNetCoreLog.ErrorAfterResponseStarted(logger, ex);
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

    private static async Task Ok<T>(HttpContext http, ServerCallContext call, JsonElement? id, T result)
    {
        HttpPlumbing.WriteExtensionsHeader(http, call);
        // One pass: the result is encoded straight into the envelope, into a pooled buffer, then written once. It is not
        // written to the response pipe while encoding, so an encoding failure still becomes a JSON-RPC error response.
        var body = JsonRpcWriter.Success(id, result, A2AJson.GetTypeInfo<T>());
        http.Response.StatusCode = 200;
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        await http.Response.Body.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
    }

    private async Task StreamAsync(HttpContext http, ServerCallContext call, JsonElement? id, IAsyncEnumerable<SequencedEvent> events, Task startNow)
    {
        var e = await HttpPlumbing.PrimeAsync(events, _keepAlive, startNow, http.RequestAborted).ConfigureAwait(false)
                ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
        HttpPlumbing.WriteExtensionsHeader(http, call);
        HttpPlumbing.StartEventStream(http);
        await HttpPlumbing.PumpAsync(http, e,
            evt => JsonRpcWriter.Success(id, EventEncoder.EnsureEncoded(evt).Json.Span),
            ex => A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Failure(id, HttpPlumbing.Normalize(ex, logger, _includeDetails))),
            _keepAlive, logger).ConfigureAwait(false);
    }
}
