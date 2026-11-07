using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;

namespace A2A.Grpc;

/// <summary>Maps <see cref="A2AException"/> to and from gRPC status with a google.rpc.ErrorInfo detail (spec §10.6).</summary>
public static class GrpcErrors
{
    /// <summary>Metadata key for the A2A-Version service parameter (lowercase per gRPC, spec §10.2).</summary>
    public const string VersionKey = "a2a-version";

    /// <summary>Metadata key for the A2A-Extensions service parameter.</summary>
    public const string ExtensionsKey = "a2a-extensions";

    public static StatusCode ToStatusCode(A2AErrorCode code) => A2AErrors.GrpcStatus(code) switch
    {
        "NOT_FOUND" => StatusCode.NotFound,
        "INVALID_ARGUMENT" => StatusCode.InvalidArgument,
        "INTERNAL" => StatusCode.Internal,
        "UNAUTHENTICATED" => StatusCode.Unauthenticated,
        "PERMISSION_DENIED" => StatusCode.PermissionDenied,
        "FAILED_PRECONDITION" => StatusCode.FailedPrecondition,
        "RESOURCE_EXHAUSTED" => StatusCode.ResourceExhausted,
        "UNAVAILABLE" => StatusCode.Unavailable,
        "ALREADY_EXISTS" => StatusCode.AlreadyExists,
        "ABORTED" => StatusCode.Aborted,
        "DEADLINE_EXCEEDED" => StatusCode.DeadlineExceeded,
        "UNIMPLEMENTED" => StatusCode.Unimplemented,
        _ => StatusCode.Unknown,
    };

    public static RpcException ToRpcException(A2AException ex)
    {
        var info = new ErrorInfo { Reason = A2AErrors.Reason(ex.Code), Domain = A2AProtocol.ErrorDomain };
        if (ex.Metadata is not null)
            foreach (var kv in ex.Metadata) info.Metadata[kv.Key] = kv.Value;
        var status = new Google.Rpc.Status
        {
            Code = (int)ToStatusCode(ex.Code),
            Message = ex.Message,
            Details = { Any.Pack(info) },
        };
        return status.ToRpcException();
    }

    public static A2AException FromRpcException(RpcException ex)
    {
        var status = ex.GetRpcStatus();
        var info = status?.GetDetail<ErrorInfo>();
        A2AErrorCode code;
        if (info is not null && info.Domain == A2AProtocol.ErrorDomain && A2AErrors.TryParseReason(info.Reason, out var parsed))
        {
            code = parsed;
        }
        else
        {
            code = ex.StatusCode switch
            {
                StatusCode.NotFound => A2AErrorCode.TaskNotFound,
                StatusCode.InvalidArgument => A2AErrorCode.InvalidParams,
                StatusCode.Unauthenticated => A2AErrorCode.Unauthenticated,
                StatusCode.PermissionDenied => A2AErrorCode.PermissionDenied,
                StatusCode.Unimplemented => A2AErrorCode.MethodNotFound,
                StatusCode.FailedPrecondition => A2AErrorCode.UnsupportedOperation,
                _ => A2AErrorCode.Internal,
            };
        }
        var message = status?.Message ?? ex.Status.Detail;
        return new A2AException(code, string.IsNullOrEmpty(message) ? null : message, ex)
        {
            Metadata = info?.Metadata.Count > 0 ? info.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value) : null,
        };
    }
}
