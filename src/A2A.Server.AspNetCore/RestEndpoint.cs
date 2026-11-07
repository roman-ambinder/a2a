using System.Globalization;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP+JSON/REST binding (spec §11). Errors use the google.rpc.Status shape (§11.6).</summary>
internal sealed class RestEndpoint(A2ARequestHandler handler,
    IOptions<A2AServerOptions> serverOptions, IHostEnvironment environment, ILogger<RestEndpoint> logger)
{
    private readonly A2ALimits _limits = serverOptions.Value.Limits;
    private readonly TimeSpan _keepAlive = serverOptions.Value.Streaming.KeepAliveInterval;
    private readonly bool _includeDetails = HttpPlumbing.ShouldIncludeDetails(serverOptions.Value, environment);

    /// <summary>
    /// Maps the routes under <paramref name="group"/>. Unless the group's own pattern already has a <c>{tenant}</c> segment
    /// (<paramref name="tenantInPattern"/>), every route also gets a <c>/{tenant}</c> variant.
    /// </summary>
    public void Map(IEndpointRouteBuilder group, bool tenantInPattern = false)
    {
        foreach (var prefix in tenantInPattern ? new[] { "" } : new[] { "", "/{tenant}" })
        {
            Add(group.MapPost(prefix + "/message:send", SendMessage), "POST", prefix + "/message:send");
            Add(group.MapPost(prefix + "/message:stream", SendStreamingMessage), "POST", prefix + "/message:stream");
            Add(group.MapGet(prefix + "/tasks", ListTasks), "GET", prefix + "/tasks");
            Add(group.MapGet(prefix + "/tasks/{id}", GetTask), "GET", prefix + "/tasks/{id}");
            Add(group.MapPost(prefix + "/tasks/{id}:cancel", CancelTask), "POST", prefix + "/tasks/{id}:cancel");
            // The spec text says POST, the normative proto says GET: accept both.
            Add(group.MapMethods(prefix + "/tasks/{id}:subscribe", ["GET", "POST"], Subscribe), "GET|POST", prefix + "/tasks/{id}:subscribe");
            Add(group.MapPost(prefix + "/tasks/{id}/pushNotificationConfigs", CreatePushConfig), "POST", prefix + "/tasks/{id}/pushNotificationConfigs");
            Add(group.MapGet(prefix + "/tasks/{id}/pushNotificationConfigs", ListPushConfigs), "GET", prefix + "/tasks/{id}/pushNotificationConfigs");
            Add(group.MapGet(prefix + "/tasks/{id}/pushNotificationConfigs/{configId}", GetPushConfig), "GET", prefix + "/tasks/{id}/pushNotificationConfigs/{configId}");
            Add(group.MapDelete(prefix + "/tasks/{id}/pushNotificationConfigs/{configId}", DeletePushConfig), "DELETE", prefix + "/tasks/{id}/pushNotificationConfigs/{configId}");
            Add(group.MapGet(prefix + "/extendedAgentCard", GetExtendedAgentCard), "GET", prefix + "/extendedAgentCard");
        }

        // The display name starts with "A2A " so the startup check can find every A2A endpoint.
        static void Add(IEndpointConventionBuilder builder, string method, string route) => builder.WithDisplayName($"A2A HTTP+JSON {method} {route}");
    }

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

    // ---- plumbing ----

    private static async Task<object?> Box<T>(Task<T> t) => await t.ConfigureAwait(false);

    private async Task Run(HttpContext http, Func<ServerCallContext, CancellationToken, Task<object?>> action)
    {
        ServerCallContext call;
        try { call = HttpPlumbing.CreateCallContext(http); }
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
        try { call = HttpPlumbing.CreateCallContext(http); }
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
            await HttpPlumbing.PumpAsync(http, e, A2AJson.SerializeToUtf8Bytes,
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

    private Task WriteErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        HttpPlumbing.SetAuthChallenge(http, ex, handler.AgentCard);
        HttpPlumbing.SetRetryAfter(http, ex);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        return HttpPlumbing.WriteJsonAsync(http, StatusBody(ex), A2AProtocol.MediaType, HttpPlumbing.StatusFor(ex));
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

    private async Task<T?> OptionalBody<T>(HttpContext http)
    {
        var bytes = await HttpPlumbing.ReadBodyAsync(http.Request, _limits.MaxRequestBodyBytes).ConfigureAwait(false);
        if (bytes.Length == 0) return default;
        try
        {
            using var doc = HttpPlumbing.ParseBody(bytes, _limits.MaxJsonDepth);
            return ProtoJsonFieldNames.Normalize(doc.RootElement).Deserialize(A2AJson.GetTypeInfo<T>());
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
