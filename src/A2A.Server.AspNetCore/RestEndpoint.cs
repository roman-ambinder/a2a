using System.Globalization;
using System.Text.Json;
using A2A.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP+JSON/REST binding (spec §11). Errors use the google.rpc.Status shape (§11.6).</summary>
internal sealed class RestEndpoint(A2ARequestHandler handler, IOptions<A2AHttpOptions> httpOptions, ILogger<RestEndpoint> logger)
{
    private readonly A2AHttpOptions _http = httpOptions.Value;

    public void Map(IEndpointRouteBuilder group)
    {
        foreach (var prefix in new[] { "", "/{tenant}" })
        {
            group.MapPost(prefix + "/message:send", SendMessage);
            group.MapPost(prefix + "/message:stream", SendStreamingMessage);
            group.MapGet(prefix + "/tasks", ListTasks);
            group.MapGet(prefix + "/tasks/{id}", GetTask);
            group.MapPost(prefix + "/tasks/{id}:cancel", CancelTask);
            // The spec text says POST, the normative proto says GET: accept both.
            group.MapMethods(prefix + "/tasks/{id}:subscribe", ["GET", "POST"], Subscribe);
            group.MapPost(prefix + "/tasks/{id}/pushNotificationConfigs", CreatePushConfig);
            group.MapGet(prefix + "/tasks/{id}/pushNotificationConfigs", ListPushConfigs);
            group.MapGet(prefix + "/tasks/{id}/pushNotificationConfigs/{configId}", GetPushConfig);
            group.MapDelete(prefix + "/tasks/{id}/pushNotificationConfigs/{configId}", DeletePushConfig);
            group.MapGet(prefix + "/extendedAgentCard", GetExtendedAgentCard);
        }
    }

    private Task SendMessage(HttpContext http) => Run(http, async (call, ct) =>
    {
        var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
        return await handler.SendMessageAsync(req, call, ct).ConfigureAwait(false);
    });

    private Task SendStreamingMessage(HttpContext http) => RunStream(http, async (call, ct) =>
    {
        var req = await Body<SendMessageRequest>(http).ConfigureAwait(false);
        return handler.SendStreamingMessageAsync(req, call, ct);
    });

    private Task GetTask(HttpContext http) => Run(http, (call, ct) =>
        Box(handler.GetTaskAsync(new GetTaskRequest { Id = Route(http, "id"), HistoryLength = QueryInt(http, "historyLength"), Tenant = call.Tenant }, call, ct)));

    private Task ListTasks(HttpContext http) => Run(http, (call, ct) =>
    {
        var q = http.Request.Query;
        TaskState? status = null;
        if (!string.IsNullOrEmpty(q["status"]))
        {
            if (!ProtoEnumConverter<TaskState>.TryParse(q["status"], out var s)) throw A2AException.InvalidParams("status is not a valid TaskState");
            status = s;
        }
        DateTimeOffset? after = null;
        if (!string.IsNullOrEmpty(q["statusTimestampAfter"]))
        {
            if (!DateTimeOffset.TryParse(q["statusTimestampAfter"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                throw A2AException.InvalidParams("statusTimestampAfter must be an ISO 8601 timestamp");
            after = t.ToUniversalTime();
        }
        bool? includeArtifacts = null;
        if (!string.IsNullOrEmpty(q["includeArtifacts"]))
        {
            if (!bool.TryParse(q["includeArtifacts"], out var b)) throw A2AException.InvalidParams("includeArtifacts must be true or false");
            includeArtifacts = b;
        }
        var req = new ListTasksRequest
        {
            Tenant = call.Tenant,
            ContextId = q["contextId"],
            Status = status,
            PageSize = QueryInt(http, "pageSize"),
            PageToken = q["pageToken"],
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

    private Task Subscribe(HttpContext http) => RunStream(http, (call, ct) =>
        Task.FromResult(handler.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = Route(http, "id"), Tenant = call.Tenant }, call, ct)));

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
            TaskId = Route(http, "id"), PageSize = QueryInt(http, "pageSize"), PageToken = http.Request.Query["pageToken"], Tenant = call.Tenant,
        }, call, ct)));

    private Task DeletePushConfig(HttpContext http) => Run(http, async (call, ct) =>
    {
        await handler.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest
        {
            TaskId = Route(http, "id"), Id = Route(http, "configId"), Tenant = call.Tenant,
        }, call, ct).ConfigureAwait(false);
        return new { };
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
            await WriteErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, logger)).ConfigureAwait(false);
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
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger)).ConfigureAwait(false);
        }
    }

    private async Task RunStream(HttpContext http, Func<ServerCallContext, CancellationToken, Task<IAsyncEnumerable<StreamResponse>>> action)
    {
        ServerCallContext call;
        try { call = HttpPlumbing.CreateCallContext(http); }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, ServerCallContext.Anonymous(), HttpPlumbing.Normalize(ex, logger)).ConfigureAwait(false);
            return;
        }
        try
        {
            handler.ValidateCall(call);
            var events = await action(call, http.RequestAborted).ConfigureAwait(false);
            var e = await HttpPlumbing.PrimeAsync(events, http.RequestAborted).ConfigureAwait(false)
                    ?? throw new A2AException(A2AErrorCode.InvalidAgentResponse, "Empty stream");
            HttpPlumbing.WriteExtensionsHeader(http, call);
            HttpPlumbing.StartEventStream(http);
            await HttpPlumbing.PumpAsync(http, e, A2AJson.SerializeToUtf8Bytes,
                ex => A2AJson.SerializeToUtf8Bytes(StatusBody(HttpPlumbing.Normalize(ex, logger))),
                _http.StreamKeepAliveInterval, logger).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            await WriteErrorAsync(http, call, HttpPlumbing.Normalize(ex, logger)).ConfigureAwait(false);
        }
    }

    private Task WriteErrorAsync(HttpContext http, ServerCallContext call, A2AException ex)
    {
        HttpPlumbing.SetAuthChallenge(http, ex, handler.AgentCard);
        HttpPlumbing.WriteExtensionsHeader(http, call);
        return HttpPlumbing.WriteJsonAsync(http, StatusBody(ex), A2AProtocol.MediaType, A2AErrors.HttpStatus(ex.Code));
    }

    internal static object StatusBody(A2AException ex) => new
    {
        error = new
        {
            code = A2AErrors.HttpStatus(ex.Code),
            status = A2AErrors.GrpcStatus(ex.Code),
            message = ex.Message,
            details = A2AErrors.BuildDetails(ex),
        },
    };

    private async Task<T> Body<T>(HttpContext http) =>
        await OptionalBody<T>(http).ConfigureAwait(false) ?? throw A2AException.InvalidParams("A request body is required");

    private async Task<T?> OptionalBody<T>(HttpContext http)
    {
        var bytes = await HttpPlumbing.ReadBodyAsync(http.Request, _http.MaxRequestBodySize).ConfigureAwait(false);
        if (bytes.Length == 0) return default;
        try { return JsonSerializer.Deserialize<T>(bytes, A2AJson.Options); }
        catch (JsonException ex) { throw A2AException.InvalidParams($"Invalid request body: {ex.Message}"); }
    }

    private static string Route(HttpContext http, string name) =>
        http.GetRouteValue(name) as string ?? throw A2AException.InvalidParams($"{name} is required");

    private static int? QueryInt(HttpContext http, string name)
    {
        string? v = http.Request.Query[name];
        if (string.IsNullOrEmpty(v)) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw A2AException.InvalidParams($"{name} must be an integer");
    }
}
