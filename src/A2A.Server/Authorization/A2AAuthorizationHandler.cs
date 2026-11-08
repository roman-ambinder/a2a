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
    /// <see cref="A2AOperationDescriptor.Kind"/> and <see cref="A2AOperationDescriptor.Name"/>. For an extension operation with an
    /// <see cref="A2AExtensionOperation.AuthorizationPolicy"/>, <c>call.Items[</c><see cref="A2AExtensionOperation.PolicySatisfiedItem"/><c>]</c>
    /// is <c>true</c> when the HTTP binding evaluated the policy and it succeeded. (The handler registered by default also allows
    /// an extension operation registered with <see cref="A2AExtensionOperation.Allow"/>, or whose policy succeeded.)
    /// </summary>
    /// <param name="call">The caller, with its tenant.</param>
    /// <param name="operation">The operation: its kind, and its name (the registered name for an extension operation).</param>
    /// <param name="request">The request object (for example a <see cref="GetTaskRequest"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public virtual ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(operation.Kind != A2AOperation.Extension);
}

/// <summary>
/// Default handler: allows every built-in operation. Allows an extension operation only when it was registered with
/// <see cref="A2AExtensionOperation.Allow"/> or its <see cref="A2AExtensionOperation.AuthorizationPolicy"/> succeeded (see
/// <see cref="A2AExtensionOperation.PolicySatisfiedItem"/>); denies every other extension operation (fail closed).
/// </summary>
internal sealed class AllowAllAuthorizationHandler(IEnumerable<A2AExtensionOperation> extensions) : A2AAuthorizationHandler
{
    private readonly HashSet<string> _allowed = extensions.Where(e => e.Allow).Select(e => e.Method).ToHashSet(StringComparer.Ordinal);

    public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(operation.Kind != A2AOperation.Extension
            || _allowed.Contains(operation.Name)
            || (call.Items.TryGetValue(A2AExtensionOperation.PolicySatisfiedItem, out var satisfied) && satisfied is true));
}
