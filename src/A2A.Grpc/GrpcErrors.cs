using System.Globalization;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;

namespace A2A.Grpc;

/// <summary>
/// Maps <see cref="A2AException"/> to and from gRPC status with a <c>google.rpc.ErrorInfo</c> detail (spec §10.6). The status
/// code comes from the shared table (<see cref="A2AErrors.GrpcStatus"/>), so every binding reports an error the same way.
/// </summary>
public static class GrpcErrors
{
    /// <summary>Metadata key for the A2A-Version service parameter (lowercase per gRPC, spec §10.2).</summary>
    public const string VersionKey = "a2a-version";

    /// <summary>Metadata key for the A2A-Extensions service parameter.</summary>
    public const string ExtensionsKey = "a2a-extensions";

    /// <summary>Metadata key for the idempotency key of SendMessage (the <c>Idempotency-Key</c> header of the HTTP bindings).</summary>
    public const string IdempotencyKeyKey = "idempotency-key";

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

    /// <summary>
    /// The <c>google.rpc.Status</c> for an error: the code from the shared table, the message, and an <c>ErrorInfo</c> with the
    /// reason (<see cref="A2AException.ReasonOverride"/> first), the A2A domain and the metadata, including <c>retryable</c> and
    /// <c>retryAfter</c> (whole seconds) as in the HTTP bindings. A <c>google.rpc.RetryInfo</c> is added when
    /// <see cref="A2AException.RetryAfter"/> is set.
    /// </summary>
    public static Google.Rpc.Status ToStatus(A2AException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var info = new ErrorInfo { Reason = ex.ReasonOverride ?? A2AErrors.Reason(ex.Code), Domain = A2AProtocol.ErrorDomain };
        if (ex.Metadata is not null)
            foreach (var kv in ex.Metadata) info.Metadata[kv.Key] = kv.Value;
        info.Metadata["retryable"] = ex.Retryable ? "true" : "false";
        var status = new Google.Rpc.Status
        {
            Code = (int)ToStatusCode(ex.Code),
            Message = ex.Message,
        };
        if (ex.RetryAfter is { } after)
        {
            var seconds = (long)Math.Ceiling(after.TotalSeconds);
            info.Metadata["retryAfter"] = seconds.ToString(CultureInfo.InvariantCulture);
            status.Details.Add(Any.Pack(info));
            status.Details.Add(Any.Pack(new RetryInfo { RetryDelay = new Duration { Seconds = seconds } }));
        }
        else
        {
            status.Details.Add(Any.Pack(info));
        }
        return status;
    }

    public static RpcException ToRpcException(A2AException ex) => ToStatus(ex).ToRpcException();

    public static A2AException FromRpcException(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var status = ex.GetRpcStatus();
        var info = status?.GetDetail<ErrorInfo>();
        if (info is not null && info.Domain != A2AProtocol.ErrorDomain) info = null;
        A2AErrorCode code;
        if (info is not null && A2AErrors.TryParseReason(info.Reason, out var parsed))
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
        var reason = info?.Reason;
        return new A2AException(code, string.IsNullOrEmpty(message) ? null : message, ex)
        {
            Metadata = info?.Metadata.Count > 0 ? info.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value) : null,
            Details = info is null ? null : [ToJson(info)],
            RetryAfter = RetryAfterOf(status, info),
            ReasonOverride = reason is A2AErrors.PayloadTooLargeReason or A2AErrors.IdempotencyKeyMismatchReason ? reason : null,
        };
    }

    /// <summary>The server's wait: <c>RetryInfo</c>, else <c>metadata.retryAfter</c> (whole seconds).</summary>
    private static TimeSpan? RetryAfterOf(Google.Rpc.Status? status, ErrorInfo? info)
    {
        if (status?.GetDetail<RetryInfo>()?.RetryDelay is { } delay) return delay.ToTimeSpan();
        if (info is not null && info.Metadata.TryGetValue("retryAfter", out var s)
            && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>The ErrorInfo in its ProtoJSON <c>Any</c> form, as the HTTP bindings carry it in <see cref="A2AException.Details"/>.</summary>
    private static JsonElement ToJson(ErrorInfo info)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("@type", A2AErrors.ErrorInfoType);
            w.WriteString("reason", info.Reason);
            w.WriteString("domain", info.Domain);
            w.WriteStartObject("metadata");
            foreach (var kv in info.Metadata) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }
}
