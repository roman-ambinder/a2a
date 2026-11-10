using A2A.Diagnostics;
using A2A.Server.Audit;
using A2A.Server.Pipeline;
using A2A.Server.Telemetry;

namespace A2A.Server.Authorization;

/// <summary>
///     The authorization step every operation starts with: run by <see cref="AuthorizationFilter" />, and by the task
///     streamer when an idempotent stream retry resumes its task.
/// </summary>
internal static class OperationAuthorization
{
    /// <summary>
    ///     Refuses anonymous callers unless <see cref="A2AServerOptions.AllowAnonymous" /> is set (so direct callers that skip
    ///     <see cref="A2ARequestHandler.ValidateCall" /> are covered), then asks <paramref name="handler" /> about
    ///     <paramref name="operation" />.
    ///     A denial of a task-scoped operation looks like a missing task; any other denial is
    ///     <see cref="A2AErrorCode.PermissionDenied" />.
    ///     Both refusals are security events; a denial is counted once, whichever error the caller gets.
    ///     Every denial is written to the audit trail first, with its true reason (<c>Unauthenticated</c> or
    ///     <c>PermissionDenied</c>), even
    ///     when the caller is told TaskNotFound. A sink failure never changes the refusal.
    /// </summary>
    public static async Task RequireAsync(this A2AAuthorizationHandler handler, A2AServerOptions options,
        ServerCallContext call,
        A2AOperationDescriptor operation, object request, string? taskId, A2AAuditor audit,
        CancellationToken cancellationToken)
    {
        if (!options.AllowAnonymous && !call.IsAuthenticated)
        {
            SecurityEventLog.Record(options, A2ATelemetry.SecurityEventNames.Unauthenticated, operation.Name, call,
                taskId);
            await DeniedAsync(audit, call, operation, taskId, "Unauthenticated").ConfigureAwait(false);
            throw new A2AException(A2AErrorCode.Unauthenticated);
        }

        if (await handler.AuthorizeAsync(call, operation, request, cancellationToken).ConfigureAwait(false)) return;
        SecurityEventLog.Record(options, A2ATelemetry.SecurityEventNames.PermissionDenied, operation.Name, call,
            string.IsNullOrEmpty(taskId) ? null : taskId);
        await DeniedAsync(audit, call, operation, taskId, "PermissionDenied").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(taskId)) throw A2AException.TaskNotFound(taskId);
        throw new A2AException(A2AErrorCode.PermissionDenied);
    }

    /// <summary>Writes the denial to the audit trail. Never throws.</summary>
    internal static ValueTask DeniedAsync(A2AAuditor audit, ServerCallContext call, A2AOperationDescriptor operation,
        string? taskId, string reason)
    {
        return audit.RecordAsync(
            audit.For(A2AAuditAction.AccessDenied, call, string.IsNullOrEmpty(taskId) ? null : taskId)
                with
                {
                    Outcome = A2AAuditOutcome.Denied, Operation = operation.Name, Reason = reason
                });
    }
}