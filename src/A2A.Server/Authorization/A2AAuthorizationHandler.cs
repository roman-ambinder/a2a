namespace A2A.Server.Authorization;

/// <summary>
/// Optional extra authorization on top of owner-keyed storage. Called at the top of every operation, on both bindings,
/// after the tenant is known. Return false to deny.
/// </summary>
/// <remarks>
/// A denied task-scoped operation (and a <c>SendMessage</c> that names a task) is reported as <c>TaskNotFound</c>, so a
/// denial never confirms that a task exists. A denied <c>SendMessage</c> without a task id, <c>ListTasks</c> or
/// <c>GetExtendedAgentCard</c> is reported as <c>PermissionDenied</c> (HTTP 403).
/// </remarks>
public abstract class A2AAuthorizationHandler
{
    /// <summary>Decides whether <paramref name="call"/> may perform <paramref name="op"/>. The default allows everything.</summary>
    /// <param name="call">The caller, with its tenant.</param>
    /// <param name="op">The operation.</param>
    /// <param name="request">The request object (for example a <see cref="GetTaskRequest"/>).</param>
    /// <param name="ct">Cancellation.</param>
    public virtual ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) =>
        ValueTask.FromResult(true);
}

/// <summary>Default handler: allows every call.</summary>
internal sealed class AllowAllAuthorizationHandler : A2AAuthorizationHandler;
