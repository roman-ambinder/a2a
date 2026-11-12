using System.Text.Json;
using A2A.Json;
using A2A.JsonRpc;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     JSON-RPC 2.0 binding (spec §9): one POST endpoint, SSE for streaming methods. Methods are looked up in the
///     <see cref="A2AOperationTable" />: the 11 built-in methods and the registered extension operations.
/// </summary>
/// <remarks>
///     <c>MapA2A</c> and <c>MapA2AJsonRpc</c> create and map it; a host that maps its own routes can create one with
///     <c>ActivatorUtilities.CreateInstance</c> and map <see cref="HandleAsync" />.
/// </remarks>
/// <param name="handler">The server core.</param>
/// <param name="table">The operations to serve.</param>
/// <param name="serverOptions">The server options (limits, streaming, error details).</param>
/// <param name="environment">The host environment (error details in Development).</param>
/// <param name="logger">The logger.</param>
public sealed class JsonRpcEndpoint(
    IA2ARequestHandler handler,
    A2AOperationTable table,
    IOptions<A2AServerOptions> serverOptions,
    IHostEnvironment environment,
    ILogger<JsonRpcEndpoint> logger)
{
    /// <summary>Display name of the JSON-RPC endpoints. The rate limiter uses it to pick the error format.</summary>
    internal const string DisplayName = "A2A JSON-RPC";

    private static readonly byte[] EnvelopeEnd = "}"u8.ToArray();
    private readonly bool _allowAnonymous = serverOptions.Value.AllowAnonymous;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
    private readonly TimeSpan _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;

    private readonly A2ALimits _limits = serverOptions.Value.Limits;

    /// <summary>Handles one JSON-RPC request: a single JSON-RPC object (no batches) in the body of a POST.</summary>
    /// <param name="http">The HTTP request.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task HandleAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        JsonElement? id = null;
        ServerCallContext call;
        try
        {
            call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.JsonRpc);
        }
        catch (Exception ex)
        {
            await WriteFailureAsync(http, ServerCallContext.Anonymous(), id,
                HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }

        try
        {
            A2AOperationDescriptor operation;
            object request;
            StreamRequest? live = null; // A2A 1.1 SendLiveMessage
            // The body lives in a pooled buffer for this block only. Everything that leaves it is a copy: the id and the
            // extension params are JsonElements with their own documents, and typed params are plain objects (any JsonElement
            // inside them is copied by the serializer). The buffer goes back to the pool before the handler runs.
            using (var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes)
                       .ConfigureAwait(false))
            {
                var json = body.Memory;
                string? jsonRpc, method;
                EnvelopeMembers members;
                try
                {
                    var root = HttpPlumbing.CheckJsonRpcBody(json, _limits.MaxJsonDepth, out members);
                    if (json.Span[root][0] != (byte)'{')
                        throw new A2AException(A2AErrorCode.InvalidRequest, "Batch requests are not supported");
                    // The id is read first, so a request whose other members do not bind still gets its id back.
                    if (members.Id.Start >= 0) id = Member<JsonElement>(json.Span, members.Id);
                    // JSON-RPC 2.0 (spec §9.3: requests MUST follow the JSON-RPC 2.0 format): "jsonrpc" MUST be exactly "2.0"; a missing member is InvalidRequest.
                    jsonRpc = members.JsonRpc.Start >= 0 ? Member<string>(json.Span, members.JsonRpc) : null;
                    method = members.Method.Start >= 0 ? Member<string>(json.Span, members.Method) : string.Empty;
                }
                catch (JsonException)
                {
                    throw new A2AException(A2AErrorCode.JsonParse);
                }

                if (jsonRpc != "2.0" || string.IsNullOrEmpty(method))
                    throw new A2AException(A2AErrorCode.InvalidRequest);
                if (id is { } idv &&
                    idv.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                    throw new A2AException(A2AErrorCode.InvalidRequest, "id must be a string, number, or null");

                handler.ValidateCall(call);

                // A2A 1.1: SendLiveMessage is a method of 1.1 only; each of its requests is authorized as the operation it runs.
                if (method == A2AProtocol.Methods.SendLiveMessage &&
                    A2AProtocol.IsVersion11OrLater(call.ProtocolVersion))
                {
                    live = Params<StreamRequest>(json.Span, members.Params);
                    operation = A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage);
                    request = live;
                }
                else if (!table.JsonRpcMethods.TryGetValue(method, out operation!))
                {
                    throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{method}' not found");
                }

                var p = members.Params;
                request = live is not null
                    ? live
                    : operation.Kind switch
                    {
                        A2AOperation.SendMessage or A2AOperation.SendStreamingMessage => Params<SendMessageRequest>(
                            json.Span, p),
                        A2AOperation.GetTask => Params<GetTaskRequest>(json.Span, p),
                        A2AOperation.ListTasks => Params<ListTasksRequest>(json.Span, p),
                        A2AOperation.CancelTask => Params<CancelTaskRequest>(json.Span, p),
                        A2AOperation.SubscribeToTask => Params<SubscribeToTaskRequest>(json.Span, p),
                        A2AOperation.CreateTaskPushNotificationConfig => Params<TaskPushNotificationConfig>(json.Span,
                            p),
                        A2AOperation.GetTaskPushNotificationConfig => Params<GetTaskPushNotificationConfigRequest>(
                            json.Span, p),
                        A2AOperation.ListTaskPushNotificationConfigs => Params<ListTaskPushNotificationConfigsRequest>(
                            json.Span, p),
                        A2AOperation.DeleteTaskPushNotificationConfig =>
                            Params<DeleteTaskPushNotificationConfigRequest>(json.Span, p),
                        A2AOperation.GetExtendedAgentCard => Params<GetExtendedAgentCardRequest>(json.Span, p),
                        A2AOperation.Extension => ParamsElement(json.Span, p),
                        _ => throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{method}' not found")
                    };
            }

            if (live is not null)
            {
                // The call whose Accept takes an event stream is the downlink (§3.1.12); any other call is an uplink call and
                // gets only the task as it is now.
                if (HttpPlumbing.AcceptsEventStream(http))
                    await StreamAsync(http, call, id, handler.SendLiveMessageAsync(One(live), call, ct),
                        Task.CompletedTask).ConfigureAwait(false);
                else
                    await Ok(http, call, id, await handler.SendLiveUplinkAsync(live, call, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                return;
            }

            switch (operation.Kind)
            {
                case A2AOperation.SendMessage:
                    await Ok(http, call, id,
                            await handler.SendMessageAsync((SendMessageRequest)request, call, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;
                case A2AOperation.SendStreamingMessage:
                {
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await StreamAsync(http, call, id,
                        handler.SendStreamingMessageAsync((SendMessageRequest)request, call,
                            HttpPlumbing.LastEventId(http), caughtUp.Signal, ct),
                        caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case A2AOperation.GetTask:
                    await Ok(http, call, id,
                            await handler.GetTaskAsync((GetTaskRequest)request, call, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;
                case A2AOperation.ListTasks:
                    await Ok(http, call, id,
                            await handler.ListTasksAsync((ListTasksRequest)request, call, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;
                case A2AOperation.CancelTask:
                    await Ok(http, call, id,
                            await handler.CancelTaskAsync((CancelTaskRequest)request, call, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;
                case A2AOperation.SubscribeToTask:
                {
                    var subscribe = (SubscribeToTaskRequest)request;
                    var caughtUp = HttpPlumbing.CaughtUpSignal();
                    await StreamAsync(http, call, id,
                        handler.SubscribeToTaskAsync(subscribe, call, HttpPlumbing.ResumeAfter(http, subscribe.Id),
                            caughtUp.Signal, ct),
                        caughtUp.Fired).ConfigureAwait(false);
                    break;
                }
                case A2AOperation.CreateTaskPushNotificationConfig:
                    await Ok(http, call, id,
                        await handler
                            .CreateTaskPushNotificationConfigAsync((TaskPushNotificationConfig)request, call, ct)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.GetTaskPushNotificationConfig:
                    await Ok(http, call, id,
                        await handler
                            .GetTaskPushNotificationConfigAsync((GetTaskPushNotificationConfigRequest)request, call, ct)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.ListTaskPushNotificationConfigs:
                    await Ok(http, call, id,
                        await handler
                            .ListTaskPushNotificationConfigsAsync((ListTaskPushNotificationConfigsRequest)request, call,
                                ct).ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.DeleteTaskPushNotificationConfig:
                    await handler
                        .DeleteTaskPushNotificationConfigAsync((DeleteTaskPushNotificationConfigRequest)request, call,
                            ct).ConfigureAwait(false);
                    await Ok(http, call, id, A2AJson.EmptyObject).ConfigureAwait(false);
                    break;
                case A2AOperation.GetExtendedAgentCard:
                    await Ok(http, call, id,
                        await handler.GetExtendedAgentCardAsync((GetExtendedAgentCardRequest)request, call, ct)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                case A2AOperation.Extension:
                {
                    var extension = table.GetExtension(operation.Name);
                    await ExtensionPolicy.RequireAsync(http, extension, call, _allowAnonymous).ConfigureAwait(false);
                    await Ok(http, call, id,
                        await handler.InvokeExtensionAsync(extension.Method, (JsonElement)request, call, ct)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{operation.Name}' not found");
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

            await WriteFailureAsync(http, call, id, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteFailureAsync(HttpContext http, ServerCallContext call, JsonElement? id,
        A2AException a2a)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, a2a, call).ConfigureAwait(false);
        var status = a2a.Code switch
        {
            A2AErrorCode.Unauthenticated => 401,
            A2AErrorCode.PermissionDenied => 403,
            _ when a2a.HttpStatus is { } explicitStatus => explicitStatus,
            _ => 200 // JSON-RPC errors travel in a 200 response
        };
        HttpPlumbing.SetRetryAfter(http, a2a);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        await HttpPlumbing.WriteJsonAsync(http, JsonRpcResponse.Failure(id, a2a), A2AProtocol.JsonMediaType, status)
            .ConfigureAwait(false);
    }

    /// <summary>A member of the envelope, bound as the envelope type would bind it (a <see cref="JsonElement" /> is a copy).</summary>
    private static T? Member<T>(ReadOnlySpan<byte> json, (int Start, int End) member)
    {
        return JsonSerializer.Deserialize(json[member.Start..member.End], A2AJson.GetTypeInfo<T>());
    }

    /// <summary>The params slice; an absent or null <c>params</c> is the empty object.</summary>
    private static ReadOnlySpan<byte> ParamsJson(ReadOnlySpan<byte> json, (int Start, int End) p)
    {
        return p.Start < 0 || json[p.Start] == (byte)'n' ? "{}"u8 : json[p.Start..p.End];
    }

    private static T Params<T>(ReadOnlySpan<byte> json, (int Start, int End) member)
    {
        var p = ParamsJson(json, member);
        if (p[0] != (byte)'{') throw A2AException.InvalidParams("params must be an object");
        try
        {
            // Proto field names (history_length) are accepted as ProtoJSON requires; the usual body binds from its bytes directly.
            return ProtoJsonFieldNames.Deserialize(p, A2AJson.GetTypeInfo<T>()) ??
                   throw A2AException.InvalidParams("params are required");
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Invalid params: {ex.Message}");
        }
    }

    /// <summary>The params of an extension operation as a <see cref="JsonElement" /> of its own (any JSON value).</summary>
    private static JsonElement ParamsElement(ReadOnlySpan<byte> json, (int Start, int End) member)
    {
        return member.Start < 0 || json[member.Start] == (byte)'n'
            ? A2AJson.EmptyObject
            : Member<JsonElement>(json, member);
    }

    private static async Task Ok<T>(HttpContext http, ServerCallContext call, JsonElement? id, T result)
    {
        HttpPlumbing.WriteExtensionsHeader(http, call);
        // One pass: the result is encoded straight into the envelope, into a pooled buffer, then written once. It is not
        // written to the response pipe while encoding, so an encoding failure still becomes a JSON-RPC error response.
        await PooledResponse.WriteAsync(http, 200, A2AProtocol.JsonMediaType,
            (id, result, info: A2AJson.GetTypeInfo<T>(call.ProtocolVersion)),
            static (w, s) => JsonRpcWriter.WriteSuccess(w, s.id, s.result, s.info)).ConfigureAwait(false);
    }

#pragma warning disable CS1998 // the one request of an HTTP downlink call, as an uplink
    private static async IAsyncEnumerable<StreamRequest> One(StreamRequest request)
    {
        yield return request;
    }
#pragma warning restore CS1998

    private async Task StreamAsync(HttpContext http, ServerCallContext call, JsonElement? id,
        IAsyncEnumerable<SequencedEvent> events, Task startNow)
    {
        var e = await HttpPlumbing.PrimeAsync(events, _keepAlive, startNow, http.RequestAborted).ConfigureAwait(false)
                ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
        HttpPlumbing.WriteExtensionsHeader(http, call);
        HttpPlumbing.StartEventStream(http);
        // The envelope around every event is the same: its start is built once, and each event's stored bytes go after it.
        await HttpPlumbing.PumpAsync(http, e,
            evt => EventEncoder.JsonFor(evt, call.ProtocolVersion), JsonRpcWriter.SuccessPrefix(id), EnvelopeEnd,
            ex => A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Failure(id,
                HttpPlumbing.Normalize(ex, logger, _includeDetails))),
            _keepAlive, logger).ConfigureAwait(false);
    }
}