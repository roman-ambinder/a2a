using A2A.Server.Authorization;

namespace A2A.Server.Pipeline;

/// <summary>
/// The built-in first step of the operation pipeline: <see cref="OperationAuthorization.RequireAsync"/> (the anonymous
/// backstop, then the <see cref="A2AAuthorizationHandler"/>). A denial of a call whose request names a task looks like a
/// missing task; any other denial is <see cref="A2AErrorCode.PermissionDenied"/>.
/// </summary>
internal sealed class AuthorizationFilter(A2AAuthorizationHandler handler, A2AServerOptions options) : IA2AOperationFilter
{
    public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
    {
        var authorized = RequireAsync(context.Operation, context.Call, request, cancellationToken);
        return authorized.IsCompletedSuccessfully ? next(context, request, cancellationToken) : ThenAsync(authorized, context, request, next, cancellationToken);

        static async ValueTask<object?> ThenAsync(Task authorized, A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken ct)
        {
            await authorized.ConfigureAwait(false);
            return await next(context, request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Authorizes <paramref name="call"/> for <paramref name="operation"/>; throws when it is refused.</summary>
    public Task RequireAsync(A2AOperationDescriptor operation, ServerCallContext call, object request, CancellationToken cancellationToken) =>
        handler.RequireAsync(options, call, operation, request, TaskIdOf(request), cancellationToken);

    /// <summary>The task a request names, if any: a denial of such a call is reported as TaskNotFound.</summary>
    internal static string? TaskIdOf(object request) => request switch
    {
        SendMessageRequest r => r.Message?.TaskId,
        GetTaskRequest r => r.Id,
        CancelTaskRequest r => r.Id,
        SubscribeToTaskRequest r => r.Id,
        TaskPushNotificationConfig r => r.TaskId,
        GetTaskPushNotificationConfigRequest r => r.TaskId,
        ListTaskPushNotificationConfigsRequest r => r.TaskId,
        DeleteTaskPushNotificationConfigRequest r => r.TaskId,
        _ => null, // ListTasks, GetExtendedAgentCard, extension operations
    };
}
