using System.Text.Json;
using A2A.Deletion;
using A2A.Json;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.Retention;

/// <summary>
///     The task-deletion extension operation (<see cref="TaskDeletion" />), registered by
///     <see cref="A2AServerBuilder.AddTaskDeletion" />. It runs through the operation pipeline like any extension operation
///     (authorization, the activation of <see cref="TaskDeletion.ExtensionUri" />), then deletes the caller's own task with
///     <see cref="A2ATaskAdmin" />.
/// </summary>
/// <remarks>
///     The admin and the auditor are resolved at the first call, not when the operation is built: the card provider and the
///     authorization handler need the registered operations, and the admin needs the execution engine, which needs the card
///     provider. Resolving them here would be a dependency cycle.
/// </remarks>
internal sealed class TaskDeletionOperation(IServiceProvider services)
{
    private static readonly A2AOperationDescriptor Descriptor = A2AOperationDescriptor.Extension(TaskDeletion.Method);
    private A2ATaskAdmin? _admin;
    private A2AAuditor? _audit;

    private A2ATaskAdmin Admin => _admin ??= services.GetRequiredService<A2ATaskAdmin>();
    private A2AAuditor Audit => _audit ??= services.GetService<A2AAuditor>() ?? A2AAuditor.None;

    /// <summary>
    ///     The registration. Allowed by the default authorization handler (the owner check is the task key), and task-scoped:
    ///     a denial by a custom handler is TaskNotFound.
    /// </summary>
    public A2AExtensionOperation Create()
    {
        return new A2AExtensionOperation(TaskDeletion.Method, TaskDeletion.ExtensionUri, HandleAsync, Allow: true)
        {
            TaskIdMember = TaskDeletion.IdParam
        };
    }

    private async ValueTask<JsonElement> HandleAsync(A2AOperationContext context, JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var call = context.Call;
        var taskId = parameters.ValueKind == JsonValueKind.Object
                     && parameters.TryGetProperty(TaskDeletion.IdParam, out var id)
                     && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        if (string.IsNullOrEmpty(taskId))
            throw A2AException.InvalidParams($"'{TaskDeletion.IdParam}' (the task id) is required");
        // Also with AllowAnonymous: anonymous callers share one principal, so none of them owns a task alone.
        if (!call.IsAuthenticated)
        {
            await OperationAuthorization.DeniedAsync(Audit, call, Descriptor, taskId, "Unauthenticated")
                .ConfigureAwait(false);
            throw new A2AException(A2AErrorCode.Unauthenticated);
        }

        await Admin.DeleteOwnTaskAsync(call, taskId, cancellationToken).ConfigureAwait(false);
        return A2AJson.EmptyObject;
    }
}
