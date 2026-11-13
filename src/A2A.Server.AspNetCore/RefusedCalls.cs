using A2A.Diagnostics;
using A2A.Server.AspNetCore.Operations;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Endpoint metadata of an A2A endpoint: its binding and, when the route alone names it, its operation. The refused-call
///     telemetry uses it for a call refused before the endpoint runs.
/// </summary>
internal sealed class A2AOperationMetadata(string binding, string? operation)
{
    /// <summary>The <see cref="ProtocolBindings" /> value.</summary>
    public string Binding { get; } = binding;

    /// <summary>The operation name, or null when the route serves several (JSON-RPC names the method in the body).</summary>
    public string? Operation { get; } = operation;
}

/// <summary>
///     Records the calls an A2A endpoint, or the authorization middleware in front of it, refuses before the operation
///     pipeline runs (<see cref="IA2ARequestHandler.RecordRefusedCall" />), so they get a span, a count and, for an
///     authentication or authorization refusal, a security event.
/// </summary>
internal static class RefusedCalls
{
    /// <summary>
    ///     Records <paramref name="error" /> for a call: <paramref name="call" />, or when the binding could not build one, a
    ///     context with the binding, the caller's trace context, its address and its protocol version.
    /// </summary>
    public static void Record(IA2ARequestHandler handler, HttpContext http, ServerCallContext? call, string binding,
        string? operation, Exception error)
    {
        handler.RecordRefusedCall(call ?? Minimal(http, binding), operation, error);
    }

    /// <summary>The call context of a request refused before its own could be built.</summary>
    public static ServerCallContext Minimal(HttpContext http, string? binding)
    {
        string? version = http.Request.Headers[A2AProtocol.Headers.Version];
        if (string.IsNullOrWhiteSpace(version)) version = http.Request.Query[A2AProtocol.Headers.Version];
        return new ServerCallContext
        {
            Binding = binding,
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RemoteAddress = http.Connection.RemoteIpAddress?.ToString(),
            TraceParent = A2ATelemetry.TryExtractTraceContext(
                http.Request.Headers[A2ATelemetry.TraceParentHeader],
                http.Request.Headers[A2ATelemetry.TraceStateHeader], out var parent)
                ? parent
                : default
        };
    }

    /// <summary>The operation of the endpoint's route, or null when its route does not name one.</summary>
    public static string? OperationOf(HttpContext http)
    {
        return http.GetEndpoint()?.Metadata.GetMetadata<A2AOperationMetadata>()?.Operation;
    }

    /// <summary>
    ///     Records the refusal of a request to an A2A endpoint by the authorization middleware (a challenge is
    ///     Unauthenticated, a forbid PermissionDenied) or by the rate limit (<paramref name="error" />). Requests to other
    ///     endpoints are not recorded.
    /// </summary>
    public static void RecordBeforeEndpoint(HttpContext http, Exception error)
    {
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<A2AStartupCheckMetadata>() is null) return;
        if (http.RequestServices.GetService<IA2ARequestHandler>() is not { } handler) return;
        var (binding, operation) = Describe(http, endpoint);
        // A forbidden caller is authenticated: its principal goes into the security event's hash. Any other refusal (a
        // challenge, the rate limit) gets the cheap context, so a flood of refused requests costs no resolver call.
        var call = error is A2AException { Code: A2AErrorCode.PermissionDenied } && http.User.Identity?.IsAuthenticated == true
            ? CallOrMinimal(http, binding)
            : Minimal(http, binding);
        handler.RecordRefusedCall(call, operation, error);
    }

    /// <summary>The error the caller gets for an authorization result that is not a success, or null for a success.</summary>
    public static A2AException? ErrorOf(PolicyAuthorizationResult result)
    {
        if (result.Challenged) return new A2AException(A2AErrorCode.Unauthenticated);
        if (result.Forbidden) return new A2AException(A2AErrorCode.PermissionDenied);
        return null;
    }

    /// <summary>
    ///     The call context of an authenticated caller (with its principal, so the security event's principal hash tells
    ///     callers apart), or the minimal one when it cannot be built.
    /// </summary>
    private static ServerCallContext CallOrMinimal(HttpContext http, string? binding)
    {
        try
        {
            return HttpPlumbing.CreateCallContext(http, binding);
        }
        catch (Exception)
        {
            return Minimal(http, binding);
        }
    }

    /// <summary>
    ///     The binding and operation of an A2A endpoint: from <see cref="A2AOperationMetadata" />, else from a gRPC
    ///     endpoint's method (the path's last segment, kept only when it is an operation the server serves), else none.
    /// </summary>
    private static (string? Binding, string? Operation) Describe(HttpContext http, Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<A2AOperationMetadata>() is { } known) return (known.Binding, known.Operation);
        if (endpoint.DisplayName?.StartsWith("A2A gRPC ", StringComparison.Ordinal) != true) return (null, null);
        var path = http.Request.Path.Value ?? "";
        var method = path[(path.LastIndexOf('/') + 1)..];
        var table = http.RequestServices.GetService<A2AOperationTable>();
        if (method == A2AProtocol.Methods.SendLiveMessage) return (ProtocolBindings.Grpc, method);
        return (ProtocolBindings.Grpc,
            table is not null && table.JsonRpcMethods.TryGetValue(method, out var operation) ? operation.Name : null);
    }
}
