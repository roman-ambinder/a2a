using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using A2A.Json;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Streaming;
using A2A.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     HTTP+JSON/REST binding (spec §11). Errors use the google.rpc.Status shape (§11.6). The routes come from the
///     <see cref="A2AOperationTable" />: the 11 built-in routes and <c>POST /ext/{method}</c> per extension operation.
/// </summary>
/// <remarks>
///     <c>MapA2A</c> and <c>MapA2AHttpJson</c> create and map it; a host that maps its own route groups can create one
///     with
///     <c>ActivatorUtilities.CreateInstance</c> and call <see cref="Map" />.
/// </remarks>
/// <param name="handler">The server core.</param>
/// <param name="table">The operations to serve.</param>
/// <param name="serverOptions">The server options (limits, streaming, error details).</param>
/// <param name="environment">The host environment (error details in Development).</param>
/// <param name="logger">The logger.</param>
public sealed class RestEndpoint(
    IA2ARequestHandler handler,
    A2AOperationTable table,
    IOptions<A2AServerOptions> serverOptions,
    IHostEnvironment environment,
    ILogger<RestEndpoint> logger)
{
    private readonly bool _allowAnonymous = serverOptions.Value.AllowAnonymous;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
    private readonly TimeSpan _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;
    private readonly A2ALimits _limits = serverOptions.Value.Limits;

    /// <summary>
    ///     Maps the routes of the <see cref="A2AOperationTable" /> under <paramref name="group" />. Unless the group's own
    ///     pattern
    ///     already has a <c>{tenant}</c> segment (<paramref name="tenantInPattern" />), every route also gets a
    ///     <c>/{tenant}</c> variant.
    /// </summary>
    /// <param name="group">The route group (or app) to map into.</param>
    /// <param name="tenantInPattern">True when the group's pattern has a <c>{tenant}</c> segment.</param>
    public void Map(IEndpointRouteBuilder group, bool tenantInPattern = false)
    {
        ArgumentNullException.ThrowIfNull(group);
        foreach (var prefix in tenantInPattern ? new[] { "" } : new[] { "", "/{tenant}" })
        {
            foreach (var (method, route, operation) in table.RestRoutes)
                // The display name starts with "A2A " so the startup check can find every A2A endpoint.
                group.MapMethods(prefix + route, method.Split('|'), HandlerFor(operation))
                    .WithDisplayName($"A2A HTTP+JSON {method} {prefix + route}");
            // A2A 1.1 SendLiveMessage (§3.1.12). Each request is authorized as the operation it runs.
            group.MapMethods(prefix + "/message:live", ["POST"], SendLiveMessage)
                .WithDisplayName($"A2A HTTP+JSON POST {prefix}/message:live");
        }
    }

    /// <summary>
    ///     The call whose <c>Accept</c> takes an event stream is the downlink; any other call is an uplink call and gets only
    ///     the
    ///     task as it is now. A 1.0 caller gets UnsupportedOperation (the operation is A2A 1.1).
    /// </summary>
    private Task SendLiveMessage(HttpContext http)
    {
        return HttpPlumbing.AcceptsEventStream(http)
            ? RunStream(http, async (call, caughtUp, ct) =>
            {
                var req = await Body<StreamRequest>(http).ConfigureAwait(false);
                req.Tenant ??= call.Tenant;
                return handler.SendLiveMessageAsync(One(req), call, ct);
            })
            : Run(http, async (call, ct) =>
            {
                var req = await Body<StreamRequest>(http).ConfigureAwait(false);
                req.Tenant ??= call.Tenant;
                return await handler.SendLiveUplinkAsync(req, call, ct).ConfigureAwait(false);
            });
    }

#pragma warning disable CS1998 // the one request of an HTTP downlink call, as an uplink
    private static async IAsyncEnumerable<StreamRequest> One(StreamRequest request)
    {
        yield return request;
    }
#pragma warning restore CS1998

    private RequestDelegate HandlerFor(A2AOperationDescriptor operation)
    {
        return operation.Kind switch
        {
            A2AOperation.SendMessage => SendMessage,
            A2AOperation.SendStreamingMessage => SendStreamingMessage,
            A2AOperation.ListTasks => ListTasks,
            A2AOperation.GetTask => GetTask,
            A2AOperation.CancelTask => CancelTask,
            A2AOperation.SubscribeToTask => Subscribe,
            A2AOperation.CreateTaskPushNotificationConfig => CreatePushConfig,
            A2AOperation.ListTaskPushNotificationConfigs => ListPushConfigs,
            A2AOperation.GetTaskPushNotificationConfig => GetPushConfig,
            A2AOperation.DeleteTaskPushNotificationConfig => DeletePushConfig,
            A2AOperation.GetExtendedAgentCard => GetExtendedAgentCard,
            A2AOperation.Extension => ExtensionHandler(table.GetExtension(operation.Name)),
            _ => throw new InvalidOperationException($"No HTTP+JSON handler for {operation.Kind}.")
        };
    }

    private Task SendMessage(HttpContext http)
    {
        return Run(http, async (call, ct) =>
        {
            var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
            return await handler.SendMessageAsync(req, call, ct).ConfigureAwait(false);
        });
    }

    private Task SendStreamingMessage(HttpContext http)
    {
        return RunStream(http, async (call, caughtUp, ct) =>
        {
            var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
            return handler.SendStreamingMessageAsync(req, call, HttpPlumbing.LastEventId(http), caughtUp, ct);
        });
    }

    private Task GetTask(HttpContext http)
    {
        return Run(http, (call, ct) =>
            Box(handler.GetTaskAsync(new GetTaskRequest
            {
                Id = Route(http, "id"),
                HistoryLength = QueryInt(http, "historyLength"),
                TimelineLength = QueryInt(http, "timelineLength"),
                CurrentGeneration = QueryLong(http, "currentGeneration"),
                Tenant = call.Tenant
            }, call, ct)));
    }

    private Task ListTasks(HttpContext http)
    {
        return Run(http, (call, ct) =>
        {
            TaskState? status = null;
            if (!string.IsNullOrEmpty(Query(http, "status")))
            {
                if (!ProtoEnumConverter<TaskState>.TryParse(Query(http, "status"), out var s))
                    throw A2AException.InvalidParams("status is not a valid TaskState");
                status = s;
            }

            DateTimeOffset? after = null;
            if (!string.IsNullOrEmpty(Query(http, "statusTimestampAfter")))
            {
                if (!DateTimeOffset.TryParse(Query(http, "statusTimestampAfter"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var t))
                    throw A2AException.InvalidParams("statusTimestampAfter must be an ISO 8601 timestamp");
                after = t.ToUniversalTime();
            }

            bool? includeArtifacts = null;
            if (!string.IsNullOrEmpty(Query(http, "includeArtifacts")))
            {
                if (!bool.TryParse(Query(http, "includeArtifacts"), out var b))
                    throw A2AException.InvalidParams("includeArtifacts must be true or false");
                includeArtifacts = b;
            }

            var req = new ListTasksRequest
            {
                Tenant = call.Tenant,
                ContextId = Query(http, "contextId"),
                Status = status,
                PageSize = QueryInt(http, "pageSize"),
                PageToken = Query(http, "pageToken"),
                HistoryLength = QueryInt(http, "historyLength"),
                TimelineLength = QueryInt(http, "timelineLength"),
                StatusTimestampAfter = after,
                IncludeArtifacts = includeArtifacts
            };
            return Box(handler.ListTasksAsync(req, call, ct));
        });
    }

    private Task CancelTask(HttpContext http)
    {
        return Run(http, async (call, ct) =>
        {
            var req = await OptionalBody<CancelTaskRequest>(http).ConfigureAwait(false) ?? new CancelTaskRequest();
            req.Id = Route(http, "id");
            return await handler.CancelTaskAsync(req, call, ct).ConfigureAwait(false);
        });
    }

    private Task Subscribe(HttpContext http)
    {
        return RunStream(http, (call, caughtUp, ct) =>
        {
            var id = Route(http, "id");
            // A2A 1.1: GET has no body, so lastSeenGeneration comes as a query parameter (§11.5 query mapping).
            var request = new SubscribeToTaskRequest
                { Id = id, Tenant = call.Tenant, LastSeenGeneration = QueryLong(http, "lastSeenGeneration") };
            return Task.FromResult(handler.SubscribeToTaskAsync(request, call, HttpPlumbing.ResumeAfter(http, id),
                caughtUp, ct));
        });
    }

    private Task CreatePushConfig(HttpContext http)
    {
        return Run(http, async (call, ct) =>
        {
            var config = await Body<TaskPushNotificationConfig>(http).ConfigureAwait(false);
            config.TaskId = Route(http, "id");
            return await handler.CreateTaskPushNotificationConfigAsync(config, call, ct).ConfigureAwait(false);
        });
    }

    private Task GetPushConfig(HttpContext http)
    {
        return Run(http, (call, ct) =>
            Box(handler.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
            {
                TaskId = Route(http, "id"), Id = Route(http, "configId"), Tenant = call.Tenant
            }, call, ct)));
    }

    private Task ListPushConfigs(HttpContext http)
    {
        return Run(http, (call, ct) =>
            Box(handler.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest
            {
                TaskId = Route(http, "id"), PageSize = QueryInt(http, "pageSize"), PageToken = Query(http, "pageToken"),
                Tenant = call.Tenant
            }, call, ct)));
    }

    private Task DeletePushConfig(HttpContext http)
    {
        return Run(http, async (call, ct) =>
        {
            await handler.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
            {
                TaskId = Route(http, "id"), Id = Route(http, "configId"), Tenant = call.Tenant
            }, call, ct).ConfigureAwait(false);
            return A2AJson.EmptyObject;
        });
    }

    /// <summary>
    ///     GET /extendedAgentCard (spec §13.3). The card depends on the caller, so it is <c>private</c> to the caller's
    ///     cache and <c>no-cache</c>: a client may keep it, but asks again before each use. The entity tag is a hash of
    ///     the served bytes, so an <c>If-None-Match</c> that still matches gets 304 and a changed card gets the new bytes.
    /// </summary>
    private Task GetExtendedAgentCard(HttpContext http)
    {
        return RunWrite(http, async (call, ct) =>
        {
            var card = await handler
                .GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest { Tenant = call.Tenant }, call, ct)
                .ConfigureAwait(false);
            var body = JsonSerializer.SerializeToUtf8Bytes(card, A2AJson.GetTypeInfo<AgentCard>(call.ProtocolVersion));
            var tag = "\"" + Base64Url.Encode(SHA256.HashData(body))[..22] + "\"";
            HttpPlumbing.WriteExtensionsHeader(http, call);
            http.Response.Headers.CacheControl = "private, no-cache";
            http.Response.Headers.Vary = "Authorization, Cookie, A2A-Version";
            http.Response.Headers.ETag = tag;
            if (AgentCardEndpoint.Matches(http.Request.Headers.IfNoneMatch, tag))
            {
                http.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            http.Response.ContentType = A2AProtocol.MediaType;
            http.Response.ContentLength = body.Length;
            await http.Response.Body.WriteAsync(body, ct).ConfigureAwait(false);
        });
    }

    private RequestDelegate ExtensionHandler(A2AExtensionOperation extension)
    {
        return http => Run(http, async (call, ct) =>
        {
            await ExtensionPolicy.RequireAsync(http, extension, call, _allowAnonymous).ConfigureAwait(false);
            var parameters = await JsonBody(http).ConfigureAwait(false);
            return await handler.InvokeExtensionAsync(extension.Method, parameters, call, ct).ConfigureAwait(false);
        });
    }

    // ---- plumbing ----

    private static async Task<object?> Box<T>(Task<T> t)
    {
        return await t.ConfigureAwait(false);
    }

    private Task Run(HttpContext http, Func<ServerCallContext, CancellationToken, Task<object?>> action)
    {
        return RunWrite(http, async (call, ct) =>
        {
            var result = await action(call, ct).ConfigureAwait(false);
            HttpPlumbing.WriteExtensionsHeader(http, call);
            await HttpPlumbing.WriteJsonAsync(http, result, A2AProtocol.MediaType, call.ProtocolVersion)
                .ConfigureAwait(false);
        });
    }

    /// <summary>
    ///     Runs a unary route whose action writes the response itself. Errors before the response starts are written as
    ///     <c>google.rpc.Status</c>.
    /// </summary>
    private async Task RunWrite(HttpContext http, Func<ServerCallContext, CancellationToken, Task> write)
    {
        ServerCallContext call;
        try
        {
            call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson);
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(),
                HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }

        try
        {
            handler.ValidateCall(call);
            await write(call, http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Runs a streaming route. The action gets the caught-up signal to pass to the handler (see
    ///     <see cref="HttpPlumbing.PrimeAsync" />).
    /// </summary>
    private async Task RunStream(HttpContext http,
        Func<ServerCallContext, Action, CancellationToken, Task<IAsyncEnumerable<SequencedEvent>>> action)
    {
        ServerCallContext call;
        try
        {
            call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson);
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(),
                HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }

        try
        {
            handler.ValidateCall(call);
            var caughtUp = HttpPlumbing.CaughtUpSignal();
            var events = await action(call, caughtUp.Signal, http.RequestAborted).ConfigureAwait(false);
            var e = await HttpPlumbing.PrimeAsync(events, _keepAlive, caughtUp.Fired, http.RequestAborted)
                        .ConfigureAwait(false)
                    ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
            HttpPlumbing.WriteExtensionsHeader(http, call);
            HttpPlumbing.StartEventStream(http);
            // The event's stored bytes, as they are (encoded here only for snapshots and other events made without them); a 1.1
            // caller gets the 1.1 encoding.
            await HttpPlumbing.PumpAsync(http, e, evt => EventEncoder.JsonFor(evt, call.ProtocolVersion),
                ex => A2AJson.SerializeToUtf8Bytes(StatusBody(HttpPlumbing.Normalize(ex, logger, _includeDetails))),
                // A2A #1262: a2a-python reads REST stream errors from frames named "error".
                _keepAlive, logger, "error").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails))
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, ex, call).ConfigureAwait(false);
        HttpPlumbing.SetRetryAfter(http, ex);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        await HttpPlumbing.WriteJsonAsync(http, StatusBody(ex), A2AProtocol.MediaType, HttpPlumbing.StatusFor(ex))
            .ConfigureAwait(false);
    }

    internal static RestErrorBody StatusBody(A2AException ex)
    {
        return new RestErrorBody
        {
            Error = new RestError
            {
                Code = HttpPlumbing.StatusFor(ex),
                Status = A2AErrors.GrpcStatus(ex.Code),
                Message = ex.Message,
                Details = A2AErrors.BuildDetails(ex)
            }
        };
    }

    private async Task<T> Body<T>(HttpContext http)
    {
        return await OptionalBody<T>(http).ConfigureAwait(false) ??
               throw A2AException.InvalidParams("A request body is required");
    }

    // The body is read into a pooled buffer, checked in one pass, and deserialized from the same bytes; the buffer is
    // returned before the caller runs. The result never points into it (the serializer copies any JsonElement it builds).
    private async Task<T?> OptionalBody<T>(HttpContext http)
    {
        using var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes)
            .ConfigureAwait(false);
        if (body.Length == 0) return default;
        try
        {
            var root = HttpPlumbing.CheckDepthAndDuplicates(body.Memory, _limits.MaxJsonDepth);
            return ProtoJsonFieldNames.Deserialize(body.Memory.Span[root], A2AJson.GetTypeInfo<T>());
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Invalid request body: {ex.Message}");
        }
    }

    /// <summary>The body as JSON (any value); an empty body is an empty object.</summary>
    private async Task<JsonElement> JsonBody(HttpContext http)
    {
        using var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes)
            .ConfigureAwait(false);
        if (body.Length == 0) return A2AJson.EmptyObject;
        try
        {
            var root = HttpPlumbing.CheckDepthAndDuplicates(body.Memory, _limits.MaxJsonDepth);
            return JsonSerializer.Deserialize(body.Memory.Span[root], A2AJson.GetTypeInfo<JsonElement>());
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Invalid request body: {ex.Message}");
        }
    }

    private static string Route(HttpContext http, string name)
    {
        return http.GetRouteValue(name) as string ?? throw A2AException.InvalidParams($"{name} is required");
    }

    /// <summary>
    ///     A query parameter by its JSON name (<c>historyLength</c>) or its proto name (<c>history_length</c>), as HTTP
    ///     transcoding accepts both. Both present is InvalidParams.
    /// </summary>
    private static string? Query(HttpContext http, string jsonName)
    {
        string? value = http.Request.Query[jsonName];
        var protoName = ProtoName(jsonName);
        if (protoName == jsonName) return value;
        string? alt = http.Request.Query[protoName];
        if (string.IsNullOrEmpty(alt)) return value;
        if (!string.IsNullOrEmpty(value))
            throw A2AException.InvalidParams($"Both '{jsonName}' and '{protoName}' are present; send only one.");
        return alt;
    }

    private static string ProtoName(string jsonName)
    {
        var sb = new StringBuilder(jsonName.Length + 4);
        foreach (var c in jsonName)
            if (char.IsUpper(c)) sb.Append('_').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        return sb.ToString();
    }

    private static long? QueryLong(HttpContext http, string name)
    {
        var v = Query(http, name);
        if (string.IsNullOrEmpty(v)) return null;
        return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? l
            : throw A2AException.InvalidParams($"{name} must be an integer");
    }

    private static int? QueryInt(HttpContext http, string name)
    {
        var v = Query(http, name);
        if (string.IsNullOrEmpty(v)) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw A2AException.InvalidParams($"{name} must be an integer");
    }
}