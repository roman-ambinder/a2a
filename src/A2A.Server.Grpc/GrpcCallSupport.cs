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
internal sealed class GrpcCallSupport(ILogger logger, bool includeDetails)
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
    public async Task<RpcException> FailAsync(Exception ex, GrpcContext context, ServerCallContext? call, bool headersSent)
    {
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
            GrpcServerLog.UnhandledError(logger, ex, context.Method);
            a2a = includeDetails
                ? new A2AException(A2AErrorCode.Internal,
                    $"{A2AErrors.DefaultMessage(A2AErrorCode.Internal)}: {ex.GetType().Name}: {ex.Message}")
                : new A2AException(A2AErrorCode.Internal);
        }

        return GrpcErrors.ToRpcException(a2a);
    }
}