using A2A.Server.Pipeline;

namespace A2A.Server.Authorization;

/// <summary>The authorization step every operation starts with: run by <see cref="AuthorizationFilter"/>, and by the task streamer when an idempotent stream retry resumes its task.</summary>
internal static class OperationAuthorization
{
    /// <summary>
    /// Refuses anonymous callers unless <see cref="A2AServerOptions.AllowAnonymous"/> is set (so direct callers that skip
    /// <see cref="A2ARequestHandler.ValidateCall"/> are covered), then asks <paramref name="handler"/> about <paramref name="operation"/>.
    /// A denial of a task-scoped operation looks like a missing task; any other denial is <see cref="A2AErrorCode.PermissionDenied"/>.
    /// </summary>
    public static async Task RequireAsync(this A2AAuthorizationHandler handler, A2AServerOptions options, ServerCallContext call,
        A2AOperationDescriptor operation, object request, string? taskId, CancellationToken cancellationToken)
    {
        if (!options.AllowAnonymous && !call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated);
        if (await handler.AuthorizeAsync(call, operation, request, cancellationToken).ConfigureAwait(false)) return;
        if (!string.IsNullOrEmpty(taskId)) throw A2AException.TaskNotFound(taskId);
        throw new A2AException(A2AErrorCode.PermissionDenied);
    }
}
