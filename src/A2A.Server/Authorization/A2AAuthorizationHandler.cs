using A2A.Server.Pipeline;

namespace A2A.Server.Authorization;

/// <summary>
/// Optional extra authorization on top of owner-keyed storage. Called at the top of every operation, on every binding, after
/// the tenant is known, as the built-in first step of the <see cref="IA2AOperationFilter"/> pipeline. Return false to deny.
/// </summary>
/// <remarks>
/// A denied task-scoped operation (and a <c>SendMessage</c> that names a task) is reported as <c>TaskNotFound</c>, so a
/// denial never confirms that a task exists. A denied <c>SendMessage</c> without a task id, <c>ListTasks</c>,
/// <c>GetExtendedAgentCard</c> or extension operation is reported as <c>PermissionDenied</c> (HTTP 403).
/// Open streams are authorized again every <see cref="A2ALimits.StreamReauthInterval"/>, with the same operation and request.
/// </remarks>
public abstract class A2AAuthorizationHandler
{
    /// <summary>
    /// Decides whether <paramref name="call"/> may perform <paramref name="operation"/>. The default allows every built-in
    /// operation and denies extension operations (<see cref="A2AOperation.Extension"/>): override it to decide by
    /// <see cref="A2AOperationDescriptor.Kind"/> and <see cref="A2AOperationDescriptor.Name"/>.
    /// </summary>
    /// <param name="call">The caller, with its tenant.</param>
    /// <param name="operation">The operation: its kind, and its name (the registered name for an extension operation).</param>
    /// <param name="request">The request object (for example a <see cref="GetTaskRequest"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public virtual ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(operation.Kind != A2AOperation.Extension);
}

/// <summary>Default handler: allows every built-in operation, denies extension operations (fail closed).</summary>
internal sealed class AllowAllAuthorizationHandler : A2AAuthorizationHandler;
