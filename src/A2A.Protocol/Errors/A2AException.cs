using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>Protocol error codes. Values are the JSON-RPC codes (spec §5.4, §9.5).</summary>
public enum A2AErrorCode
{
    // JSON-RPC standard
    /// <summary>The request body is not valid JSON (JSON-RPC -32700).</summary>
    JsonParse = -32700,

    /// <summary>The request is not a valid request object (JSON-RPC -32600).</summary>
    InvalidRequest = -32600,

    /// <summary>The method does not exist (JSON-RPC -32601).</summary>
    MethodNotFound = -32601,

    /// <summary>The method parameters are invalid (JSON-RPC -32602).</summary>
    InvalidParams = -32602,

    /// <summary>The server failed internally (JSON-RPC -32603). Retryable by default.</summary>
    Internal = -32603,

    // A2A specific
    /// <summary>The task id does not exist or the caller cannot see it.</summary>
    TaskNotFound = -32001,

    /// <summary>The task is in a state where it cannot be canceled.</summary>
    TaskNotCancelable = -32002,

    /// <summary>The agent does not support push notifications.</summary>
    PushNotificationNotSupported = -32003,

    /// <summary>The agent does not support the requested operation.</summary>
    UnsupportedOperation = -32004,

    /// <summary>The media types of the request or the requested output are not supported.</summary>
    ContentTypeNotSupported = -32005,

    /// <summary>The agent produced a response that violates the protocol.</summary>
    InvalidAgentResponse = -32006,

    /// <summary>The agent has no extended Agent Card to return.</summary>
    ExtendedAgentCardNotConfigured = -32007,

    /// <summary>A required extension was not declared by the client in <c>A2A-Extensions</c>.</summary>
    ExtensionSupportRequired = -32008,

    /// <summary>The server does not support the protocol version the client asked for.</summary>
    VersionNotSupported = -32009,

    /// <summary>
    ///     A2A 1.1: <c>ifGenerationMatch</c> did not equal the task's generation. Re-read the task before sending again
    ///     (HTTP 409, gRPC <c>ABORTED</c>).
    /// </summary>
    TaskGenerationMismatch = -32010,

    // SDK extension: server is throttling the caller. Retryable; see A2AException.RetryAfter.
    /// <summary>The server is throttling the caller (SDK extension). Retryable.</summary>
    RateLimited = -32000,

    // Transport-level (HTTP 401/403). Never sent as JSON-RPC codes by this SDK; the server answers with an HTTP status instead.
    /// <summary>The caller is not authenticated (HTTP 401).</summary>
    Unauthenticated = -1,

    /// <summary>The caller is authenticated but not allowed (HTTP 403).</summary>
    PermissionDenied = -2
}

/// <summary>An A2A protocol error. Thrown by clients on error responses and by servers to produce error responses.</summary>
public class A2AException : Exception
{
    private readonly bool? _retryable;

    /// <summary>
    ///     Creates an error with a code. When <paramref name="message" /> is null, the default message for
    ///     <paramref name="code" /> is used.
    /// </summary>
    public A2AException(A2AErrorCode code, string? message = null, Exception? inner = null)
        : base(message ?? A2AErrors.DefaultMessage(code), inner)
    {
        Code = code;
    }

    private A2AException(A2AErrorCode code, TimeSpan retryAfter) : this(code)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The protocol error code.</summary>
    public A2AErrorCode Code { get; }

    /// <summary>Structured error details (ProtoJSON <c>Any</c> objects, each with an <c>@type</c> key).</summary>
    public List<JsonElement>? Details { get; init; }

    /// <summary>Extra ErrorInfo metadata (e.g. taskId) added to the details on the wire.</summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    ///     HTTP status received by the client, or the status a server should answer with. Overrides the status derived
    ///     from <see cref="Code" />.
    /// </summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    ///     Whether retrying the same request may succeed. Defaults from <see cref="Code" />. Written as
    ///     <c>metadata.retryable</c>.
    /// </summary>
    public bool Retryable
    {
        get => _retryable ?? A2AErrors.DefaultRetryable(Code);
        init => _retryable = value;
    }

    /// <summary>
    ///     How long the caller should wait before retrying. Written as <c>metadata.retryAfter</c> (whole seconds) and the
    ///     <c>Retry-After</c> header.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>ErrorInfo reason to send instead of the one derived from <see cref="Code" />.</summary>
    public string? ReasonOverride { get; init; }

    /// <summary>
    ///     Client side: true when the server marked the error retryable (<c>metadata.retryable</c> in the parsed details),
    ///     otherwise the default for <see cref="Code" />.
    /// </summary>
    public bool IsRetryable => A2AErrors.TryGetRetryableFromDetails(Details, out var r) ? r : Retryable;

    /// <summary>Creates the TaskNotFound error for <paramref name="taskId" />, with the id in the error metadata.</summary>
    public static A2AException TaskNotFound(string taskId)
    {
        return new A2AException(A2AErrorCode.TaskNotFound, "The specified task ID does not exist or is not accessible")
        {
            Metadata = new Dictionary<string, string> { ["taskId"] = taskId }
        };
    }

    /// <summary>Creates an InvalidParams error with <paramref name="message" />.</summary>
    public static A2AException InvalidParams(string message)
    {
        return new A2AException(A2AErrorCode.InvalidParams, message);
    }

    /// <summary>
    ///     Creates an InvalidParams error for one request field: the message is <c>"{field} {description}"</c>, and the
    ///     details carry a <c>google.rpc.BadRequest</c> with that field violation (spec §9.5, §11.6).
    /// </summary>
    public static A2AException InvalidField(string field, string description)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(description);
        return new A2AException(A2AErrorCode.InvalidParams, field + " " + description)
        {
            Details = [A2AErrors.BadRequest([new KeyValuePair<string, string>(field, description)])]
        };
    }

    /// <summary>The request body is larger than <paramref name="limit" /> bytes. HTTP 413.</summary>
    public static A2AException PayloadTooLarge(long limit)
    {
        return new A2AException(A2AErrorCode.InvalidRequest, $"Request body exceeds the limit of {limit} bytes")
        {
            ReasonOverride = A2AErrors.PayloadTooLargeReason,
            HttpStatus = 413
        };
    }

    /// <summary>The idempotency key was reused with a different request. HTTP 422.</summary>
    public static A2AException IdempotencyKeyMismatch()
    {
        return new A2AException(A2AErrorCode.InvalidRequest,
            "The idempotency key was already used with a different request")
        {
            ReasonOverride = A2AErrors.IdempotencyKeyMismatchReason,
            HttpStatus = 422
        };
    }

    /// <summary>The caller is sending too many requests. Retryable after <paramref name="retryAfter" />. HTTP 429.</summary>
    public static A2AException RateLimited(TimeSpan retryAfter)
    {
        return new A2AException(A2AErrorCode.RateLimited, retryAfter) { HttpStatus = 429 };
    }
}

/// <summary>Maps error codes to their HTTP status, gRPC status, ErrorInfo reason, and default message.</summary>
public static class A2AErrors
{
    /// <summary>Type URL of the <c>google.rpc.ErrorInfo</c> detail entry.</summary>
    public const string ErrorInfoType = "type.googleapis.com/google.rpc.ErrorInfo";

    /// <summary>Type URL of the <c>google.rpc.BadRequest</c> detail entry (field violations of a validation error).</summary>
    public const string BadRequestType = "type.googleapis.com/google.rpc.BadRequest";

    /// <summary>
    ///     A <c>google.rpc.BadRequest</c> detail entry in its ProtoJSON <c>Any</c> form, with one field violation per
    ///     (field, description) pair.
    /// </summary>
    public static JsonElement BadRequest(IEnumerable<KeyValuePair<string, string>> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("@type", BadRequestType);
            writer.WriteStartArray("fieldViolations");
            foreach (var violation in violations)
            {
                writer.WriteStartObject();
                writer.WriteString("field", violation.Key);
                writer.WriteString("description", violation.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>True when <paramref name="detail" /> is a JSON object with a string <c>@type</c> key.</summary>
    public static bool HasType(JsonElement detail)
    {
        return detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("@type", out var type) &&
               type.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(type.GetString());
    }

    /// <summary>ErrorInfo reason for a request body over the size limit.</summary>
    public const string PayloadTooLargeReason = "PAYLOAD_TOO_LARGE";

    /// <summary>ErrorInfo reason for an idempotency key reused with a different request.</summary>
    public const string IdempotencyKeyMismatchReason = "IDEMPOTENCY_KEY_MISMATCH";

    /// <summary>Returns the HTTP status for an error code (400 unless the code has a specific status).</summary>
    public static int HttpStatus(A2AErrorCode code)
    {
        return code switch
        {
            A2AErrorCode.RateLimited => 429,
            A2AErrorCode.TaskNotFound or A2AErrorCode.MethodNotFound => 404,
            A2AErrorCode.TaskGenerationMismatch => 409,
            A2AErrorCode.InvalidAgentResponse or A2AErrorCode.Internal => 500,
            A2AErrorCode.Unauthenticated => 401,
            A2AErrorCode.PermissionDenied => 403,
            _ => 400
        };
    }

    /// <summary>Returns the gRPC status name for an error code, for example <c>NOT_FOUND</c>.</summary>
    public static string GrpcStatus(A2AErrorCode code)
    {
        return code switch
        {
            A2AErrorCode.TaskNotFound or A2AErrorCode.MethodNotFound => "NOT_FOUND",
            A2AErrorCode.ContentTypeNotSupported or A2AErrorCode.InvalidParams or A2AErrorCode.InvalidRequest
                or A2AErrorCode.JsonParse => "INVALID_ARGUMENT",
            A2AErrorCode.InvalidAgentResponse or A2AErrorCode.Internal => "INTERNAL",
            A2AErrorCode.Unauthenticated => "UNAUTHENTICATED",
            A2AErrorCode.PermissionDenied => "PERMISSION_DENIED",
            A2AErrorCode.RateLimited => "RESOURCE_EXHAUSTED",
            A2AErrorCode.TaskGenerationMismatch => "ABORTED",
            _ => "FAILED_PRECONDITION"
        };
    }

    /// <summary>ErrorInfo reason: the error type in UPPER_SNAKE_CASE without the "Error" suffix (spec §11.6).</summary>
    public static string Reason(A2AErrorCode code)
    {
        return code switch
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
            A2AErrorCode.TaskGenerationMismatch => "TASK_GENERATION_MISMATCH",
            A2AErrorCode.JsonParse => "JSON_PARSE",
            A2AErrorCode.InvalidRequest => "INVALID_REQUEST",
            A2AErrorCode.MethodNotFound => "METHOD_NOT_FOUND",
            A2AErrorCode.InvalidParams => "INVALID_PARAMS",
            A2AErrorCode.Unauthenticated => "UNAUTHENTICATED",
            A2AErrorCode.PermissionDenied => "PERMISSION_DENIED",
            A2AErrorCode.RateLimited => "RATE_LIMITED",
            _ => "INTERNAL"
        };
    }

    /// <summary>True for codes where retrying the same request may succeed.</summary>
    public static bool DefaultRetryable(A2AErrorCode code)
    {
        return code is A2AErrorCode.Internal or A2AErrorCode.RateLimited;
    }

    /// <summary>Maps an ErrorInfo reason back to an error code. Returns false, with <c>Internal</c>, for an unknown reason.</summary>
    public static bool TryParseReason(string? reason, out A2AErrorCode code)
    {
        if (reason is PayloadTooLargeReason or IdempotencyKeyMismatchReason)
        {
            code = A2AErrorCode.InvalidRequest;
            return true;
        }

        foreach (var c in Enum.GetValues<A2AErrorCode>())
            if (Reason(c) == reason)
            {
                code = c;
                return true;
            }

        code = A2AErrorCode.Internal;
        return false;
    }

    /// <summary>Returns the default message for an error code.</summary>
    public static string DefaultMessage(A2AErrorCode code)
    {
        return code switch
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
            A2AErrorCode.TaskGenerationMismatch => "Task generation mismatch",
            A2AErrorCode.Unauthenticated => "Authentication required",
            A2AErrorCode.PermissionDenied => "Permission denied",
            A2AErrorCode.RateLimited => "Too many requests",
            _ => "Internal error"
        };
    }

    /// <summary>
    ///     Builds the details array for an exception, always including a google.rpc.ErrorInfo entry. Entries of
    ///     <see cref="A2AException.Details" /> follow, except another ErrorInfo and any entry that is not an object with an
    ///     <c>@type</c> key (spec §3.3.2: each detail object MUST carry one).
    /// </summary>
    public static List<JsonElement> BuildDetails(A2AException ex)
    {
        var details = new List<JsonElement>();
        var metadata = ex.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ex.Metadata);
        metadata["retryable"] = ex.Retryable ? "true" : "false";
        if (ex.RetryAfter is { } after)
            metadata["retryAfter"] = ((long)Math.Ceiling(after.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        var info = new ErrorInfoDetail
        {
            Reason = ex.ReasonOverride ?? Reason(ex.Code),
            Domain = A2AProtocol.ErrorDomain,
            Metadata = metadata
        };
        details.Add(A2AJson.SerializeToElement(info));
        if (ex.Details is not null)
            details.AddRange(ex.Details.Where(d =>
                HasType(d) && d.GetProperty("@type").GetString() != ErrorInfoType));
        return details;
    }

    /// <summary>Reads <c>metadata.retryable</c> from the A2A ErrorInfo in a details array, when present.</summary>
    public static bool TryGetRetryableFromDetails(IEnumerable<JsonElement>? details, out bool retryable)
    {
        retryable = false;
        if (details is null) return false;
        foreach (var d in details)
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            if (!d.TryGetProperty("@type", out var t) || t.GetString() != ErrorInfoType) continue;
            if (d.TryGetProperty("domain", out var dom) && dom.GetString() != A2AProtocol.ErrorDomain) continue;
            if (!d.TryGetProperty("metadata", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("retryable", out var r) || r.ValueKind != JsonValueKind.String) continue;
            if (bool.TryParse(r.GetString(), out retryable)) return true;
        }

        return false;
    }

    /// <summary>
    ///     The string metadata of the A2A ErrorInfo in a details array (the first ErrorInfo with the A2A domain), or null
    ///     when there is none or it has no metadata. Clients put it in <see cref="A2AException.Metadata" />, so an error
    ///     reads the same on every binding.
    /// </summary>
    public static Dictionary<string, string>? GetErrorInfoMetadata(IEnumerable<JsonElement>? details)
    {
        if (details is null) return null;
        foreach (var d in details)
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            if (!d.TryGetProperty("@type", out var t) || t.GetString() != ErrorInfoType) continue;
            if (d.TryGetProperty("domain", out var dom) && dom.GetString() != A2AProtocol.ErrorDomain) continue;
            if (!d.TryGetProperty("metadata", out var m) || m.ValueKind != JsonValueKind.Object) return null;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in m.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    metadata[p.Name] = p.Value.GetString()!;
            return metadata.Count > 0 ? metadata : null;
        }

        return null;
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

/// <summary>Error body of the HTTP+JSON binding: <c>{"error": {...}}</c> (spec §11.6).</summary>
public sealed class RestErrorBody
{
    /// <summary>The error object.</summary>
    public RestError Error { get; set; } = new();
}

/// <summary>The <c>error</c> object of <see cref="RestErrorBody" />.</summary>
public sealed class RestError
{
    /// <summary>HTTP status code of the response.</summary>
    public int Code { get; set; }

    /// <summary>gRPC status name, for example <c>NOT_FOUND</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Structured details, including the ErrorInfo entry.</summary>
    public List<JsonElement> Details { get; set; } = new();
}

/// <summary>The <c>google.rpc.ErrorInfo</c> detail entry the SDK emits in every error (spec §9.5).</summary>
internal sealed class ErrorInfoDetail
{
    [JsonPropertyName("@type")] public string Type { get; init; } = A2AErrors.ErrorInfoType;

    public string Reason { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new();
}