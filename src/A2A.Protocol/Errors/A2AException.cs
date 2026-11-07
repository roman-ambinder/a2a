using System.Text.Json;

namespace A2A;

/// <summary>Protocol error codes. Values are the JSON-RPC codes (spec §5.4, §9.5).</summary>
public enum A2AErrorCode
{
    // JSON-RPC standard
    JsonParse = -32700,
    InvalidRequest = -32600,
    MethodNotFound = -32601,
    InvalidParams = -32602,
    Internal = -32603,

    // A2A specific
    TaskNotFound = -32001,
    TaskNotCancelable = -32002,
    PushNotificationNotSupported = -32003,
    UnsupportedOperation = -32004,
    ContentTypeNotSupported = -32005,
    InvalidAgentResponse = -32006,
    ExtendedAgentCardNotConfigured = -32007,
    ExtensionSupportRequired = -32008,
    VersionNotSupported = -32009,

    // Transport-level (HTTP 401/403). Never sent as JSON-RPC codes by this SDK; the server answers with an HTTP status instead.
    Unauthenticated = -1,
    PermissionDenied = -2,
}

/// <summary>An A2A protocol error. Thrown by clients on error responses and by servers to produce error responses.</summary>
public class A2AException : Exception
{
    public A2AException(A2AErrorCode code, string? message = null, Exception? inner = null)
        : base(message ?? A2AErrors.DefaultMessage(code), inner)
    {
        Code = code;
    }

    public A2AErrorCode Code { get; }

    /// <summary>Structured error details (ProtoJSON <c>Any</c> objects, each with an <c>@type</c> key).</summary>
    public List<JsonElement>? Details { get; init; }

    /// <summary>Extra ErrorInfo metadata (e.g. taskId) added to the details on the wire.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>HTTP status received by the client, when applicable.</summary>
    public int? HttpStatus { get; init; }

    public static A2AException TaskNotFound(string taskId) =>
        new(A2AErrorCode.TaskNotFound, "The specified task ID does not exist or is not accessible")
        {
            Metadata = new() { ["taskId"] = taskId },
        };

    public static A2AException InvalidParams(string message) => new(A2AErrorCode.InvalidParams, message);
}

/// <summary>Maps error codes to their HTTP status, gRPC status, ErrorInfo reason, and default message.</summary>
public static class A2AErrors
{
    public const string ErrorInfoType = "type.googleapis.com/google.rpc.ErrorInfo";

    public static int HttpStatus(A2AErrorCode code) => code switch
    {
        A2AErrorCode.TaskNotFound or A2AErrorCode.MethodNotFound => 404,
        A2AErrorCode.InvalidAgentResponse or A2AErrorCode.Internal => 500,
        A2AErrorCode.Unauthenticated => 401,
        A2AErrorCode.PermissionDenied => 403,
        _ => 400,
    };

    public static string GrpcStatus(A2AErrorCode code) => code switch
    {
        A2AErrorCode.TaskNotFound or A2AErrorCode.MethodNotFound => "NOT_FOUND",
        A2AErrorCode.ContentTypeNotSupported or A2AErrorCode.InvalidParams or A2AErrorCode.InvalidRequest
            or A2AErrorCode.JsonParse => "INVALID_ARGUMENT",
        A2AErrorCode.InvalidAgentResponse or A2AErrorCode.Internal => "INTERNAL",
        A2AErrorCode.Unauthenticated => "UNAUTHENTICATED",
        A2AErrorCode.PermissionDenied => "PERMISSION_DENIED",
        _ => "FAILED_PRECONDITION",
    };

    /// <summary>ErrorInfo reason: the error type in UPPER_SNAKE_CASE without the "Error" suffix (spec §11.6).</summary>
    public static string Reason(A2AErrorCode code) => code switch
    {
        A2AErrorCode.TaskNotFound => "TASK_NOT_FOUND",
        A2AErrorCode.TaskNotCancelable => "TASK_NOT_CANCELABLE",
        A2AErrorCode.PushNotificationNotSupported => "PUSH_NOTIFICATION_NOT_SUPPORTED",
        A2AErrorCode.UnsupportedOperation => "UNSUPPORTED_OPERATION",
        A2AErrorCode.ContentTypeNotSupported => "CONTENT_TYPE_NOT_SUPPORTED",
        A2AErrorCode.InvalidAgentResponse => "INVALID_AGENT_RESPONSE",
        A2AErrorCode.ExtendedAgentCardNotConfigured => "EXTENDED_AGENT_CARD_NOT_CONFIGURED",
        A2AErrorCode.ExtensionSupportRequired => "EXTENSION_SUPPORT_REQUIRED",
        A2AErrorCode.VersionNotSupported => "VERSION_NOT_SUPPORTED",
        A2AErrorCode.JsonParse => "JSON_PARSE",
        A2AErrorCode.InvalidRequest => "INVALID_REQUEST",
        A2AErrorCode.MethodNotFound => "METHOD_NOT_FOUND",
        A2AErrorCode.InvalidParams => "INVALID_PARAMS",
        A2AErrorCode.Unauthenticated => "UNAUTHENTICATED",
        A2AErrorCode.PermissionDenied => "PERMISSION_DENIED",
        _ => "INTERNAL",
    };

    public static bool TryParseReason(string? reason, out A2AErrorCode code)
    {
        foreach (var c in Enum.GetValues<A2AErrorCode>())
        {
            if (Reason(c) == reason) { code = c; return true; }
        }
        code = A2AErrorCode.Internal;
        return false;
    }

    public static string DefaultMessage(A2AErrorCode code) => code switch
    {
        A2AErrorCode.JsonParse => "Invalid JSON payload",
        A2AErrorCode.InvalidRequest => "Request payload validation error",
        A2AErrorCode.MethodNotFound => "Method not found",
        A2AErrorCode.InvalidParams => "Invalid parameters",
        A2AErrorCode.TaskNotFound => "Task not found",
        A2AErrorCode.TaskNotCancelable => "Task cannot be canceled",
        A2AErrorCode.PushNotificationNotSupported => "Push notifications are not supported",
        A2AErrorCode.UnsupportedOperation => "This operation is not supported",
        A2AErrorCode.ContentTypeNotSupported => "Incompatible content types",
        A2AErrorCode.InvalidAgentResponse => "Invalid agent response",
        A2AErrorCode.ExtendedAgentCardNotConfigured => "Extended agent card is not configured",
        A2AErrorCode.ExtensionSupportRequired => "A required extension was not declared by the client",
        A2AErrorCode.VersionNotSupported => "Protocol version not supported",
        A2AErrorCode.Unauthenticated => "Authentication required",
        A2AErrorCode.PermissionDenied => "Permission denied",
        _ => "Internal error",
    };

    /// <summary>Builds the details array for an exception, always including a google.rpc.ErrorInfo entry.</summary>
    public static List<JsonElement> BuildDetails(A2AException ex)
    {
        var details = new List<JsonElement>();
        var info = new Dictionary<string, object?>
        {
            ["@type"] = ErrorInfoType,
            ["reason"] = Reason(ex.Code),
            ["domain"] = A2AProtocol.ErrorDomain,
        };
        if (ex.Metadata is { Count: > 0 }) info["metadata"] = ex.Metadata;
        details.Add(JsonSerializer.SerializeToElement(info));
        if (ex.Details is not null)
        {
            details.AddRange(ex.Details.Where(d =>
                !(d.ValueKind == JsonValueKind.Object && d.TryGetProperty("@type", out var t) && t.GetString() == ErrorInfoType)));
        }
        return details;
    }

    /// <summary>Recovers an error code from a details array (looks for an ErrorInfo with the A2A domain).</summary>
    public static bool TryGetCodeFromDetails(IEnumerable<JsonElement>? details, out A2AErrorCode code)
    {
        code = A2AErrorCode.Internal;
        if (details is null) return false;
        foreach (var d in details)
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            if (!d.TryGetProperty("@type", out var t) || t.GetString() != ErrorInfoType) continue;
            if (d.TryGetProperty("domain", out var dom) && dom.GetString() != A2AProtocol.ErrorDomain) continue;
            if (d.TryGetProperty("reason", out var r) && TryParseReason(r.GetString(), out code)) return true;
        }
        return false;
    }
}
