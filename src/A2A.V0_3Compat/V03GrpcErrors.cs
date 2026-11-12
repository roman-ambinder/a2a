namespace A2A.V0_3Compat;

/// <summary>
///     A2A errors on the v0.3 gRPC binding. The v0.3 spec asks gRPC to "map A2A error codes to appropriate gRPC status
///     codes", and its proto defines no detail message, so this is the table of the official Python SDK 0.3
///     (<c>grpc_handler.abort_context</c>): a status code, and a message that starts with the error's name, for example
///     <c>TaskNotFoundError: ...</c>. The name prefix is what lets a client tell the errors that share a status apart.
/// </summary>
internal static class V03GrpcErrors
{
    // Grpc.Core.StatusCode values: this package has no gRPC dependency.
    public const int Unknown = 2, InvalidArgument = 3, NotFound = 5, PermissionDenied = 7, ResourceExhausted = 8,
        Unimplemented = 12, Internal = 13, Unavailable = 14, Unauthenticated = 16;

    private static readonly (A2AErrorCode Code, string Name, int Status)[] Table =
    [
        (A2AErrorCode.JsonParse, "JSONParseError", Internal),
        (A2AErrorCode.InvalidRequest, "InvalidRequestError", InvalidArgument),
        (A2AErrorCode.MethodNotFound, "MethodNotFoundError", NotFound),
        (A2AErrorCode.InvalidParams, "InvalidParamsError", InvalidArgument),
        (A2AErrorCode.Internal, "InternalError", Internal),
        (A2AErrorCode.TaskNotFound, "TaskNotFoundError", NotFound),
        (A2AErrorCode.TaskNotCancelable, "TaskNotCancelableError", Unimplemented),
        (A2AErrorCode.PushNotificationNotSupported, "PushNotificationNotSupportedError", Unimplemented),
        (A2AErrorCode.UnsupportedOperation, "UnsupportedOperationError", Unimplemented),
        (A2AErrorCode.ContentTypeNotSupported, "ContentTypeNotSupportedError", Unimplemented),
        (A2AErrorCode.InvalidAgentResponse, "InvalidAgentResponseError", Internal),
        // Not in the v0.3 Python table: the v1.0 codes v0.3 has no name for, with the gRPC status of v1.0.
        (A2AErrorCode.VersionNotSupported, "VersionNotSupportedError", InvalidArgument),
        (A2AErrorCode.ExtensionSupportRequired, "ExtensionSupportRequiredError", InvalidArgument),
        (A2AErrorCode.ExtendedAgentCardNotConfigured, "AuthenticatedExtendedCardNotConfiguredError", NotFound),
        (A2AErrorCode.RateLimited, "RateLimitedError", ResourceExhausted),
        (A2AErrorCode.Unauthenticated, "UnauthenticatedError", Unauthenticated),
        (A2AErrorCode.PermissionDenied, "PermissionDeniedError", PermissionDenied)
    ];

    /// <summary>The gRPC status and message of <paramref name="ex" />.</summary>
    public static (int Status, string Message) ToStatus(A2AException ex)
    {
        foreach (var (code, name, status) in Table)
            if (code == ex.Code)
                return (status, $"{name}: {ex.Message}");
        return (Unknown, $"Unknown error type: {ex.Message}");
    }

    /// <summary>
    ///     The A2A error of a v0.3 gRPC status: by the name prefix of the message when it has one, else by the status code
    ///     (the closest A2A error).
    /// </summary>
    public static A2AException FromStatus(int status, string? detail)
    {
        var message = detail ?? "";
        foreach (var (code, name, _) in Table)
            if (message.StartsWith(name + ":", StringComparison.Ordinal))
                return new A2AException(code, message[(name.Length + 1)..].Trim());
        var fallback = status switch
        {
            InvalidArgument => A2AErrorCode.InvalidParams,
            NotFound => A2AErrorCode.TaskNotFound,
            Unimplemented => A2AErrorCode.UnsupportedOperation,
            Unauthenticated => A2AErrorCode.Unauthenticated,
            ResourceExhausted => A2AErrorCode.RateLimited,
            PermissionDenied => A2AErrorCode.PermissionDenied,
            _ => A2AErrorCode.Internal
        };
        return new A2AException(fallback, message.Length > 0 ? message : $"gRPC status {status}")
        {
            // As on the v1 path (GrpcErrors): UNAVAILABLE is the gRPC form of HTTP 503, a dropped connection or a server
            // that is restarting. A resumable stream then reconnects instead of ending.
            HttpStatus = status == Unavailable ? 503 : null
        };
    }
}
