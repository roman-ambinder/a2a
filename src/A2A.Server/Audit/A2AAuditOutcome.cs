namespace A2A.Server.Audit;

/// <summary>How far the audited action got. See <see cref="A2AAuditEvent.Outcome" />.</summary>
public enum A2AAuditOutcome
{
    /// <summary>
    ///     Written before the change, only with <see cref="A2AAuditOptions.FailClosed" />. The final row of the same action
    ///     has the
    ///     same <see cref="A2AAuditEvent.Id" />.
    /// </summary>
    Attempted,

    /// <summary>The action was done.</summary>
    Succeeded,

    /// <summary>The call was refused (<see cref="A2AAuditAction.AccessDenied" />).</summary>
    Denied,

    /// <summary>The action was tried and did not happen. <see cref="A2AAuditEvent.Reason" /> says why.</summary>
    Failed
}