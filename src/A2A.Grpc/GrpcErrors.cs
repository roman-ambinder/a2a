using System.Globalization;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace A2A.Grpc;

/// <summary>
///     Maps <see cref="A2AException" /> to and from gRPC status with a <c>google.rpc.ErrorInfo</c> detail (spec §10.6).
///     The status
///     code comes from the shared table (<see cref="A2AErrors.GrpcStatus" />), so every binding reports an error the same
///     way.
/// </summary>
public static class GrpcErrors
{
    /// <summary>Metadata key for the A2A-Version service parameter (lowercase per gRPC, spec §10.2).</summary>
    public const string VersionKey = "a2a-version";

    /// <summary>Metadata key for the A2A-Extensions service parameter.</summary>
    public const string ExtensionsKey = "a2a-extensions";

    /// <summary>The metadata key of the authentication challenge on an UNAUTHENTICATED status (spec 3.3.2, 7.4).</summary>
    public const string AuthChallengeKey = "www-authenticate";

    /// <summary>Metadata key for the idempotency key of SendMessage (the <c>Idempotency-Key</c> header of the HTTP bindings).</summary>
    public const string IdempotencyKeyKey = "idempotency-key";

    /// <summary>Returns the gRPC status code for an A2A error code, for example NotFound for TaskNotFound.</summary>
    public static StatusCode ToStatusCode(A2AErrorCode code)
    {
        return A2AErrors.GrpcStatus(code) switch
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
            _ => StatusCode.Unknown
        };
    }

    /// <summary>
    ///     The <c>google.rpc.Status</c> for an error: the code from the shared table, the message, and an <c>ErrorInfo</c>
    ///     with the
    ///     reason (<see cref="A2AException.ReasonOverride" /> first), the A2A domain and the metadata, including
    ///     <c>retryable</c> and
    ///     <c>retryAfter</c> (whole seconds) as in the HTTP bindings. A <c>google.rpc.RetryInfo</c> is added when
    ///     <see cref="A2AException.RetryAfter" /> is set.
    /// </summary>
    public static Status ToStatus(A2AException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var info = new ErrorInfo
            { Reason = ex.ReasonOverride ?? A2AErrors.Reason(ex.Code), Domain = A2AProtocol.ErrorDomain };
        if (ex.Metadata is not null)
            foreach (var kv in ex.Metadata)
                info.Metadata[kv.Key] = kv.Value;
        info.Metadata["retryable"] = ex.Retryable ? "true" : "false";
        var status = new Status
        {
            Code = (int)ToStatusCode(ex.Code),
            Message = ex.Message
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

        if (ex.Details is not null)
            foreach (var detail in ex.Details)
                if (ToBadRequest(detail) is { } badRequest)
                    status.Details.Add(Any.Pack(badRequest));

        FitHeaderBudget(status, info);
        return status;
    }

    /// <summary>
    ///     The largest <c>google.rpc.Status</c>, in serialized bytes, that <see cref="ToStatus" /> returns. gRPC sends the
    ///     status in the <c>grpc-status-details-bin</c> trailer (base64, so 4/3 of this) next to <c>grpc-message</c>, and
    ///     peers cap trailers at 8 KiB (grpc-java, gRPC C core) or 16 KiB (tonic); an error over the cap is lost whole
    ///     (A2A#2433). 3 KiB leaves room for the message and the other trailers under 8 KiB.
    /// </summary>
    internal const int MaxStatusBytes = 3 * 1024;

    /// <summary>
    ///     The largest status message, in UTF-8 bytes. The message is sent twice (in the status and percent-encoded in
    ///     <c>grpc-message</c>, up to 3 bytes per byte), so it has its own cap.
    /// </summary>
    internal const int MaxMessageBytes = 1024;

    /// <summary>The <c>ErrorInfo</c> metadata key that says <see cref="ToStatus" /> left something out to fit the cap.</summary>
    internal const string TruncatedKey = "truncated";

    /// <summary>
    ///     Shrinks <paramref name="status" /> to <see cref="MaxStatusBytes" />: first the message to
    ///     <see cref="MaxMessageBytes" />, then the <c>BadRequest</c> details from the last one, then the
    ///     <c>ErrorInfo</c> metadata from the largest entry, then the message again. The <c>ErrorInfo</c> itself, its reason,
    ///     domain, <c>retryable</c> and <c>retryAfter</c>, and the <c>RetryInfo</c> always stay, so the client still gets
    ///     the A2A error code and its retry advice. When anything was left out, <c>ErrorInfo.metadata.truncated</c> is
    ///     <c>"true"</c>.
    /// </summary>
    private static void FitHeaderBudget(Status status, ErrorInfo info)
    {
        var truncated = false;
        var message = Truncate(status.Message, MaxMessageBytes);
        if (!ReferenceEquals(message, status.Message))
        {
            status.Message = message;
            truncated = true;
        }

        if (status.CalculateSize() > MaxStatusBytes)
        {
            truncated = true;
            for (var i = status.Details.Count - 1; i >= 0 && status.CalculateSize() > MaxStatusBytes; i--)
                if (status.Details[i].Is(BadRequest.Descriptor))
                    status.Details.RemoveAt(i);

            var removable = info.Metadata
                .Where(kv => kv.Key is not ("retryable" or "retryAfter"))
                .OrderByDescending(kv => kv.Key.Length + kv.Value.Length)
                .Select(kv => kv.Key)
                .ToList();
            var infoIndex = IndexOf(status, ErrorInfo.Descriptor);
            foreach (var key in removable)
            {
                if (status.CalculateSize() <= MaxStatusBytes) break;
                info.Metadata.Remove(key);
                info.Metadata[TruncatedKey] = "true";
                status.Details[infoIndex] = Any.Pack(info);
            }

            // Only the message is left to cut. Its length prefix can shrink by a byte as it gets shorter, so cut and measure.
            while (status.CalculateSize() > MaxStatusBytes && status.Message.Length > 0)
            {
                var over = status.CalculateSize() - MaxStatusBytes;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(status.Message);
                var cut = Truncate(status.Message, Math.Max(0, bytes - over));
                status.Message = cut.Length < status.Message.Length ? cut : "";
            }
        }

        if (truncated && !info.Metadata.ContainsKey(TruncatedKey))
        {
            info.Metadata[TruncatedKey] = "true";
            status.Details[IndexOf(status, ErrorInfo.Descriptor)] = Any.Pack(info);
        }
    }

    private static int IndexOf(Status status, Google.Protobuf.Reflection.MessageDescriptor descriptor)
    {
        for (var i = 0; i < status.Details.Count; i++)
            if (status.Details[i].Is(descriptor))
                return i;
        throw new InvalidOperationException("The status has no " + descriptor.Name);
    }

    /// <summary>
    ///     <paramref name="value" /> cut to at most <paramref name="maxBytes" /> UTF-8 bytes on a character boundary, ending
    ///     in "..." when cut (when the budget has room for it); the same instance when it fits.
    /// </summary>
    internal static string Truncate(string value, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var ellipsis = maxBytes >= 3 ? "..." : ""; // a budget under 3 bytes has no room for it
        var budget = Math.Max(0, maxBytes - ellipsis.Length);
        var sb = new System.Text.StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget) break;
            sb.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        return sb.Append(ellipsis).ToString();
    }

    /// <summary>The <c>google.rpc.BadRequest</c> in a ProtoJSON detail entry, or null when the entry is another type.</summary>
    private static BadRequest? ToBadRequest(JsonElement detail)
    {
        if (!A2AErrors.HasType(detail) || detail.GetProperty("@type").GetString() != A2AErrors.BadRequestType)
            return null;
        var badRequest = new BadRequest();
        if (detail.TryGetProperty("fieldViolations", out var violations) && violations.ValueKind == JsonValueKind.Array)
            foreach (var v in violations.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                badRequest.FieldViolations.Add(new BadRequest.Types.FieldViolation
                {
                    Field = v.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : "",
                    Description = v.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : ""
                });
            }

        return badRequest;
    }

    /// <summary>
    ///     The details of a received status as the HTTP bindings carry them: the A2A ErrorInfo, then each BadRequest. Null
    ///     when there is neither.
    /// </summary>
    private static List<JsonElement>? DetailsOf(Status? status, ErrorInfo? info)
    {
        var details = new List<JsonElement>();
        if (info is not null) details.Add(ToJson(info));
        if (status is not null)
            foreach (var any in status.Details)
                if (any.TryUnpack<BadRequest>(out var badRequest))
                    details.Add(ToJson(badRequest));
        return details.Count > 0 ? details : null;
    }

    /// <summary>The <c>google.rpc.BadRequest</c> in its ProtoJSON <c>Any</c> form.</summary>
    private static JsonElement ToJson(BadRequest badRequest)
    {
        return A2AErrors.BadRequest(badRequest.FieldViolations.Select(v =>
            new KeyValuePair<string, string>(v.Field, v.Description)));
    }

    /// <summary>
    ///     Converts an A2A error into an <see cref="RpcException" /> with a google.rpc.ErrorInfo detail (see
    ///     <see cref="ToStatus" />).
    /// </summary>
    public static RpcException ToRpcException(A2AException ex)
    {
        return ToStatus(ex).ToRpcException();
    }

    /// <summary>
    ///     Converts an <see cref="RpcException" /> into an A2A error. The ErrorInfo reason wins; without it the gRPC status
    ///     code
    ///     decides. A bare RESOURCE_EXHAUSTED (no ErrorInfo) is a message over the gRPC size limit: PAYLOAD_TOO_LARGE.
    /// </summary>
    public static A2AException FromRpcException(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var status = ex.GetRpcStatus();
        var info = status?.GetDetail<ErrorInfo>();
        if (info is not null && info.Domain != A2AProtocol.ErrorDomain) info = null;
        A2AErrorCode code;
        if (info is not null && A2AErrors.TryParseReason(info.Reason, out var parsed))
            code = parsed;
        else
            code = ex.StatusCode switch
            {
                StatusCode.NotFound => A2AErrorCode.TaskNotFound,
                StatusCode.InvalidArgument => A2AErrorCode.InvalidParams,
                StatusCode.Unauthenticated => A2AErrorCode.Unauthenticated,
                StatusCode.PermissionDenied => A2AErrorCode.PermissionDenied,
                StatusCode.Unimplemented => A2AErrorCode.MethodNotFound,
                StatusCode.FailedPrecondition => A2AErrorCode.UnsupportedOperation,
                _ => A2AErrorCode.Internal
            };
        var message = status?.Message ?? ex.Status.Detail;
        var reason = info?.Reason;
        int? httpStatus = null;
        if (info is null && ex.StatusCode == StatusCode.ResourceExhausted)
        {
            // A bare RESOURCE_EXHAUSTED comes from the gRPC layer itself: the message was over the size limit. Report it as
            // the HTTP bindings report a body over the limit (InvalidRequest, PAYLOAD_TOO_LARGE, 413).
            code = A2AErrorCode.InvalidRequest;
            reason = A2AErrors.PayloadTooLargeReason;
            httpStatus = 413;
        }

        if (info is null && ex.StatusCode == StatusCode.Unavailable) httpStatus = 503; // the gRPC form of HTTP 503
        return new A2AException(code, string.IsNullOrEmpty(message) ? null : message, ex)
        {
            HttpStatus = httpStatus,
            Metadata = info?.Metadata.Count > 0 ? info.Metadata.ToDictionary(kv => kv.Key, kv => kv.Value) : null,
            Details = DetailsOf(status, info),
            RetryAfter = RetryAfterOf(status, info),
            ReasonOverride = reason is A2AErrors.PayloadTooLargeReason or A2AErrors.IdempotencyKeyMismatchReason
                ? reason
                : null
        };
    }

    /// <summary>The server's wait: <c>RetryInfo</c>, else <c>metadata.retryAfter</c> (whole seconds).</summary>
    private static TimeSpan? RetryAfterOf(Status? status, ErrorInfo? info)
    {
        if (status?.GetDetail<RetryInfo>()?.RetryDelay is { } delay) return delay.ToTimeSpan();
        if (info is not null && info.Metadata.TryGetValue("retryAfter", out var s)
                             && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>
    ///     The ErrorInfo in its ProtoJSON <c>Any</c> form, as the HTTP bindings carry it in
    ///     <see cref="A2AException.Details" />.
    /// </summary>
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