using System.Text.Json;
using A2A.Grpc;
using A2A.Server.AspNetCore;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
///     The error translation and the extensions echo shared by the A2A gRPC services, so every service answers a failure
///     the same way.
/// </summary>
internal sealed class GrpcCallSupport(ILogger logger, bool includeDetails, IA2ARequestHandler handler)
{
    /// <summary>As the HTTP bindings decide it: the option, else true only in Development.</summary>
    public static bool ShouldIncludeDetails(IOptions<A2AServerOptions> options, IHostEnvironment environment)
    {
        return options.Value.IncludeExceptionDetails ?? environment.IsDevelopment();
    }

    /// <summary>Sends the activated extensions as initial metadata. Does nothing when none are active.</summary>
    public static Task WriteExtensionsHeaderAsync(GrpcContext context, ServerCallContext call)
    {
        return call.ActivatedExtensions.Count == 0
            ? Task.CompletedTask
            : context.WriteResponseHeadersAsync(new Metadata
                { { GrpcErrors.ExtensionsKey, string.Join(",", call.ActivatedExtensions) } });
    }

    /// <summary>
    ///     The exception to throw for a failed call. The activated extensions are echoed in the trailers, as the HTTP bindings
    ///     echo
    ///     them in the error response, unless the initial metadata already carried them (<paramref name="headersSent" />).
    /// </summary>
    /// <remarks>
    ///     An Unauthenticated error also carries a <c>www-authenticate</c> challenge naming the scheme, as the HTTP bindings
    ///     send <c>WWW-Authenticate</c> (spec 3.3.2, 7.4).
    /// </remarks>
    /// <param name="ex">The failure.</param>
    /// <param name="context">The gRPC call.</param>
    /// <param name="call">The A2A call, or null when it could not be built.</param>
    /// <param name="headersSent">True when the initial metadata was sent.</param>
    /// <param name="operation">
    ///     The operation of the gRPC method, recorded for a call that failed before the operation pipeline (
    ///     <see cref="IA2ARequestHandler.RecordRefusedCall" />); null when the method does not name one.
    /// </param>
    public async Task<RpcException> FailAsync(Exception ex, GrpcContext context, ServerCallContext? call, bool headersSent,
        string? operation)
    {
        // A call the pipeline saw is traced and counted there already: this records only one refused before it.
        handler.RecordRefusedCall(call ?? MinimalCall(context), operation, Normalize(ex, context));
        if (call is { ActivatedExtensions.Count: > 0 } && !headersSent)
            context.ResponseTrailers.Add(GrpcErrors.ExtensionsKey, string.Join(",", call.ActivatedExtensions));
        if (call is not null && ex is A2AException { Code: A2AErrorCode.Unauthenticated })
        {
            var scheme = await context.GetHttpContext().GetA2AAuthChallengeSchemeAsync(call).ConfigureAwait(false);
            context.ResponseTrailers.Add(GrpcErrors.AuthChallengeKey, scheme);
        }

        return Translate(ex, context);
    }

    /// <summary>
    ///     The gRPC status for a failure: A2A errors through the shared table; unexpected errors are logged and sent as a
    ///     generic
    ///     INTERNAL (with the exception text only when <see cref="A2AServerOptions.IncludeExceptionDetails" /> allows it).
    /// </summary>
    private RpcException Translate(Exception ex, GrpcContext context)
    {
        if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            return new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client"));
        return GrpcErrors.ToRpcException(NormalizeA2A(ex, context, log: true));
    }

    /// <summary>The operation of a method of <c>lf.a2a.v1.A2AService</c>: its name (<c>/lf.a2a.v1.A2AService/GetTask</c>).</summary>
    public static string OperationOf(GrpcContext context)
    {
        var method = context.Method;
        return method[(method.LastIndexOf('/') + 1)..];
    }

    /// <summary>The call context of a call refused before its own could be built: the binding and the trace context.</summary>
    private static ServerCallContext MinimalCall(GrpcContext context)
    {
        var headers = context.GetHttpContext().Request.Headers;
        return new ServerCallContext
        {
            Binding = ProtocolBindings.Grpc,
            TraceParent = A2A.Diagnostics.A2ATelemetry.TryExtractTraceContext(
                headers[A2A.Diagnostics.A2ATelemetry.TraceParentHeader],
                headers[A2A.Diagnostics.A2ATelemetry.TraceStateHeader], out var parent)
                ? parent
                : default
        };
    }

    /// <summary>
    ///     The error a failure is recorded as: the caller's cancellation as it is, the request's own errors with their code,
    ///     and anything else a generic INTERNAL (not logged here: <see cref="Translate" /> logs it).
    /// </summary>
    private Exception Normalize(Exception ex, GrpcContext context)
    {
        if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested) return ex;
        return NormalizeA2A(ex, context, log: false);
    }

    private A2AException NormalizeA2A(Exception ex, GrpcContext context, bool log)
    {
        // The same normalization as the HTTP bindings: the request's own errors keep their code, anything else is a generic
        // INTERNAL (with the exception text only when IncludeExceptionDetails allows it).
        var a2a = ex switch
        {
            A2AException e => e,
            JsonException json => A2AException.InvalidParams($"Invalid request body: {json.Message}"),
            BadHttpRequestException bad => new A2AException(A2AErrorCode.InvalidRequest, bad.Message),
            _ => null
        };
        if (a2a is null)
        {
            if (log) GrpcServerLog.UnhandledError(logger, ex, context.Method);
            a2a = includeDetails
                ? new A2AException(A2AErrorCode.Internal,
                    $"{A2AErrors.DefaultMessage(A2AErrorCode.Internal)}: {ex.GetType().Name}: {ex.Message}")
                : new A2AException(A2AErrorCode.Internal);
        }

        return a2a;
    }
}