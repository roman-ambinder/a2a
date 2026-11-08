using System.Globalization;
using System.Text.Json;
using A2A.Json;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// HTTP+JSON/REST binding (spec §11). Errors use the google.rpc.Status shape (§11.6). The routes come from the
/// <see cref="A2AOperationTable"/>: the 11 built-in routes and <c>POST /ext/{method}</c> per extension operation.
/// </summary>
/// <remarks>
/// <c>MapA2A</c> and <c>MapA2AHttpJson</c> create and map it; a host that maps its own route groups can create one with
/// <c>ActivatorUtilities.CreateInstance</c> and call <see cref="Map"/>.
/// </remarks>
/// <param name="handler">The server core.</param>
/// <param name="table">The operations to serve.</param>
/// <param name="serverOptions">The server options (limits, streaming, error details).</param>
/// <param name="environment">The host environment (error details in Development).</param>
/// <param name="logger">The logger.</param>
public sealed class RestEndpoint(IA2ARequestHandler handler, A2AOperationTable table,
    IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<RestEndpoint> logger)
{
    private readonly A2ALimits _limits = serverOptions.Value.Limits;
    private readonly TimeSpan _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);
    private readonly bool _allowAnonymous = serverOptions.Value.AllowAnonymous;

    /// <summary>
    /// Maps the routes of the <see cref="A2AOperationTable"/> under <paramref name="group"/>. Unless the group's own pattern
    /// already has a <c>{tenant}</c> segment (<paramref name="tenantInPattern"/>), every route also gets a <c>/{tenant}</c> variant.
    /// </summary>
    /// <param name="group">The route group (or app) to map into.</param>
    /// <param name="tenantInPattern">True when the group's pattern has a <c>{tenant}</c> segment.</param>
    public void Map(IEndpointRouteBuilder group, bool tenantInPattern = false)
    {
        ArgumentNullException.ThrowIfNull(group);
        foreach (var prefix in tenantInPattern ? new[] { "" } : new[] { "", "/{tenant}" })
        {
            foreach (var (method, route, operation) in table.RestRoutes)
            {
                // The display name starts with "A2A " so the startup check can find every A2A endpoint.
                group.MapMethods(prefix + route, method.Split('|'), HandlerFor(operation))
                    .WithDisplayName($"A2A HTTP+JSON {method} {prefix + route}");
            }
        }
    }

    private RequestDelegate HandlerFor(A2AOperationDescriptor operation) => operation.Kind switch
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
        _ => throw new InvalidOperationException($"No HTTP+JSON handler for {operation.Kind}."),
    };

    private Task SendMessage(HttpContext http) => Run(http, async (call, ct) =>
    {
        var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
        return await handler.SendMessageAsync(req, call, ct).ConfigureAwait(false);
    });

    private Task SendStreamingMessage(HttpContext http) => RunStream(http, async (call, caughtUp, ct) =>
    {
        var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
        return handler.SendStreamingMessageAsync(req, call, HttpPlumbing.LastEventId(http), caughtUp, ct);
    });

    private Task GetTask(HttpContext http) => Run(http, (call, ct) =>
        Box(handler.GetTaskAsync(new GetTaskRequest { Id = Route(http, "id"), HistoryLength = QueryInt(http, "historyLength"), Tenant = call.Tenant }, call, ct)));

    private Task ListTasks(HttpContext http) => Run(http, (call, ct) =>
    {
        TaskState? status = null;
        if (!string.IsNullOrEmpty(Query(http, "status")))
        {
            if (!ProtoEnumConverter<TaskState>.TryParse(Query(http, "status"), out var s)) throw A2AException.InvalidParams("status is not a valid TaskState");
            status = s;
        }
        DateTimeOffset? after = null;
        if (!string.IsNullOrEmpty(Query(http, "statusTimestampAfter")))
        {
            if (!DateTimeOffset.TryParse(Query(http, "statusTimestampAfter"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                throw A2AException.InvalidParams("statusTimestampAfter must be an ISO 8601 timestamp");
            after = t.ToUniversalTime();
        }
        bool? includeArtifacts = null;
        if (!string.IsNullOrEmpty(Query(http, "includeArtifacts")))
        {
            if (!bool.TryParse(Query(http, "includeArtifacts"), out var b)) throw A2AException.InvalidParams("includeArtifacts must be true or false");
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
            StatusTimestampAfter = after,
            IncludeArtifacts = includeArtifacts,
        };
        return Box(handler.ListTasksAsync(req, call, ct));
    });

    private Task CancelTask(HttpContext http) => Run(http, async (call, ct) =>
    {
        var req = await OptionalBody<CancelTaskRequest>(http).ConfigureAwait(false) ?? new CancelTaskRequest();
        req.Id = Route(http, "id");
        return await handler.CancelTaskAsync(req, call, ct).ConfigureAwait(false);
    });

    private Task Subscribe(HttpContext http) => RunStream(http, (call, caughtUp, ct) =>
    {
        var id = Route(http, "id");
        return Task.FromResult(handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = id, Tenant = call.Tenant }, call, HttpPlumbing.ResumeAfter(http, id), caughtUp, ct));
    });

    private Task CreatePushConfig(HttpContext http) => Run(http, async (call, ct) =>
    {
        var config = await Body<TaskPushNotificationConfig>(http).ConfigureAwait(false);
        config.TaskId = Route(http, "id");
        return await handler.CreateTaskPushNotificationConfigAsync(config, call, ct).ConfigureAwait(false);
    });

    private Task GetPushConfig(HttpContext http) => Run(http, (call, ct) =>
        Box(handler.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest
        {
            TaskId = Route(http, "id"), Id = Route(http, "configId"), Tenant = call.Tenant,
        }, call, ct)));

    private Task ListPushConfigs(HttpContext http) => Run(http, (call, ct) =>
        Box(handler.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest
        {
            TaskId = Route(http, "id"), PageSize = QueryInt(http, "pageSize"), PageToken = Query(http, "pageToken"), Tenant = call.Tenant,
        }, call, ct)));

    private Task DeletePushConfig(HttpContext http) => Run(http, async (call, ct) =>
    {
        await handler.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
        {
            TaskId = Route(http, "id"), Id = Route(http, "configId"), Tenant = call.Tenant,
        }, call, ct).ConfigureAwait(false);
        return A2AJson.EmptyObject;
    });

    private Task GetExtendedAgentCard(HttpContext http) => Run(http, (call, ct) =>
        Box(handler.GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest { Tenant = call.Tenant }, call, ct)));

    private RequestDelegate ExtensionHandler(A2AExtensionOperation extension) => http => Run(http, async (call, ct) =>
    {
        await ExtensionPolicy.RequireAsync(http, extension, call, _allowAnonymous).ConfigureAwait(false);
        var parameters = await JsonBody(http).ConfigureAwait(false);
        return await handler.InvokeExtensionAsync(extension.Method, parameters, call, ct).ConfigureAwait(false);
    });

    // ---- plumbing ----

    private static async Task<object?> Box<T>(Task<T> t) => await t.ConfigureAwait(false);

    private async Task Run(HttpContext http, Func<ServerCallContext, CancellationToken, Task<object?>> action)
    {
        ServerCallContext call;
        try { call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson); }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            handler.ValidateCall(call);
            var result = await action(call, http.RequestAborted).ConfigureAwait(false);
            HttpPlumbing.WriteExtensionsHeader(http, call);
            await HttpPlumbing.WriteJsonAsync(http, result, A2AProtocol.MediaType).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a streaming route. The action gets the caught-up signal to pass to the handler (see <see cref="HttpPlumbing.PrimeAsync"/>).</summary>
    private async Task RunStream(HttpContext http, Func<ServerCallContext, Action, CancellationToken, Task<IAsyncEnumerable<SequencedEvent>>> action)
    {
        ServerCallContext call;
        try { call = HttpPlumbing.CreateCallContext(http, ProtocolBindings.HttpJson); }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
            return;
        }
        try
        {
            handler.ValidateCall(call);
            var caughtUp = HttpPlumbing.CaughtUpSignal();
            var events = await action(call, caughtUp.Signal, http.RequestAborted).ConfigureAwait(false);
            var e = await HttpPlumbing.PrimeAsync(events, _keepAlive, caughtUp.Fired, http.RequestAborted).ConfigureAwait(false)
                    ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
            HttpPlumbing.WriteExtensionsHeader(http, call);
            HttpPlumbing.StartEventStream(http);
            // The event's stored bytes, as they are (encoded here only for snapshots and other events made without them).
            await HttpPlumbing.PumpAsync(http, e, evt => EventEncoder.EnsureEncoded(evt).Json,
                ex => A2AJson.SerializeToUtf8Bytes(StatusBody(HttpPlumbing.Normalize(ex, logger, _includeDetails))),
                // A2A #1262: a2a-python reads REST stream errors from frames named "error".
                _keepAlive, logger, errorEventType: "error").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger, _includeDetails)).ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        await HttpPlumbing.SetAuthChallengeAsync(http, ex, call).ConfigureAwait(false);
        HttpPlumbing.SetRetryAfter(http, ex);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        await HttpPlumbing.WriteJsonAsync(http, StatusBody(ex), A2AProtocol.MediaType, HttpPlumbing.StatusFor(ex)).ConfigureAwait(false);
    }

    internal static RestErrorBody StatusBody(A2AException ex) => new()
    {
        Error = new RestError
        {
            Code = HttpPlumbing.StatusFor(ex),
            Status = A2AErrors.GrpcStatus(ex.Code),
            Message = ex.Message,
            Details = A2AErrors.BuildDetails(ex),
        },
    };

    private async Task<T> Body<T>(HttpContext http) =>
        await OptionalBody<T>(http).ConfigureAwait(false) ?? throw A2AException.InvalidParams("A request body is required");

    // The body is read into a pooled buffer, checked in one pass, and deserialized from the same bytes; the buffer is
    // returned before the caller runs. The result never points into it (the serializer copies any JsonElement it builds).
    private async Task<T?> OptionalBody<T>(HttpContext http)
    {
        using var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false);
        if (body.Length == 0) return default;
        try
        {
            var root = HttpPlumbing.CheckDepthAndDuplicates(body.Memory, _limits.MaxJsonDepth);
            return ProtoJsonFieldNames.Deserialize(body.Memory.Span[root], A2AJson.GetTypeInfo<T>());
        }
        catch (JsonException ex) { throw A2AException.InvalidParams($"Invalid request body: {ex.Message}"); }
    }

    /// <summary>The body as JSON (any value); an empty body is an empty object.</summary>
    private async Task<JsonElement> JsonBody(HttpContext http)
    {
        using var body = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false);
        if (body.Length == 0) return A2AJson.EmptyObject;
        try
        {
            var root = HttpPlumbing.CheckDepthAndDuplicates(body.Memory, _limits.MaxJsonDepth);
            return JsonSerializer.Deserialize(body.Memory.Span[root], A2AJson.GetTypeInfo<JsonElement>());
        }
        catch (JsonException ex) { throw A2AException.InvalidParams($"Invalid request body: {ex.Message}"); }
    }

    private static string Route(HttpContext http, string name) =>
        http.GetRouteValue(name) as string ?? throw A2AException.InvalidParams($"{name} is required");

    /// <summary>
    /// A query parameter by its JSON name (<c>historyLength</c>) or its proto name (<c>history_length</c>), as HTTP
    /// transcoding accepts both. Both present is InvalidParams.
    /// </summary>
    private static string? Query(HttpContext http, string jsonName)
    {
        string? value = http.Request.Query[jsonName];
        var protoName = ProtoName(jsonName);
        if (protoName == jsonName) return value;
        string? alt = http.Request.Query[protoName];
        if (string.IsNullOrEmpty(alt)) return value;
        if (!string.IsNullOrEmpty(value)) throw A2AException.InvalidParams($"Both '{jsonName}' and '{protoName}' are present; send only one.");
        return alt;
    }

    private static string ProtoName(string jsonName)
    {
        var sb = new System.Text.StringBuilder(jsonName.Length + 4);
        foreach (var c in jsonName)
        {
            if (char.IsUpper(c)) sb.Append('_').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static int? QueryInt(HttpContext http, string name)
    {
        var v = Query(http, name);
        if (string.IsNullOrEmpty(v)) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw A2AException.InvalidParams($"{name} must be an integer");
    }
}
