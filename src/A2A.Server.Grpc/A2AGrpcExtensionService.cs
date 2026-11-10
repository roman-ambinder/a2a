using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using A2A.Grpc.Extensions.Protos;
using A2A.Json;
using A2A.Server.AspNetCore;
using A2A.Server.AspNetCore.Operations;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GrpcContext = Grpc.Core.ServerCallContext;

namespace A2A.Server.Grpc;

/// <summary>
///     Serves the extension operations (<see cref="A2A.Server.Pipeline.A2AExtensionOperation" />) over gRPC as
///     <c>a2a.extensions.v1.A2AExtensionService</c>, the same service definition as the official A2A .NET SDK. The request
///     names
///     the operation in <c>operation_id</c> (the operation's <c>Method</c>) and carries its params as UTF-8 JSON in
///     <c>payload</c>;
///     the response <c>payload</c> is the UTF-8 JSON result.
/// </summary>
/// <remarks>
///     <para>
///         A call behaves as on the HTTP+JSON binding: the same call context, version check, extension policy check,
///         tenant binding,
///         operation pipeline and error mapping as <see cref="A2AGrpcService" />. An empty payload means <c>{}</c>. A
///         payload that is
///         not JSON, or nests deeper than <see cref="A2ALimits.MaxJsonDepth" />, is <c>InvalidParams</c>; an unknown
///         <c>operation_id</c> is <c>MethodNotFound</c>.
///     </para>
///     <para>
///         Only the unary <c>InvokeExtensionOperation</c> is served. <c>InvokeStreamingExtensionOperation</c> answers
///         UNIMPLEMENTED,
///         because extension operations are unary on every binding. Map the service with
///         <see cref="A2AGrpcExtensions.MapA2AGrpcExtensions" />.
///     </para>
/// </remarks>
[Experimental("A2AEXP004",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class A2AGrpcExtensionService : A2AExtensionService.A2AExtensionServiceBase
{
    private readonly bool _allowAnonymous;
    private readonly IA2ARequestHandler _handler;
    private readonly int _maxDepth;
    private readonly GrpcCallSupport _support;
    private readonly A2AOperationTable _table;

    /// <summary>Creates the service. The DI container does this.</summary>
    public A2AGrpcExtensionService(IA2ARequestHandler handler, A2AOperationTable table,
        IOptions<A2AServerOptions> serverOptions,
        IHostEnvironment environment, ILogger<A2AGrpcExtensionService> logger)
    {
        _handler = handler;
        _table = table;
        _maxDepth = serverOptions.Value.Limits.MaxJsonDepth;
        _allowAnonymous = serverOptions.Value.AllowAnonymous;
        _support = new GrpcCallSupport(logger, GrpcCallSupport.ShouldIncludeDetails(serverOptions, environment));
    }

    /// <summary>Runs the extension operation named by <c>operation_id</c> (unary).</summary>
    public override async Task<ExtensionOperationResponse> InvokeExtensionOperation(ExtensionOperationRequest request,
        GrpcContext context)
    {
        ServerCallContext? call = null;
        try
        {
            var http = context.GetHttpContext();
            call = http.CreateA2ACallContext(ProtocolBindings.Grpc);
            _handler.ValidateCall(call);
            if (!_table.TryGetExtension(request.OperationId, out var operation))
                throw new A2AException(A2AErrorCode.MethodNotFound, $"Method '{request.OperationId}' not found");
            await ExtensionPolicy.RequireAsync(http, operation, call, _allowAnonymous).ConfigureAwait(false);
            var parameters = ParsePayload(request.Payload);
            var result = await _handler
                .InvokeExtensionAsync(operation.Method, parameters, call, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new ExtensionOperationResponse { Payload = ByteString.CopyFromUtf8(result.GetRawText()) };
            await GrpcCallSupport.WriteExtensionsHeaderAsync(context, call).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw _support.Fail(ex, context, call, false);
        }
    }

    /// <summary>The payload as a JSON value: empty is the empty object (as absent params in JSON-RPC).</summary>
    private JsonElement ParsePayload(ByteString payload)
    {
        if (payload.Length == 0) return A2AJson.EmptyObject;
        try
        {
            using var document = JsonDocument.Parse(payload.Memory, new JsonDocumentOptions { MaxDepth = _maxDepth });
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw A2AException.InvalidParams($"Invalid request body: {ex.Message}");
        }
    }
}