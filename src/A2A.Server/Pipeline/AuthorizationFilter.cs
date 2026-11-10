using A2A.Server.Audit;
using A2A.Server.Authorization;

namespace A2A.Server.Pipeline;

/// <summary>
///     The built-in first step of the operation pipeline: <see cref="OperationAuthorization.RequireAsync" /> (the
///     anonymous
///     backstop, then the <see cref="A2AAuthorizationHandler" />, then the skill check of a send when
///     <see cref="A2AServerOptions.SkillAuthorization" /> is on). A denial of a call whose request names a task looks like
///     a
///     missing task; any other denial is <see cref="A2AErrorCode.PermissionDenied" />.
/// </summary>
internal sealed class AuthorizationFilter(
    A2AAuthorizationHandler handler,
    A2AServerOptions options,
    SkillAuthorizer skills,
    A2AAuditor audit) : IA2AOperationFilter
{
    public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next,
        CancellationToken cancellationToken)
    {
        var authorized = RequireAsync(context.Operation, context.Call, request, cancellationToken);
        return authorized.IsCompletedSuccessfully
            ? next(context, request, cancellationToken)
            : ThenAsync(authorized, context, request, next, cancellationToken);

        static async ValueTask<object?> ThenAsync(Task authorized, A2AOperationContext context, object request,
            A2AOperationDelegate next, CancellationToken ct)
        {
            await authorized.ConfigureAwait(false);
            return await next(context, request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Authorizes <paramref name="call" /> for <paramref name="operation" />; throws when it is refused.</summary>
    public Task RequireAsync(A2AOperationDescriptor operation, ServerCallContext call, object request,
        CancellationToken cancellationToken)
    {
        var taskId = TaskIdOf(request);
        var authorized = handler.RequireAsync(options, call, operation, request, taskId, audit, cancellationToken);
        // Skill authorization off, or not a send: no extra work.
        return skills.AppliesTo(operation)
            ? ThenSkillAsync(authorized, operation, call, request, taskId, cancellationToken)
            : authorized;
    }

    private async Task ThenSkillAsync(Task authorized, A2AOperationDescriptor operation, ServerCallContext call,
        object request, string? taskId, CancellationToken ct)
    {
        await authorized.ConfigureAwait(false);
        await skills.RequireAsync(operation, call, request, taskId, ct).ConfigureAwait(false);
    }

    /// <summary>The task a request names, if any: a denial of such a call is reported as TaskNotFound.</summary>
    internal static string? TaskIdOf(object request)
    {
        return request switch
        {
            SendMessageRequest r => r.Message?.TaskId,
            GetTaskRequest r => r.Id,
            CancelTaskRequest r => r.Id,
            SubscribeToTaskRequest r => r.Id,
            TaskPushNotificationConfig r => r.TaskId,
            GetTaskPushNotificationConfigRequest r => r.TaskId,
            ListTaskPushNotificationConfigsRequest r => r.TaskId,
            DeleteTaskPushNotificationConfigRequest r => r.TaskId,
            _ => null // ListTasks, GetExtendedAgentCard, extension operations
        };
    }
}